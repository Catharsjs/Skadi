#include "VideoEngine.h"

#include <Windows.h>
#include <codecapi.h>
#include <icodecapi.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>
#include <roapi.h>
#include <wrl/client.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/base.h>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <filesystem>
#include <limits>
#include <sstream>
#include <thread>

using Microsoft::WRL::ComPtr;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

namespace EventCaptureNative
{
    EncodedStorageFile::~EncodedStorageFile()
    {
        std::error_code error;
        std::filesystem::remove(std::filesystem::path(path), error);
    }

    namespace
    {
        constexpr int64_t TicksPerSecond = 10'000'000;

        void ThrowIfFailed(HRESULT result)
        {
            if (FAILED(result)) winrt::throw_hresult(result);
        }

        std::wstring HResultMessage(HRESULT result)
        {
            wchar_t* message = nullptr;
            const DWORD size = FormatMessageW(
                FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                nullptr,
                static_cast<DWORD>(result),
                0,
                reinterpret_cast<wchar_t*>(&message),
                0,
                nullptr);
            std::wstring value = size > 0 && message != nullptr ? std::wstring(message, size) : L"Unknown native error";
            if (message != nullptr) LocalFree(message);
            return value;
        }

        IDirect3DDevice CreateWinRtDevice(ID3D11Device* device)
        {
            ComPtr<IDXGIDevice> dxgiDevice;
            ThrowIfFailed(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice)));
            ComPtr<IInspectable> inspectable;
            ThrowIfFailed(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), &inspectable));
            IDirect3DDevice result{ nullptr };
            ThrowIfFailed(inspectable->QueryInterface(winrt::guid_of<IDirect3DDevice>(), winrt::put_abi(result)));
            return result;
        }

        GraphicsCaptureItem CreateCaptureItem(const EcVideoConfig& config)
        {
            const auto interop = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
            GraphicsCaptureItem item{ nullptr };
            HRESULT result = config.targetKind == EcTargetKind::Window
                ? interop->CreateForWindow(static_cast<HWND>(config.targetHandle), winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item))
                : interop->CreateForMonitor(static_cast<HMONITOR>(config.targetHandle), winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item));
            ThrowIfFailed(result);
            return item;
        }

        void RequestBorderlessCaptureAccess()
        {
            std::exception_ptr failure;

            std::thread worker([&failure]
                {
                    try
                    {
                        winrt::init_apartment(winrt::apartment_type::multi_threaded);
                        GraphicsCaptureAccess::RequestAccessAsync(GraphicsCaptureAccessKind::Borderless).get();
                    }
                    catch (...)
                    {
                        failure = std::current_exception();
                    }
                });

            worker.join();

            if (failure)
                std::rethrow_exception(failure);
        }

        ComPtr<ID3D11Texture2D> GetTexture(const IDirect3DSurface& surface)
        {
            const auto access = surface.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            ComPtr<ID3D11Texture2D> texture;
            ThrowIfFailed(access->GetInterface(IID_PPV_ARGS(&texture)));
            return texture;
        }

        void SetVariantUInt32(ICodecAPI* codec, const GUID& key, uint32_t value)
        {
            if (codec == nullptr) return;
            VARIANT variant{};
            VariantInit(&variant);
            variant.vt = VT_UI4;
            variant.ulVal = value;
            codec->SetValue(&key, &variant);
            VariantClear(&variant);
        }

        void SetVariantBool(ICodecAPI* codec, const GUID& key, bool value)
        {
            if (codec == nullptr) return;
            VARIANT variant{};
            VariantInit(&variant);
            variant.vt = VT_BOOL;
            variant.boolVal = value ? VARIANT_TRUE : VARIANT_FALSE;
            codec->SetValue(&key, &variant);
            VariantClear(&variant);
        }

        void ConfigureSdrBt709ColorMetadata(IMFMediaType* mediaType)
        {
            if (mediaType == nullptr) return;
            mediaType->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709);
            mediaType->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709);
            mediaType->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
            mediaType->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
        }

        bool HasAnnexBPrefix(const uint8_t* data, size_t size)
        {
            return size >= 4 && data[0] == 0 && data[1] == 0 &&
                ((data[2] == 1) || (data[2] == 0 && data[3] == 1));
        }

        std::vector<uint8_t> ToAnnexB(const uint8_t* data, size_t size)
        {
            if (HasAnnexBPrefix(data, size)) return { data, data + size };
            std::vector<uint8_t> result;
            result.reserve(size + 32);
            size_t offset = 0;
            while (offset + 4 <= size)
            {
                const uint32_t length =
                    (static_cast<uint32_t>(data[offset]) << 24) |
                    (static_cast<uint32_t>(data[offset + 1]) << 16) |
                    (static_cast<uint32_t>(data[offset + 2]) << 8) |
                    static_cast<uint32_t>(data[offset + 3]);
                offset += 4;
                if (length == 0 || length > size - offset) return { data, data + size };
                constexpr uint8_t startCode[] = { 0, 0, 0, 1 };
                result.insert(result.end(), std::begin(startCode), std::end(startCode));
                result.insert(result.end(), data + offset, data + offset + length);
                offset += length;
            }
            return result;
        }
    }

    class VideoEngine::Implementation final
    {
    public:
        explicit Implementation(const EcVideoConfig& config) : config_(config)
        {
            if (config_.outputWidth == 0 || config_.outputHeight == 0 || config_.framesPerSecond == 0 ||
                config_.bitrateKbps == 0 || config_.replaySeconds == 0 || config_.targetHandle == nullptr)
            {
                throw std::invalid_argument("Invalid video configuration");
            }
            wchar_t temporaryPath[MAX_PATH]{};
            if (GetTempPathW(MAX_PATH, temporaryPath) == 0) throw std::runtime_error("Could not resolve the temporary directory");
            spoolDirectory_ = std::filesystem::path(temporaryPath) /
                (L"Skadi-Native-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
            std::filesystem::create_directories(spoolDirectory_);
        }

        ~Implementation()
        {
            Stop();
            std::error_code error;
            std::filesystem::remove_all(spoolDirectory_, error);
        }

        EcResult Start()
        {
            std::scoped_lock stateLock(stateMutex_);
            if (running_) return EcResult::Ok;
            try
            {
                startupStage_ = L"COM initialization";
                const HRESULT apartmentResult = RoInitialize(RO_INIT_MULTITHREADED);
                if (apartmentResult == S_OK || apartmentResult == S_FALSE) apartmentInitialized_ = true;
                else if (apartmentResult != RPC_E_CHANGED_MODE) ThrowIfFailed(apartmentResult);
                startupStage_ = L"Media Foundation startup";
                ThrowIfFailed(MFStartup(MF_VERSION, MFSTARTUP_FULL));
                mediaFoundationStarted_ = true;
                startupStage_ = L"D3D11 device creation";
                CreateDevice();
                startupStage_ = L"Windows Graphics Capture creation";
                CreateCapture();
                startupStage_ = L"H.264 encoder creation";
                CreateEncoder();
                encodeClockStart_ = std::chrono::steady_clock::now();
                lastPacketTimestamp100ns_ = -1;
                submittedFrames_.store(0);
                running_ = true;
                captureSession_.StartCapture();
                encoderThread_ = std::thread([this] { EncodeLoop(); });
                return EcResult::Ok;
            }
            catch (const winrt::hresult_error& error)
            {
                std::wstringstream message;
                message << startupStage_ << L" failed with HRESULT 0x" << std::hex
                    << static_cast<uint32_t>(error.code()) << L": " << error.message().c_str();
                SetError(message.str());
                StopCore();
                return EcResult::NativeFailure;
            }
            catch (const std::exception& error)
            {
                SetError(std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                StopCore();
                return EcResult::NativeFailure;
            }
        }

        EcResult Stop()
        {
            std::scoped_lock stateLock(stateMutex_);
            StopCore();
            return EcResult::Ok;
        }

        EcResult SaveReplay(const wchar_t* path, uint32_t seconds, EcExportResult& result)
        {
            if (path == nullptr || seconds == 0) return EcResult::InvalidArgument;
            std::vector<EncodedFrame> frames;
            {
                std::scoped_lock lock(packetMutex_);
                if (packets_.empty()) return EcResult::InvalidState;
                const size_t requestedFrames = static_cast<size_t>(seconds) * config_.framesPerSecond;
                size_t end = packets_.size();
                if (end > 1)
                {
                    for (size_t index = end - 1; index > 0; --index)
                    {
                        if (packets_[index].keyFrame)
                        {
                            end = index;
                            break;
                        }
                    }
                }
                if (end == 0) end = packets_.size();
                size_t start = end > requestedFrames ? end - requestedFrames : 0;
                while (start < end && !packets_[start].keyFrame) ++start;
                if (start >= end) return EcResult::InvalidState;
                frames.assign(packets_.begin() + static_cast<std::ptrdiff_t>(start), packets_.begin() + static_cast<std::ptrdiff_t>(end));
            }
            if (!WriteFrames(path, frames)) return EcResult::NativeFailure;
            result.startTimestamp100ns = frames.front().timestamp100ns;
            result.endTimestamp100ns = frames.back().timestamp100ns + frames.back().duration100ns;
            result.frameCount = frames.size();
            return EcResult::Ok;
        }

        EcResult StartRecording(const wchar_t* path)
        {
            if (path == nullptr || *path == L'\0') return EcResult::InvalidArgument;
            std::scoped_lock lock(packetMutex_);
            if (recording_ || recordingPending_) return EcResult::InvalidState;
            recordingPath_ = path;
            recordingPending_ = true;
            ForceKeyFrame();
            return EcResult::Ok;
        }

        EcResult StopRecording(EcExportResult& result)
        {
            std::scoped_lock lock(packetMutex_);
            if (!recording_ && !recordingPending_) return EcResult::InvalidState;
            recordingPending_ = false;
            recording_ = false;
            if (recordingStream_.is_open()) recordingStream_.close();
            result.startTimestamp100ns = recordingStart100ns_;
            result.endTimestamp100ns = recordingEnd100ns_;
            result.frameCount = recordingFrameCount_;
            recordingPath_.clear();
            return recordingFrameCount_ > 0 ? EcResult::Ok : EcResult::InvalidState;
        }

        EcResult GetStats(EcVideoStats& stats) const
        {
            stats.capturedFrames = capturedFrames_.load();
            stats.encodedFrames = encodedFrames_.load();
            stats.droppedFrames = droppedFrames_.load();
            stats.bufferedBytes = bufferedBytes_.load();
            std::scoped_lock lock(packetMutex_);
            stats.bufferedFrames = packets_.size();
            stats.isRunning = running_ ? 1 : 0;
            stats.isRecording = recording_ || recordingPending_ ? 1 : 0;
            return EcResult::Ok;
        }

        std::wstring LastError() const
        {
            std::scoped_lock lock(errorMutex_);
            return lastError_;
        }

    private:
        void CreateDevice()
        {
            const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
            D3D_FEATURE_LEVEL featureLevel{};
            ThrowIfFailed(D3D11CreateDevice(
                nullptr,
                D3D_DRIVER_TYPE_HARDWARE,
                nullptr,
                flags,
                nullptr,
                0,
                D3D11_SDK_VERSION,
                &device_,
                &featureLevel,
                &context_));
            ComPtr<ID3D10Multithread> multithread;
            ThrowIfFailed(context_.As(&multithread));
            multithread->SetMultithreadProtected(TRUE);
            winRtDevice_ = CreateWinRtDevice(device_.Get());
            ThrowIfFailed(device_.As(&videoDevice_));
            ThrowIfFailed(context_.As(&videoContext_));
            ThrowIfFailed(MFCreateDXGIDeviceManager(&deviceManagerToken_, &deviceManager_));
            ThrowIfFailed(deviceManager_->ResetDevice(device_.Get(), deviceManagerToken_));
        }

        void CreateCapture()
        {
            captureItem_ = CreateCaptureItem(config_);
            const auto size = captureItem_.Size();
            if (size.Width <= 0 || size.Height <= 0) throw std::runtime_error("Capture target has an invalid size");
            CreateLatestTexture(static_cast<uint32_t>(size.Width), static_cast<uint32_t>(size.Height));
            framePool_ = Direct3D11CaptureFramePool::CreateFreeThreaded(
                winRtDevice_,
                DirectXPixelFormat::B8G8R8A8UIntNormalized,
                2,
                size);
            frameToken_ = framePool_.FrameArrived([this](const Direct3D11CaptureFramePool& pool, const winrt::Windows::Foundation::IInspectable&)
                {
                    try
                    {
                        const auto frame = pool.TryGetNextFrame();
                        if (frame == nullptr) return;
                        const auto texture = GetTexture(frame.Surface());
                        const auto contentSize = frame.ContentSize();
                        if (contentSize.Width <= 0 || contentSize.Height <= 0) return;
                        std::scoped_lock lock(textureMutex_);
                        D3D11_TEXTURE2D_DESC description{};
                        texture->GetDesc(&description);
                        if (description.Width != sourceWidth_ || description.Height != sourceHeight_)
                        {
                            CreateLatestTexture(description.Width, description.Height);
                        }
                        context_->CopyResource(latestTexture_.Get(), texture.Get());
                        hasTexture_ = true;
                        ++textureVersion_;
                        capturedFrames_.fetch_add(1);
                        frameCondition_.notify_one();
                    }
                    catch (...)
                    {
                        droppedFrames_.fetch_add(1);
                    }
                });
            captureSession_ = framePool_.CreateCaptureSession(captureItem_);
            captureSession_.IsCursorCaptureEnabled(false);
            try
            {
                RequestBorderlessCaptureAccess();
            }
            catch (...) {}

            try
            {
                captureSession_.IsBorderRequired(false);
            }
            catch (...) {}
        }

        void CreateLatestTexture(uint32_t width, uint32_t height)
        {
            D3D11_TEXTURE2D_DESC description{};
            description.Width = width;
            description.Height = height;
            description.MipLevels = 1;
            description.ArraySize = 1;
            description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.Usage = D3D11_USAGE_DEFAULT;
            description.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
            latestTexture_.Reset();
            ThrowIfFailed(device_->CreateTexture2D(&description, nullptr, &latestTexture_));
            sourceWidth_ = width;
            sourceHeight_ = height;
            CreateVideoProcessor();
        }

        void CreateVideoProcessor()
        {
            D3D11_TEXTURE2D_DESC outputDescription{};
            outputDescription.Width = config_.outputWidth;
            outputDescription.Height = config_.outputHeight;
            outputDescription.MipLevels = 1;
            outputDescription.ArraySize = 1;
            outputDescription.Format = DXGI_FORMAT_NV12;
            outputDescription.SampleDesc.Count = 1;
            outputDescription.Usage = D3D11_USAGE_DEFAULT;
            outputDescription.BindFlags = D3D11_BIND_RENDER_TARGET;
            encoderTexture_.Reset();
            ThrowIfFailed(device_->CreateTexture2D(&outputDescription, nullptr, &encoderTexture_));

            D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
            content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            content.InputWidth = sourceWidth_;
            content.InputHeight = sourceHeight_;
            content.OutputWidth = config_.outputWidth;
            content.OutputHeight = config_.outputHeight;
            content.InputFrameRate = { config_.framesPerSecond, 1 };
            content.OutputFrameRate = { config_.framesPerSecond, 1 };
            content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            processorEnumerator_.Reset();
            processor_.Reset();
            processorInput_.Reset();
            processorOutput_.Reset();
            ThrowIfFailed(videoDevice_->CreateVideoProcessorEnumerator(&content, &processorEnumerator_));
            ThrowIfFailed(videoDevice_->CreateVideoProcessor(processorEnumerator_.Get(), 0, &processor_));
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputView{};
            inputView.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
            inputView.Texture2D.MipSlice = 0;
            inputView.Texture2D.ArraySlice = 0;
            ThrowIfFailed(videoDevice_->CreateVideoProcessorInputView(latestTexture_.Get(), processorEnumerator_.Get(), &inputView, &processorInput_));
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputView{};
            outputView.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
            outputView.Texture2D.MipSlice = 0;
            ThrowIfFailed(videoDevice_->CreateVideoProcessorOutputView(encoderTexture_.Get(), processorEnumerator_.Get(), &outputView, &processorOutput_));
            const RECT sourceRect{ 0, 0, static_cast<LONG>(sourceWidth_), static_cast<LONG>(sourceHeight_) };
            const RECT outputRect{ 0, 0, static_cast<LONG>(config_.outputWidth), static_cast<LONG>(config_.outputHeight) };
            videoContext_->VideoProcessorSetStreamSourceRect(processor_.Get(), 0, TRUE, &sourceRect);
            videoContext_->VideoProcessorSetStreamDestRect(processor_.Get(), 0, TRUE, &outputRect);
            videoContext_->VideoProcessorSetOutputTargetRect(processor_.Get(), TRUE, &outputRect);

            D3D11_VIDEO_PROCESSOR_COLOR_SPACE inputColorSpace{};
            inputColorSpace.RGB_Range = 0;
            inputColorSpace.YCbCr_Matrix = 1;
            inputColorSpace.Nominal_Range = 2;

            D3D11_VIDEO_PROCESSOR_COLOR_SPACE outputColorSpace{};
            outputColorSpace.RGB_Range = 1;
            outputColorSpace.YCbCr_Matrix = 1;
            outputColorSpace.Nominal_Range = 1;

            videoContext_->VideoProcessorSetStreamColorSpace(processor_.Get(), 0, &inputColorSpace);
            videoContext_->VideoProcessorSetOutputColorSpace(processor_.Get(), &outputColorSpace);
        }

        void CreateEncoder()
        {
            startupStage_ = L"H.264 MFT enumeration";
            MFT_REGISTER_TYPE_INFO inputInfo{ MFMediaType_Video, MFVideoFormat_NV12 };
            MFT_REGISTER_TYPE_INFO outputInfo{ MFMediaType_Video, MFVideoFormat_H264 };
            IMFActivate** activates = nullptr;
            UINT32 count = 0;
            HRESULT result = MFTEnumEx(
                MFT_CATEGORY_VIDEO_ENCODER,
                MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                &inputInfo,
                &outputInfo,
                &activates,
                &count);
            if (FAILED(result) || count == 0)
            {
                if (activates != nullptr) CoTaskMemFree(activates);
                activates = nullptr;
                count = 0;
                ThrowIfFailed(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_ALL, &inputInfo, &outputInfo, &activates, &count));
            }
            if (count == 0 || activates == nullptr) throw std::runtime_error("No H.264 Media Foundation encoder is available");
            startupStage_ = L"H.264 MFT activation";
            HRESULT activationResult = E_FAIL;
            for (UINT32 index = 0; index < count; ++index)
            {
                activationResult = activates[index]->ActivateObject(IID_PPV_ARGS(&encoder_));
                if (SUCCEEDED(activationResult)) break;
                encoder_.Reset();
            }
            for (UINT32 index = 0; index < count; ++index) activates[index]->Release();
            CoTaskMemFree(activates);
            ThrowIfFailed(activationResult);

            ComPtr<IMFAttributes> attributes;
            if (SUCCEEDED(encoder_->GetAttributes(&attributes)))
            {
                attributes->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
                attributes->SetUINT32(MF_SA_D3D11_AWARE, TRUE);
                UINT32 asynchronous = FALSE;
                if (SUCCEEDED(attributes->GetUINT32(MF_TRANSFORM_ASYNC, &asynchronous))) asyncEncoder_ = asynchronous != FALSE;
            }
            if (asyncEncoder_) ThrowIfFailed(encoder_.As(&eventGenerator_));
            startupStage_ = L"H.264 MFT D3D manager assignment";
            ThrowIfFailed(encoder_->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(deviceManager_.Get())));

            startupStage_ = L"H.264 MFT output media type";
            ComPtr<IMFMediaType> outputType;
            ThrowIfFailed(MFCreateMediaType(&outputType));
            ThrowIfFailed(outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
            ThrowIfFailed(outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
            ThrowIfFailed(outputType->SetUINT32(MF_MT_AVG_BITRATE, config_.bitrateKbps * 1000));
            ThrowIfFailed(outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
            ThrowIfFailed(MFSetAttributeSize(outputType.Get(), MF_MT_FRAME_SIZE, config_.outputWidth, config_.outputHeight));
            ThrowIfFailed(MFSetAttributeRatio(outputType.Get(), MF_MT_FRAME_RATE, config_.framesPerSecond, 1));
            ThrowIfFailed(MFSetAttributeRatio(outputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
            ConfigureSdrBt709ColorMetadata(outputType.Get());
            ThrowIfFailed(encoder_->SetOutputType(0, outputType.Get(), 0));

            startupStage_ = L"H.264 MFT input media type";
            ComPtr<IMFMediaType> inputType;
            ThrowIfFailed(MFCreateMediaType(&inputType));
            ThrowIfFailed(inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
            ThrowIfFailed(inputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
            ThrowIfFailed(inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
            ThrowIfFailed(MFSetAttributeSize(inputType.Get(), MF_MT_FRAME_SIZE, config_.outputWidth, config_.outputHeight));
            ThrowIfFailed(MFSetAttributeRatio(inputType.Get(), MF_MT_FRAME_RATE, config_.framesPerSecond, 1));
            ThrowIfFailed(MFSetAttributeRatio(inputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
            ConfigureSdrBt709ColorMetadata(inputType.Get());
            ThrowIfFailed(encoder_->SetInputType(0, inputType.Get(), 0));

            startupStage_ = L"H.264 MFT codec configuration";
            encoder_.As(&codecApi_);
            SetVariantUInt32(codecApi_.Get(), CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_CBR);
            SetVariantUInt32(codecApi_.Get(), CODECAPI_AVEncCommonMeanBitRate, config_.bitrateKbps * 1000);
            SetVariantUInt32(codecApi_.Get(), CODECAPI_AVEncMPVGOPSize, config_.framesPerSecond);
            SetVariantBool(codecApi_.Get(), CODECAPI_AVLowLatencyMode, true);
            startupStage_ = L"H.264 MFT streaming start";
            ThrowIfFailed(encoder_->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0));
            ThrowIfFailed(encoder_->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
            ThrowIfFailed(encoder_->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
        }

        void EncodeLoop()
        {
            const int64_t frameDuration = TicksPerSecond / config_.framesPerSecond;
            auto next = std::chrono::steady_clock::now();
            while (running_)
            {
                next += std::chrono::nanoseconds(frameDuration * 100);
                {
                    std::unique_lock lock(textureMutex_);
                    frameCondition_.wait_until(lock, next, [this] { return hasTexture_ || !running_; });
                    if (!running_) break;
                    if (!hasTexture_)
                    {
                        droppedFrames_.fetch_add(1);
                        continue;
                    }
                    D3D11_VIDEO_PROCESSOR_STREAM stream{};
                    stream.Enable = TRUE;
                    stream.pInputSurface = processorInput_.Get();
                    const HRESULT blit = videoContext_->VideoProcessorBlt(processor_.Get(), processorOutput_.Get(), 0, 1, &stream);
                    if (FAILED(blit))
                    {
                        SetError(HResultMessage(blit));
                        continue;
                    }
                }
                try
                {
                    const uint64_t submittedIndex = submittedFrames_.fetch_add(1);
                    SubmitFrame(static_cast<int64_t>(submittedIndex) * frameDuration, frameDuration);
                    if (asyncEncoder_) PumpEncoderEvents(false, std::chrono::milliseconds(0));
                    else DrainSynchronousEncoder();
                }
                catch (const winrt::hresult_error& error)
                {
                    SetError(error.message().c_str());
                    droppedFrames_.fetch_add(1);
                }
                std::this_thread::sleep_until(next);
            }
            if (encoder_)
            {
                encoder_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
                encoder_->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
                try
                {
                    if (asyncEncoder_) PumpEncoderEvents(false, std::chrono::seconds(2));
                    else DrainSynchronousEncoder();
                }
                catch (...) {}
            }
        }

        void SubmitFrame(int64_t timestamp, int64_t duration)
        {
            ComPtr<IMFMediaBuffer> buffer;
            ThrowIfFailed(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), encoderTexture_.Get(), 0, FALSE, &buffer));
            ComPtr<IMFSample> sample;
            ThrowIfFailed(MFCreateSample(&sample));
            ThrowIfFailed(sample->AddBuffer(buffer.Get()));
            ThrowIfFailed(sample->SetSampleTime(timestamp));
            ThrowIfFailed(sample->SetSampleDuration(duration));
            if (asyncEncoder_)
            {
                if (!PumpEncoderEvents(true, std::chrono::seconds(1))) ThrowIfFailed(MF_E_NOTACCEPTING);
                ThrowIfFailed(encoder_->ProcessInput(0, sample.Get(), 0));
                --inputCredits_;
                return;
            }

            HRESULT result = encoder_->ProcessInput(0, sample.Get(), 0);
            if (result == MF_E_NOTACCEPTING)
            {
                DrainSynchronousEncoder();
                result = encoder_->ProcessInput(0, sample.Get(), 0);
            }
            ThrowIfFailed(result);
        }

        bool PumpEncoderEvents(bool waitForInput, std::chrono::milliseconds timeout)
        {
            if (!asyncEncoder_ || eventGenerator_ == nullptr) return true;
            const auto deadline = std::chrono::steady_clock::now() + timeout;
            for (;;)
            {
                if (waitForInput && inputCredits_ > 0) return true;
                ComPtr<IMFMediaEvent> event;
                const HRESULT eventResult = eventGenerator_->GetEvent(MF_EVENT_FLAG_NO_WAIT, &event);
                if (eventResult == MF_E_NO_EVENTS_AVAILABLE)
                {
                    if (std::chrono::steady_clock::now() >= deadline) return !waitForInput;
                    std::this_thread::sleep_for(std::chrono::milliseconds(1));
                    continue;
                }
                ThrowIfFailed(eventResult);
                MediaEventType type = MEUnknown;
                ThrowIfFailed(event->GetType(&type));
                if (type == METransformNeedInput) ++inputCredits_;
                else if (type == METransformHaveOutput) ProcessSingleOutput();
                else if (type == METransformDrainComplete) return !waitForInput;
                if (!waitForInput && timeout.count() == 0) continue;
            }
        }

        HRESULT ProcessSingleOutput()
        {
            MFT_OUTPUT_STREAM_INFO streamInfo{};
            ThrowIfFailed(encoder_->GetOutputStreamInfo(0, &streamInfo));
            ComPtr<IMFSample> sample;
            if ((streamInfo.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) == 0)
            {
                ComPtr<IMFMediaBuffer> buffer;
                ThrowIfFailed(MFCreateMemoryBuffer(std::max<DWORD>(streamInfo.cbSize, 1024 * 1024), &buffer));
                ThrowIfFailed(MFCreateSample(&sample));
                ThrowIfFailed(sample->AddBuffer(buffer.Get()));
            }
            MFT_OUTPUT_DATA_BUFFER output{};
            output.dwStreamID = 0;
            output.pSample = sample.Get();
            DWORD status = 0;
            const HRESULT result = encoder_->ProcessOutput(0, 1, &output, &status);
            if (output.pEvents != nullptr) output.pEvents->Release();
            if (FAILED(result)) return result;
            if (output.pSample != nullptr && output.pSample != sample.Get()) sample.Attach(output.pSample);
            if (sample != nullptr) ConsumeEncodedSample(sample.Get());
            return result;
        }

        void DrainSynchronousEncoder()
        {
            for (;;)
            {
                const HRESULT result = ProcessSingleOutput();
                if (result == MF_E_TRANSFORM_NEED_MORE_INPUT || result == MF_E_TRANSFORM_ASYNC_LOCKED) break;
                if (result == MF_E_TRANSFORM_STREAM_CHANGE) continue;
                ThrowIfFailed(result);
            }
        }

        void ConsumeEncodedSample(IMFSample* sample)
        {
            ComPtr<IMFMediaBuffer> buffer;
            ThrowIfFailed(sample->ConvertToContiguousBuffer(&buffer));
            BYTE* data = nullptr;
            DWORD maxLength = 0;
            DWORD currentLength = 0;
            ThrowIfFailed(buffer->Lock(&data, &maxLength, &currentLength));
            EncodedFrame frame;
            try
            {
                frame.bytes = ToAnnexB(data, currentLength);
            }
            catch (...)
            {
                buffer->Unlock();
                throw;
            }
            buffer->Unlock();
            LONGLONG timestamp = 0;
            LONGLONG duration = TicksPerSecond / config_.framesPerSecond;
            sample->GetSampleTime(&timestamp);
            sample->GetSampleDuration(&duration);
            UINT32 cleanPoint = FALSE;
            sample->GetUINT32(MFSampleExtension_CleanPoint, &cleanPoint);
            frame.timestamp100ns = timestamp;
            frame.duration100ns = duration;
            frame.keyFrame = cleanPoint != FALSE;
            AddPacket(std::move(frame));
            encodedFrames_.fetch_add(1);
        }

        void AddPacket(EncodedFrame frame)
        {
            std::scoped_lock lock(packetMutex_);
            int64_t timestamp = CurrentTimestamp100ns();
            if (lastPacketTimestamp100ns_ >= 0 && timestamp <= lastPacketTimestamp100ns_) timestamp = lastPacketTimestamp100ns_ + 1;
            frame.duration100ns = lastPacketTimestamp100ns_ >= 0
                ? std::max<int64_t>(1, timestamp - lastPacketTimestamp100ns_)
                : TicksPerSecond / config_.framesPerSecond;
            frame.timestamp100ns = timestamp;
            lastPacketTimestamp100ns_ = timestamp;

            const uint64_t size = frame.bytes.size();
            if (activeSpool_ == nullptr || (frame.keyFrame && activeSpoolFrameCount_ > 0)) RotateSpoolFile();
            const std::streampos position = activeSpoolStream_.tellp();
            if (position < 0) throw std::runtime_error("Could not query the encoded spool offset");
            activeSpoolStream_.write(
                reinterpret_cast<const char*>(frame.bytes.data()),
                static_cast<std::streamsize>(frame.bytes.size()));
            if (!activeSpoolStream_) throw std::runtime_error("Could not write the encoded spool packet");
            frame.storage = activeSpool_;
            frame.storageOffset = static_cast<uint64_t>(position);
            frame.storageLength = static_cast<uint32_t>(frame.bytes.size());
            bufferedBytes_.fetch_add(size);
            packets_.push_back(std::move(frame));
            ++activeSpoolFrameCount_;
            const int64_t cutoff = packets_.back().timestamp100ns - static_cast<int64_t>(config_.replaySeconds + 2) * TicksPerSecond;
            while (packets_.size() > config_.framesPerSecond && packets_.front().timestamp100ns < cutoff)
            {
                bufferedBytes_.fetch_sub(packets_.front().bytes.size());
                packets_.pop_front();
            }

            const EncodedFrame& packet = packets_.back();
            if (recordingPending_ && packet.keyFrame)
            {
                recordingStream_.open(recordingPath_, std::ios::binary | std::ios::trunc);
                if (recordingStream_)
                {
                    recordingPending_ = false;
                    recording_ = true;
                    recordingStart100ns_ = packet.timestamp100ns;
                    recordingEnd100ns_ = packet.timestamp100ns;
                    recordingFrameCount_ = 0;
                }
                else
                {
                    recordingPending_ = false;
                    SetError(L"Could not create the continuous recording stream");
                }
            }
            if (recording_ && recordingStream_)
            {
                recordingStream_.write(reinterpret_cast<const char*>(packet.bytes.data()), static_cast<std::streamsize>(packet.bytes.size()));
                recordingEnd100ns_ = packet.timestamp100ns + packet.duration100ns;
                ++recordingFrameCount_;
            }
            packets_.back().bytes.clear();
            packets_.back().bytes.shrink_to_fit();
        }

        int64_t CurrentTimestamp100ns() const
        {
            const auto elapsed = std::chrono::steady_clock::now() - encodeClockStart_;
            return std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count() / 100;
        }

        void RotateSpoolFile()
        {
            if (activeSpoolStream_.is_open()) activeSpoolStream_.close();
            const std::filesystem::path path = spoolDirectory_ /
                (L"gop-" + std::to_wstring(spoolSequence_++) + L".h264");
            activeSpool_ = std::make_shared<EncodedStorageFile>(path.wstring());
            activeSpoolStream_.open(path, std::ios::binary | std::ios::trunc);
            if (!activeSpoolStream_) throw std::runtime_error("Could not create the encoded spool file");
            activeSpoolFrameCount_ = 0;
        }

        bool WriteFrames(const wchar_t* path, const std::vector<EncodedFrame>& frames)
        {
            std::ofstream stream(std::filesystem::path(path), std::ios::binary | std::ios::trunc);
            if (!stream)
            {
                SetError(L"Could not create replay export stream");
                return false;
            }
            std::wstring currentStoragePath;
            std::ifstream input;
            std::vector<char> buffer;
            for (const auto& frame : frames)
            {
                if (frame.storage == nullptr || frame.storageLength == 0) return false;
                if (frame.storage->path != currentStoragePath)
                {
                    input.close();
                    currentStoragePath = frame.storage->path;
                    input.open(std::filesystem::path(currentStoragePath), std::ios::binary);
                    if (!input) return false;
                }
                buffer.resize(frame.storageLength);
                input.seekg(static_cast<std::streamoff>(frame.storageOffset), std::ios::beg);
                input.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
                if (input.gcount() != static_cast<std::streamsize>(buffer.size())) return false;
                stream.write(buffer.data(), static_cast<std::streamsize>(buffer.size()));
                if (!stream)
                {
                    SetError(L"Could not write replay export stream");
                    return false;
                }
            }
            return true;
        }

        void ForceKeyFrame()
        {
            SetVariantUInt32(codecApi_.Get(), CODECAPI_AVEncVideoForceKeyFrame, 1);
        }

        void StopCore()
        {
            running_ = false;
            frameCondition_.notify_all();
            if (encoderThread_.joinable()) encoderThread_.join();
            if (recordingStream_.is_open()) recordingStream_.close();
            if (activeSpoolStream_.is_open()) activeSpoolStream_.close();
            recording_ = false;
            recordingPending_ = false;
            {
                std::scoped_lock packetLock(packetMutex_);
                packets_.clear();
                activeSpool_.reset();
                bufferedBytes_ = 0;
            }
            try
            {
                if (framePool_ != nullptr) framePool_.FrameArrived(frameToken_);
                if (captureSession_ != nullptr) captureSession_.Close();
                if (framePool_ != nullptr) framePool_.Close();
            }
            catch (...) {}
            captureSession_ = nullptr;
            framePool_ = nullptr;
            captureItem_ = nullptr;
            encoder_.Reset();
            codecApi_.Reset();
            eventGenerator_.Reset();
            processorOutput_.Reset();
            processorInput_.Reset();
            processor_.Reset();
            processorEnumerator_.Reset();
            encoderTexture_.Reset();
            latestTexture_.Reset();
            videoContext_.Reset();
            videoDevice_.Reset();
            context_.Reset();
            device_.Reset();
            deviceManager_.Reset();
            winRtDevice_ = nullptr;
            if (mediaFoundationStarted_)
            {
                MFShutdown();
                mediaFoundationStarted_ = false;
            }
            if (apartmentInitialized_)
            {
                RoUninitialize();
                apartmentInitialized_ = false;
            }
        }

        void SetError(const std::wstring& message) const
        {
            std::scoped_lock lock(errorMutex_);
            lastError_ = message;
        }

        EcVideoConfig config_{};
        mutable std::mutex stateMutex_;
        mutable std::mutex errorMutex_;
        mutable std::wstring lastError_;
        std::atomic_bool running_{ false };
        std::wstring startupStage_;
        bool mediaFoundationStarted_{ false };
        bool apartmentInitialized_{ false };

        ComPtr<ID3D11Device> device_;
        ComPtr<ID3D11DeviceContext> context_;
        ComPtr<ID3D11VideoDevice> videoDevice_;
        ComPtr<ID3D11VideoContext> videoContext_;
        ComPtr<IMFDXGIDeviceManager> deviceManager_;
        UINT deviceManagerToken_{};
        IDirect3DDevice winRtDevice_{ nullptr };
        GraphicsCaptureItem captureItem_{ nullptr };
        Direct3D11CaptureFramePool framePool_{ nullptr };
        GraphicsCaptureSession captureSession_{ nullptr };
        winrt::event_token frameToken_{};

        mutable std::mutex textureMutex_;
        std::condition_variable frameCondition_;
        ComPtr<ID3D11Texture2D> latestTexture_;
        ComPtr<ID3D11Texture2D> encoderTexture_;
        ComPtr<ID3D11VideoProcessorEnumerator> processorEnumerator_;
        ComPtr<ID3D11VideoProcessor> processor_;
        ComPtr<ID3D11VideoProcessorInputView> processorInput_;
        ComPtr<ID3D11VideoProcessorOutputView> processorOutput_;
        uint32_t sourceWidth_{};
        uint32_t sourceHeight_{};
        bool hasTexture_{ false };
        uint64_t textureVersion_{};

        ComPtr<IMFTransform> encoder_;
        ComPtr<ICodecAPI> codecApi_;
        ComPtr<IMFMediaEventGenerator> eventGenerator_;
        bool asyncEncoder_{ false };
        int inputCredits_{};
        std::thread encoderThread_;

        mutable std::mutex packetMutex_;
        std::deque<EncodedFrame> packets_;
        std::wstring recordingPath_;
        std::ofstream recordingStream_;
        std::filesystem::path spoolDirectory_;
        std::shared_ptr<EncodedStorageFile> activeSpool_;
        std::ofstream activeSpoolStream_;
        uint64_t spoolSequence_{};
        uint32_t activeSpoolFrameCount_{};
        bool recordingPending_{ false };
        bool recording_{ false };
        int64_t recordingStart100ns_{};
        int64_t recordingEnd100ns_{};
        int64_t lastPacketTimestamp100ns_{ -1 };
        uint64_t recordingFrameCount_{};
        std::chrono::steady_clock::time_point encodeClockStart_{};

        std::atomic_uint64_t capturedFrames_{};
        std::atomic_uint64_t submittedFrames_{};
        std::atomic_uint64_t encodedFrames_{};
        std::atomic_uint64_t droppedFrames_{};
        std::atomic_uint64_t bufferedBytes_{};
    };

    VideoEngine::VideoEngine(const EcVideoConfig& config) : implementation_(std::make_unique<Implementation>(config)) {}
    VideoEngine::~VideoEngine() = default;
    EcResult VideoEngine::Start() { return implementation_->Start(); }
    EcResult VideoEngine::Stop() { return implementation_->Stop(); }
    EcResult VideoEngine::SaveReplay(const wchar_t* path, uint32_t seconds, EcExportResult& result) { return implementation_->SaveReplay(path, seconds, result); }
    EcResult VideoEngine::StartRecording(const wchar_t* path) { return implementation_->StartRecording(path); }
    EcResult VideoEngine::StopRecording(EcExportResult& result) { return implementation_->StopRecording(result); }
    EcResult VideoEngine::GetStats(EcVideoStats& stats) const { return implementation_->GetStats(stats); }
    std::wstring VideoEngine::LastError() const { return implementation_->LastError(); }
}
