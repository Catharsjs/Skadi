#include "VideoEngine.h"
#include "QsvEncoder.h"
#include <Windows.h>
#include <avrt.h>
#include <codecapi.h>
#include <icodecapi.h>
#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <dwmapi.h>
#include <dxgi1_6.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
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
#include <cmath>
#include <csignal>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <exception>
#include <filesystem>
#include <iomanip>
#include <iterator>
#include <limits>
#include <sstream>
#include <thread>
#include <vector>

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
        struct CompositorVertex
        {
            float x;
            float y;
            float u;
            float v;
        };

        struct MonitorFrameSlot
        {
            ComPtr<ID3D11Texture2D> texture;
            ComPtr<ID3D11VideoProcessorInputView> inputView;
            int64_t captureTimestamp100ns{};
            bool queued{};
            bool encoding{};
        };

        struct EncoderSurfaceSlot
        {
            ComPtr<ID3D11Texture2D> texture;
            ComPtr<ID3D11VideoProcessorOutputView> outputView;
            bool inFlight{};
        };

        enum class CaptureBackend
        {
            WindowsGraphicsCapture,
            DesktopDuplication
        };

        constexpr int64_t TicksPerSecond = 10'000'000;
        std::once_flag diagnosticsInstallFlag;

        std::wstring NativeDiagnosticsPath()
        {
            wchar_t temporaryPath[MAX_PATH]{};
            if (GetTempPathW(MAX_PATH, temporaryPath) == 0) return L"Skadi-Native-Diagnostics.log";
            return std::filesystem::path(temporaryPath).append(L"Skadi-Native-Diagnostics.log").wstring();
        }

        std::string ToUtf8(const std::wstring& value)
        {
            if (value.empty()) return {};
            const int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
            if (size <= 0) return {};
            std::string result(static_cast<size_t>(size), '\0');
            WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr);
            return result;
        }

        void AppendNativeLogNoThrow(const std::wstring& message)
        {
            try
            {
                SYSTEMTIME time{};
                GetLocalTime(&time);

                std::wstringstream line;
                line
                    << L"["
                    << std::setfill(L'0')
                    << std::setw(4) << time.wYear << L"-"
                    << std::setw(2) << time.wMonth << L"-"
                    << std::setw(2) << time.wDay << L" "
                    << std::setw(2) << time.wHour << L":"
                    << std::setw(2) << time.wMinute << L":"
                    << std::setw(2) << time.wSecond << L"."
                    << std::setw(3) << time.wMilliseconds
                    << L"] [T" << GetCurrentThreadId() << L"] "
                    << message << L"\r\n";

                const std::string utf8 = ToUtf8(line.str());
                const std::wstring path = NativeDiagnosticsPath();
                HANDLE file = CreateFileW(
                    path.c_str(),
                    FILE_APPEND_DATA,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    nullptr,
                    OPEN_ALWAYS,
                    FILE_ATTRIBUTE_NORMAL,
                    nullptr);

                if (file == INVALID_HANDLE_VALUE) return;

                DWORD written = 0;
                WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
                CloseHandle(file);
            }
            catch (...) {}
        }

        std::wstring RectToString(const RECT& rectangle)
        {
            std::wstringstream text;
            text
                << L"L=" << rectangle.left
                << L" T=" << rectangle.top
                << L" R=" << rectangle.right
                << L" B=" << rectangle.bottom
                << L" W=" << (rectangle.right - rectangle.left)
                << L" H=" << (rectangle.bottom - rectangle.top);
            return text.str();
        }

        RECT GetExtendedWindowFrameBounds(HWND window)
        {
            RECT rectangle{};
            if (SUCCEEDED(DwmGetWindowAttribute(
                    window,
                    DWMWA_EXTENDED_FRAME_BOUNDS,
                    &rectangle,
                    sizeof(rectangle))) &&
                rectangle.right > rectangle.left &&
                rectangle.bottom > rectangle.top)
            {
                return rectangle;
            }

            GetWindowRect(window, &rectangle);
            return rectangle;
        }

        void LogNative(const std::wstring& message)
        {
            AppendNativeLogNoThrow(message);
            OutputDebugStringW((message + L"\n").c_str());
        }

        double ElapsedMilliseconds(
            const std::chrono::steady_clock::time_point& start,
            const std::chrono::steady_clock::time_point& end = std::chrono::steady_clock::now())
        {
            return std::chrono::duration<double, std::milli>(end - start).count();
        }

        void LogNativeTimingIfSlow(
            const wchar_t* operation,
            double elapsedMilliseconds,
            double thresholdMilliseconds,
            const std::wstring& details = L"")
        {
            if (elapsedMilliseconds < thresholdMilliseconds) return;

            std::wstringstream message;
            message
                << L"Native timing slow | Operation=" << operation
                << L" | ElapsedMs=" << std::fixed << std::setprecision(2) << elapsedMilliseconds
                << L" | ThresholdMs=" << thresholdMilliseconds;
            if (!details.empty()) message << L" | " << details;
            LogNative(message.str());
        }

        bool TryGetWindowsBuildNumber(uint32_t& buildNumber)
        {
            buildNumber = 0;
            using RtlGetVersionFunction = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
            HMODULE module = GetModuleHandleW(L"ntdll.dll");
            if (module == nullptr) return false;
            auto rtlGetVersion = reinterpret_cast<RtlGetVersionFunction>(
                GetProcAddress(module, "RtlGetVersion"));
            if (rtlGetVersion == nullptr) return false;
            RTL_OSVERSIONINFOW version{};
            version.dwOSVersionInfoSize = sizeof(version);
            if (rtlGetVersion(&version) != 0) return false;
            buildNumber = version.dwBuildNumber;
            return true;
        }

        bool IsWindows11OrGreater()
        {
            uint32_t buildNumber = 0;
            return TryGetWindowsBuildNumber(buildNumber) &&
                buildNumber >= 22000;
        }

        bool IsMonitorRotated(HMONITOR monitor)
        {
            if (monitor == nullptr) return false;

            MONITORINFOEXW info{};
            info.cbSize = sizeof(info);
            if (!GetMonitorInfoW(monitor, &info)) return false;

            DEVMODEW mode{};
            mode.dmSize = sizeof(mode);
            if (!EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode)) return false;

            return mode.dmDisplayOrientation == DMDO_90 ||
                mode.dmDisplayOrientation == DMDO_270;
        }

        void AbortSignalHandler(int signal)
        {
            std::wstringstream message;
            message << L"SIGABRT received | Signal=" << signal;
            AppendNativeLogNoThrow(message.str());
            std::_Exit(3);
        }

        void TerminateHandler()
        {
            AppendNativeLogNoThrow(L"std::terminate invoked.");

            if (std::current_exception())
            {
                try
                {
                    std::rethrow_exception(std::current_exception());
                }
                catch (const winrt::hresult_error& error)
                {
                    AppendNativeLogNoThrow(L"Terminate exception | hresult_error | " + std::wstring(error.message().c_str()));
                }
                catch (const std::exception& error)
                {
                    AppendNativeLogNoThrow(L"Terminate exception | std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                }
                catch (...)
                {
                    AppendNativeLogNoThrow(L"Terminate exception | unknown");
                }
            }

            std::_Exit(2);
        }

        void InstallNativeDiagnostics()
        {
            std::call_once(
                diagnosticsInstallFlag,
                []
                {
                    std::set_terminate(TerminateHandler);
                    std::signal(SIGABRT, AbortSignalHandler);
                    LogNative(L"Native diagnostics installed | Log=" + NativeDiagnosticsPath());
                });
        }

        void ThrowIfFailed(HRESULT result)
        {
            if (FAILED(result)) winrt::throw_hresult(result);
        }

        HRESULT SafeProcessMessage(
            IMFTransform* transform,
            MFT_MESSAGE_TYPE message,
            ULONG_PTR parameter,
            unsigned long* exceptionCode) noexcept
        {
            if (exceptionCode != nullptr) *exceptionCode = 0;

            __try
            {
                return transform->ProcessMessage(message, parameter);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                if (exceptionCode != nullptr) *exceptionCode = GetExceptionCode();
                return E_FAIL;
            }
        }

        HRESULT SafeActivateTransform(
            IMFActivate* activate,
            IMFTransform** transform,
            unsigned long* exceptionCode) noexcept
        {
            if (exceptionCode != nullptr) *exceptionCode = 0;
            if (transform != nullptr) *transform = nullptr;

            __try
            {
                return activate->ActivateObject(__uuidof(IMFTransform), reinterpret_cast<void**>(transform));
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                if (exceptionCode != nullptr) *exceptionCode = GetExceptionCode();
                return E_FAIL;
            }
        }

        HRESULT SafeGetTransformAttributes(
            IMFTransform* transform,
            IMFAttributes** attributes,
            unsigned long* exceptionCode) noexcept
        {
            if (exceptionCode != nullptr) *exceptionCode = 0;
            if (attributes != nullptr) *attributes = nullptr;

            __try
            {
                return transform->GetAttributes(attributes);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                if (exceptionCode != nullptr) *exceptionCode = GetExceptionCode();
                return E_FAIL;
            }
        }

        HRESULT SafeQueryTransformInterface(
            IMFTransform* transform,
            REFIID interfaceId,
            void** result,
            unsigned long* exceptionCode) noexcept
        {
            if (exceptionCode != nullptr) *exceptionCode = 0;
            if (result != nullptr) *result = nullptr;

            __try
            {
                return transform->QueryInterface(interfaceId, result);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                if (exceptionCode != nullptr) *exceptionCode = GetExceptionCode();
                return E_FAIL;
            }
        }

        HRESULT SafeSetTransformOutputType(
            IMFTransform* transform,
            IMFMediaType* mediaType,
            unsigned long* exceptionCode) noexcept
        {
            if (exceptionCode != nullptr) *exceptionCode = 0;

            __try
            {
                return transform->SetOutputType(0, mediaType, 0);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                if (exceptionCode != nullptr) *exceptionCode = GetExceptionCode();
                return E_FAIL;
            }
        }

        HRESULT SafeSetTransformInputType(
            IMFTransform* transform,
            IMFMediaType* mediaType,
            unsigned long* exceptionCode) noexcept
        {
            if (exceptionCode != nullptr) *exceptionCode = 0;

            __try
            {
                return transform->SetInputType(0, mediaType, 0);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                if (exceptionCode != nullptr) *exceptionCode = GetExceptionCode();
                return E_FAIL;
            }
        }

        HRESULT SafeCodecSetValue(
            ICodecAPI* codec,
            const GUID* key,
            VARIANT* value,
            unsigned long* exceptionCode) noexcept
        {
            if (exceptionCode != nullptr) *exceptionCode = 0;
            if (codec == nullptr) return S_FALSE;

            __try
            {
                return codec->SetValue(key, value);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                if (exceptionCode != nullptr) *exceptionCode = GetExceptionCode();
                return E_FAIL;
            }
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
            unsigned long exceptionCode = 0;
            HRESULT result = SafeCodecSetValue(codec, &key, &variant, &exceptionCode);
            if (exceptionCode != 0)
            {
                std::wstringstream log;
                log << L"CodecAPI SetValue UInt32 SEH | Code=0x" << std::hex << exceptionCode;
                LogNative(log.str());
            }
            else if (FAILED(result))
            {
                std::wstringstream log;
                log << L"CodecAPI SetValue UInt32 failed | HRESULT=0x" << std::hex << static_cast<uint32_t>(result);
                LogNative(log.str());
            }
            VariantClear(&variant);
        }

        void SetVariantBool(ICodecAPI* codec, const GUID& key, bool value)
        {
            if (codec == nullptr) return;
            VARIANT variant{};
            VariantInit(&variant);
            variant.vt = VT_BOOL;
            variant.boolVal = value ? VARIANT_TRUE : VARIANT_FALSE;
            unsigned long exceptionCode = 0;
            HRESULT result = SafeCodecSetValue(codec, &key, &variant, &exceptionCode);
            if (exceptionCode != 0)
            {
                std::wstringstream log;
                log << L"CodecAPI SetValue Bool SEH | Code=0x" << std::hex << exceptionCode;
                LogNative(log.str());
            }
            else if (FAILED(result))
            {
                std::wstringstream log;
                log << L"CodecAPI SetValue Bool failed | HRESULT=0x" << std::hex << static_cast<uint32_t>(result);
                LogNative(log.str());
            }
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
        struct RecordingAudioSample
        {
            int index{};
            std::vector<uint8_t> bytes;
            int64_t sampleTime100ns{};
            int64_t duration100ns{};
        };

        explicit Implementation(const EcVideoConfig& config) : config_(config)
        {
            InstallNativeDiagnostics();
            const bool useDesktopDuplication =
                config_.targetKind == EcTargetKind::Monitor &&
                !IsWindows11OrGreater();
            captureBackend_ = useDesktopDuplication
                ? CaptureBackend::DesktopDuplication
                : CaptureBackend::WindowsGraphicsCapture;

            std::wstringstream configLog;
            configLog
                << L"VideoEngine ctor | Pipeline=mp4-encoded-sample-v2"
                << L" | Backend="
                << (captureBackend_ == CaptureBackend::DesktopDuplication ? L"DDA" : L"WGC")
                << L" | MonitorRotated=" << (config_.targetKind == EcTargetKind::Monitor && IsMonitorRotated(static_cast<HMONITOR>(config_.targetHandle)) ? 1 : 0)
                << L" | TargetKind=" << static_cast<int32_t>(config_.targetKind)
                << L" | TargetHandle=0x" << std::hex << reinterpret_cast<uintptr_t>(config_.targetHandle)
                << std::dec
                << L" | Output=" << config_.outputWidth << L"x" << config_.outputHeight
                << L" | FPS=" << config_.framesPerSecond
                << L" | BitrateKbps=" << config_.bitrateKbps
                << L" | ReplaySeconds=" << config_.replaySeconds
                << L" | EnableReplay=" << config_.enableReplay;
            LogNative(configLog.str());

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
            try
            {
                {
                    std::wstringstream log;
                    log << L"Implementation::Start pre-lock | This=0x" << std::hex << reinterpret_cast<uintptr_t>(this);
                    LogNative(log.str());
                }
                LogNative(L"Implementation::Start lock begin.");
                std::scoped_lock stateLock(stateMutex_);
                LogNative(L"Implementation::Start lock acquired.");
                if (running_) return EcResult::Ok;
                LogNative(L"Start entered.");
                startupStage_ = L"COM initialization";
                const HRESULT apartmentResult = RoInitialize(RO_INIT_MULTITHREADED);
                {
                    std::wstringstream log;
                    log << L"RoInitialize result=0x" << std::hex << static_cast<uint32_t>(apartmentResult);
                    LogNative(log.str());
                }
                if (apartmentResult == S_OK || apartmentResult == S_FALSE) apartmentInitialized_ = true;
                else if (apartmentResult != RPC_E_CHANGED_MODE) ThrowIfFailed(apartmentResult);
                startupStage_ = L"Media Foundation startup";
                ThrowIfFailed(MFStartup(MF_VERSION, MFSTARTUP_FULL));
                mediaFoundationStarted_ = true;
                startupStage_ = L"D3D11 device creation";
                CreateDevice();
                if (captureBackend_ == CaptureBackend::DesktopDuplication)
                {
                    startupStage_ = L"Desktop Duplication capture creation";
                    CreateDesktopDuplicationCapture();
                }
                else
                {
                    startupStage_ = L"Windows Graphics Capture creation";
                    CreateCapture();
                }
                startupStage_ = L"H.264 encoder creation";
                LogNative(L"CreateEncoder begin.");
                CreateEncoder();
                LogNative(L"CreateEncoder completed.");
                encodeClockStart_ = std::chrono::steady_clock::now();
                lastPacketTimestamp100ns_ = -1;
                submittedFrames_.store(0);
                ResetCaptureDiagnostics();
                running_ = true;
                if (captureBackend_ == CaptureBackend::DesktopDuplication)
                {
                    LogNative(L"DesktopDuplication capture thread creation begin.");
                    desktopDuplicationThread_ = std::thread([this] { DesktopDuplicationLoop(); });
                    std::wstringstream log;
                    log << L"DesktopDuplication capture thread created | IdHash=" << std::hash<std::thread::id>{}(desktopDuplicationThread_.get_id());
                    LogNative(log.str());
                }
                else
                {
                    LogNative(L"StartCapture begin.");
                    captureSession_.StartCapture();
                    LogNative(L"StartCapture completed.");
                }
                LogNative(L"Encoder thread creation begin.");
                encoderThread_ = std::thread([this] { EncodeLoop(); });
                {
                    std::wstringstream log;
                    log << L"Encoder thread created | IdHash=" << std::hash<std::thread::id>{}(encoderThread_.get_id());
                    LogNative(log.str());
                }
                LogNative(L"Start completed.");
                return EcResult::Ok;
            }
            catch (const winrt::hresult_error& error)
            {
                std::wstringstream message;
                message << startupStage_ << L" failed with HRESULT 0x" << std::hex
                    << static_cast<uint32_t>(error.code()) << L": " << error.message().c_str();
                SetError(message.str());
                LogNative(L"Start failed | " + message.str());
                StopCore();
                return EcResult::NativeFailure;
            }
            catch (const std::exception& error)
            {
                SetError(std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                LogNative(L"Start failed | std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                StopCore();
                return EcResult::NativeFailure;
            }
            catch (...)
            {
                SetError(L"Unknown native start failure.");
                LogNative(L"Start failed | unknown exception");
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
            if (!WaitForQsvPacketWriterSnapshot()) return EcResult::NativeFailure;
            {
                std::wstringstream message;
                message << L"Native state | Action=SaveReplay enter | RequestedSec=" << seconds
                    << L" | ReplayEnabled=" << ReplayEnabled()
                    << L" | Recording=" << recording_
                    << L" | RecordingPending=" << recordingPending_
                    << L" | Packets=" << packets_.size()
                    << L" | Captured=" << capturedFrames_.load()
                    << L" | Submitted=" << submittedFrames_.load()
                    << L" | Encoded=" << encodedFrames_.load()
                    << L" | RecordingFrames=" << recordingFrameCount_;
                LogNative(message.str());
            }
            if (!ReplayEnabled()) return EcResult::InvalidState;
            std::vector<EncodedFrame> frames;
            int64_t windowStart100ns = 0;
            int64_t windowEnd100ns = 0;
            {
                std::scoped_lock lock(packetMutex_);
                if (packets_.empty()) return EcResult::InvalidState;
                if (activeSpoolStream_.is_open()) activeSpoolStream_.flush();

                const int64_t requestedDuration100ns = static_cast<int64_t>(seconds) * 10'000'000LL;
                windowEnd100ns = packets_.back().timestamp100ns + std::max<int64_t>(1, packets_.back().duration100ns);
                windowStart100ns = std::max<int64_t>(
                    packets_.front().timestamp100ns,
                    windowEnd100ns - requestedDuration100ns);

                size_t start = 0;
                while (start < packets_.size())
                {
                    const int64_t frameEnd100ns = packets_[start].timestamp100ns + std::max<int64_t>(1, packets_[start].duration100ns);
                    if (frameEnd100ns > windowStart100ns) break;
                    ++start;
                }

                size_t end = packets_.size();
                if (start >= end) return EcResult::InvalidState;
                frames.assign(packets_.begin() + static_cast<std::ptrdiff_t>(start), packets_.begin() + static_cast<std::ptrdiff_t>(end));
                std::wstringstream message;
                message << L"Native state | Action=SaveReplay selected-window | RequestedSec=" << seconds
                    << L" | PacketCount=" << packets_.size()
                    << L" | StartIndex=" << start
                    << L" | EndIndex=" << end
                    << L" | SelectedFrames=" << frames.size()
                    << L" | WindowStart=" << windowStart100ns
                    << L" | WindowEnd=" << windowEnd100ns
                    << L" | Recording=" << recording_
                    << L" | RecordingPending=" << recordingPending_
                    << L" | RecordingFrames=" << recordingFrameCount_;
                LogNative(message.str());
            }
            LogReplayCaptureDiagnostics(seconds, windowStart100ns, windowEnd100ns, frames);
            if (!WriteFramesToMp4(path, frames, windowStart100ns, windowEnd100ns)) return EcResult::NativeFailure;
            result.startTimestamp100ns = windowStart100ns;
            result.endTimestamp100ns = windowEnd100ns;
            result.frameCount = frames.size();
            {
                std::wstringstream message;
                message << L"Native state | Action=SaveReplay exit | Frames=" << frames.size()
                    << L" | WindowDurationMs=" << ((windowEnd100ns - windowStart100ns) / 10000)
                    << L" | Recording=" << recording_
                    << L" | RecordingPending=" << recordingPending_
                    << L" | RecordingFrames=" << recordingFrameCount_;
                LogNative(message.str());
            }
            return EcResult::Ok;
        }

        EcResult StartRecording(const wchar_t* path)
        {
            if (path == nullptr || *path == L'\0') return EcResult::InvalidArgument;
            if (!WaitForQsvPacketWriterSnapshot()) return EcResult::NativeFailure;

            try
            {
                std::scoped_lock lock(packetMutex_);

                if (recording_ || recordingPending_) return EcResult::InvalidState;

                ReleaseRecordingWriterNoThrow();

                recordingPath_ = path;
                recordingFrames_.clear();
                ClearRecordingAudioQueueNoLock();
                CreateRecordingWriter(path, nullptr, nullptr);

                recordingPending_ = true;
                recording_ = false;

                recordingStart100ns_ = -1;
                recordingEnd100ns_ = 0;
                recordingLastTimestamp100ns_ = -1;
                recordingLastKeyFrameRequest100ns_ = -1;
                recordingFrameCount_ = 0;

                ForceKeyFrame();

                {
                    std::wstringstream message;
                    message << L"Native state | Action=StartRecording armed-streaming"
                        << L" | ReplayEnabled=" << ReplayEnabled()
                        << L" | Packets=" << packets_.size()
                        << L" | Captured=" << capturedFrames_.load()
                        << L" | Submitted=" << submittedFrames_.load()
                        << L" | Encoded=" << encodedFrames_.load()
                        << L" | MonitorQueueOverflow=" << monitorQueueOverflowFrames_.load()
                        << L" | EncoderSurfaceStarvation=" << encoderSurfaceStarvationFrames_.load();

                    LogNative(message.str());
                }

                return EcResult::Ok;
            }
            catch (const winrt::hresult_error& error)
            {
                std::wstringstream message;
                message << L"StartRecording failed with HRESULT 0x" << std::hex
                    << static_cast<uint32_t>(error.code()) << L": " << error.message().c_str();

                SetError(message.str());
                LogNative(L"StartRecording failed | " + message.str());

                ReleaseRecordingWriterNoThrow();
                recording_ = false;
                recordingPending_ = false;
                recordingFrames_.clear();

                return EcResult::NativeFailure;
            }
            catch (const std::exception& error)
            {
                SetError(std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                LogNative(L"StartRecording failed | std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));

                ReleaseRecordingWriterNoThrow();
                recording_ = false;
                recordingPending_ = false;
                recordingFrames_.clear();

                return EcResult::NativeFailure;
            }
            catch (...)
            {
                SetError(L"Unknown StartRecording failure.");
                LogNative(L"StartRecording failed | unknown exception");

                ReleaseRecordingWriterNoThrow();
                recording_ = false;
                recordingPending_ = false;
                recordingFrames_.clear();

                return EcResult::NativeFailure;
            }
        }


        EcResult StartRecordingWithAudio(const wchar_t* path, const EcAudioStreamConfig* systemAudio, const EcAudioStreamConfig* microphoneAudio)
        {
            if (path == nullptr || *path == L'\0') return EcResult::InvalidArgument;
            if (!WaitForQsvPacketWriterSnapshot()) return EcResult::NativeFailure;

            try
            {
                std::scoped_lock lock(packetMutex_);

                if (recording_ || recordingPending_) return EcResult::InvalidState;

                ReleaseRecordingWriterNoThrow();

                recordingPath_ = path;
                recordingFrames_.clear();
                ClearRecordingAudioQueueNoLock();
                CreateRecordingWriter(path, systemAudio, microphoneAudio);

                recordingPending_ = true;
                recording_ = false;

                recordingStart100ns_ = -1;
                recordingEnd100ns_ = 0;
                recordingLastTimestamp100ns_ = -1;
                recordingLastKeyFrameRequest100ns_ = -1;
                recordingFrameCount_ = 0;
                recordingAudioLastTimestamp100ns_[0] = -1;
                recordingAudioLastTimestamp100ns_[1] = -1;
                recordingAudioQueuedLastTimestamp100ns_[0] = -1;
                recordingAudioQueuedLastTimestamp100ns_[1] = -1;

                ForceKeyFrame();
                LogNative(L"Native state | Action=StartRecordingWithAudio armed-streaming");
                return EcResult::Ok;
            }
            catch (const winrt::hresult_error& error)
            {
                std::wstringstream message;
                message << L"StartRecordingWithAudio failed with HRESULT 0x" << std::hex
                    << static_cast<uint32_t>(error.code()) << L": " << error.message().c_str();
                SetError(message.str());
                LogNative(L"StartRecordingWithAudio failed | " + message.str());
                ReleaseRecordingWriterNoThrow();
                recording_ = false;
                recordingPending_ = false;
                recordingFrames_.clear();
                return EcResult::NativeFailure;
            }
            catch (const std::exception& error)
            {
                SetError(std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                LogNative(L"StartRecordingWithAudio failed | std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                ReleaseRecordingWriterNoThrow();
                recording_ = false;
                recordingPending_ = false;
                recordingFrames_.clear();
                return EcResult::NativeFailure;
            }
            catch (...)
            {
                SetError(L"Unknown StartRecordingWithAudio failure.");
                LogNative(L"StartRecordingWithAudio failed | unknown exception");
                ReleaseRecordingWriterNoThrow();
                recording_ = false;
                recordingPending_ = false;
                recordingFrames_.clear();
                return EcResult::NativeFailure;
            }
        }

        EcResult WriteRecordingAudio(EcAudioStreamKind streamKind, const uint8_t* data, uint32_t byteCount, int64_t timestamp100ns, int64_t duration100ns)
        {
            if (data == nullptr || byteCount == 0 || duration100ns <= 0) return EcResult::InvalidArgument;
            int index = streamKind == EcAudioStreamKind::Microphone ? 1 : 0;

            try
            {
                const auto callStart = std::chrono::steady_clock::now();
                std::vector<uint8_t> bytes(data, data + byteCount);
                uint64_t droppedSamples = 0;
                uint64_t queuedBytes = 0;
                size_t queuedSamples = 0;
                int64_t sampleTime100ns = 0;

                {
                    const auto lockStart = std::chrono::steady_clock::now();
                    std::unique_lock lock(packetMutex_);
                    const double lockMs = ElapsedMilliseconds(lockStart);
                    if ((!recording_ && !recordingPending_) || recordingWriter_ == nullptr) return EcResult::InvalidState;
                    if (!recordingAudioEnabled_[index] || recordingAudioStreamIndex_[index] == static_cast<DWORD>(-1)) return EcResult::InvalidState;
                    if (recordingStart100ns_ < 0) return EcResult::Ok;

                    sampleTime100ns = timestamp100ns - recordingStart100ns_;
                    if (sampleTime100ns < 0)
                    {
                        duration100ns += sampleTime100ns;
                        sampleTime100ns = 0;
                    }
                    if (duration100ns <= 0) return EcResult::Ok;

                    if (recordingAudioQueuedLastTimestamp100ns_[index] >= 0 && sampleTime100ns <= recordingAudioQueuedLastTimestamp100ns_[index])
                        sampleTime100ns = recordingAudioQueuedLastTimestamp100ns_[index] + 1;

                    while (!recordingAudioQueue_.empty() &&
                        recordingAudioQueuedBytes_ + byteCount > MaxRecordingAudioQueueBytes)
                    {
                        recordingAudioQueuedBytes_ -= recordingAudioQueue_.front().bytes.size();
                        recordingAudioQueue_.pop_front();
                        ++recordingAudioDroppedSamples_;
                        ++droppedSamples;
                    }

                    recordingAudioQueue_.push_back(RecordingAudioSample{
                        index,
                        std::move(bytes),
                        sampleTime100ns,
                        duration100ns });
                    recordingAudioQueuedBytes_ += byteCount;
                    recordingAudioQueuedLastTimestamp100ns_[index] = sampleTime100ns;
                    queuedBytes = recordingAudioQueuedBytes_;
                    queuedSamples = recordingAudioQueue_.size();

                    if (lockMs >= 5.0)
                    {
                        std::wstringstream details;
                        details << L"Stream=" << index
                            << L" | Bytes=" << byteCount
                            << L" | LockMs=" << std::fixed << std::setprecision(2) << lockMs
                            << L" | QueueSamples=" << queuedSamples
                            << L" | QueueBytes=" << queuedBytes;
                        LogNativeTimingIfSlow(L"Audio queue lock", lockMs, 5.0, details.str());
                    }
                }

                const double totalMs = ElapsedMilliseconds(callStart);
                if (droppedSamples > 0 || totalMs >= 20.0)
                {
                    std::wstringstream details;
                    details << L"Stream=" << index
                        << L" | Bytes=" << byteCount
                        << L" | SampleTime100ns=" << sampleTime100ns
                        << L" | Duration100ns=" << duration100ns
                        << L" | QueueSamples=" << queuedSamples
                        << L" | QueueBytes=" << queuedBytes
                        << L" | DroppedNow=" << droppedSamples
                        << L" | DroppedTotal=" << recordingAudioDroppedSamples_
                        << L" | TotalMs=" << std::fixed << std::setprecision(2) << totalMs;
                    LogNative(L"Audio queued for recording | " + details.str());
                }

                return EcResult::Ok;
            }
            catch (const winrt::hresult_error& error)
            {
                std::wstringstream message;
                message << L"WriteRecordingAudio failed with HRESULT 0x" << std::hex
                    << static_cast<uint32_t>(error.code()) << L": " << error.message().c_str();
                SetError(message.str());
                LogNative(L"WriteRecordingAudio failed | " + message.str());
                return EcResult::NativeFailure;
            }
            catch (const std::exception& error)
            {
                SetError(std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                LogNative(L"WriteRecordingAudio failed | std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                return EcResult::NativeFailure;
            }
            catch (...)
            {
                SetError(L"Unknown WriteRecordingAudio failure.");
                LogNative(L"WriteRecordingAudio failed | unknown exception");
                return EcResult::NativeFailure;
            }
        }

        EcResult StopRecording(EcExportResult& result)
        {
            ComPtr<IMFSinkWriter> writer;
            std::wstring outputPath;
            int64_t start100ns = 0;
            int64_t end100ns = 0;
            uint64_t frameCount = 0;

            try
            {
                if (!WaitForQsvPacketWriterSnapshot()) return EcResult::NativeFailure;
                std::unique_lock writerLock(recordingWriterMutex_);

                {
                    std::scoped_lock lock(packetMutex_);

                    if (!recording_ && !recordingPending_) return EcResult::InvalidState;

                    recordingPending_ = false;
                    recording_ = false;

                    start100ns = recordingStart100ns_ >= 0 ? recordingStart100ns_ : 0;
                    end100ns = recordingEnd100ns_;
                    frameCount = recordingFrameCount_;
                    outputPath = recordingPath_;
                }

                if (frameCount == 0 || outputPath.empty() || recordingWriter_ == nullptr)
                {
                    if (recordingWriter_ != nullptr)
                    {
                        try { recordingWriter_->Finalize(); } catch (...) {}
                    }
                    ClearRecordingAudioQueue();
                    return EcResult::InvalidState;
                }

                const int64_t recordingDuration100ns = recordingStart100ns_ >= 0
                    ? std::max<int64_t>(0, CurrentTimestamp100ns() - recordingStart100ns_)
                    : std::max<int64_t>(0, end100ns - start100ns);
                end100ns = std::max(end100ns, start100ns + recordingDuration100ns);
                DrainRecordingAudioQueue(recordingDuration100ns);
                ClearRecordingAudioQueue();

                {
                    std::scoped_lock lock(packetMutex_);
                    recordingFrames_.clear();
                    writer = recordingWriter_;
                    recordingWriter_.Reset();
                    recordingPath_.clear();
                    recordingStart100ns_ = -1;
                    recordingEnd100ns_ = 0;
                    recordingLastTimestamp100ns_ = -1;
                    recordingFrameCount_ = 0;
                }

                writerLock.unlock();

                ThrowIfFailed(writer->Finalize());
                ReleaseRecordingWriterNoThrow();

                result.startTimestamp100ns = start100ns;
                result.endTimestamp100ns = end100ns;
                result.frameCount = frameCount;

                {
                    std::wstringstream message;
                    message << L"Native state | Action=StopRecording exit-streaming"
                        << L" | Result=Ok"
                        << L" | Frames=" << frameCount
                        << L" | Start=" << result.startTimestamp100ns
                        << L" | End=" << result.endTimestamp100ns
                        << L" | DurationMs=" << ((result.endTimestamp100ns - result.startTimestamp100ns) / 10000)
                        << L" | ReplayEnabled=" << ReplayEnabled()
                        << L" | Packets=" << packets_.size()
                        << L" | Captured=" << capturedFrames_.load()
                        << L" | Submitted=" << submittedFrames_.load()
                        << L" | Encoded=" << encodedFrames_.load();

                    LogNative(message.str());
                }

                return EcResult::Ok;
            }
            catch (const winrt::hresult_error& error)
            {
                std::wstringstream message;
                message << L"StopRecording streaming finalize failed with HRESULT 0x" << std::hex
                    << static_cast<uint32_t>(error.code()) << L": " << error.message().c_str();

                SetError(message.str());
                LogNative(L"StopRecording failed | " + message.str());

                ReleaseRecordingWriterNoThrow();
                ClearRecordingAudioQueue();
                recordingPath_.clear();
                recordingFrames_.clear();

                return EcResult::NativeFailure;
            }
            catch (const std::exception& error)
            {
                SetError(std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                LogNative(L"StopRecording failed | std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                ReleaseRecordingWriterNoThrow();
                ClearRecordingAudioQueue();
                recordingPath_.clear();
                recordingFrames_.clear();
                return EcResult::NativeFailure;
            }
            catch (...)
            {
                SetError(L"Unknown buffered recording export failure.");
                LogNative(L"StopRecording failed | unknown exception");
                ReleaseRecordingWriterNoThrow();
                ClearRecordingAudioQueue();
                recordingPath_.clear();
                recordingFrames_.clear();
                return EcResult::NativeFailure;
            }
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
        bool ReplayEnabled() const
        {
            return config_.enableReplay != 0;
        }

        ComPtr<IDXGIAdapter1> FindAdapterForTargetMonitor()
        {
            if (config_.targetKind != EcTargetKind::Monitor ||
                config_.targetHandle == nullptr)
            {
                return nullptr;
            }

            ComPtr<IDXGIFactory1> factory;
            if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return nullptr;

            for (UINT adapterIndex = 0;; ++adapterIndex)
            {
                ComPtr<IDXGIAdapter1> adapter;
                if (factory->EnumAdapters1(adapterIndex, &adapter) == DXGI_ERROR_NOT_FOUND) break;

                for (UINT outputIndex = 0;; ++outputIndex)
                {
                    ComPtr<IDXGIOutput> output;
                    if (adapter->EnumOutputs(outputIndex, &output) == DXGI_ERROR_NOT_FOUND) break;

                    DXGI_OUTPUT_DESC description{};
                    if (SUCCEEDED(output->GetDesc(&description)) &&
                        description.Monitor == static_cast<HMONITOR>(config_.targetHandle))
                    {
                        DXGI_ADAPTER_DESC1 adapterDescription{};
                        adapter->GetDesc1(&adapterDescription);
                        std::wstringstream log;
                        log
                            << L"DDA target adapter selected | Adapter=" << adapterDescription.Description
                            << L" | Output=" << description.DeviceName
                            << L" | Monitor=0x" << std::hex
                            << reinterpret_cast<uintptr_t>(description.Monitor);
                        LogNative(log.str());
                        return adapter;
                    }
                }
            }

            LogNative(L"DDA target adapter was not found; falling back to default D3D adapter.");
            return nullptr;
        }

        void CreateDevice()
        {
            const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
            D3D_FEATURE_LEVEL featureLevel{};
            ComPtr<IDXGIAdapter1> targetAdapter =
                captureBackend_ == CaptureBackend::DesktopDuplication
                    ? FindAdapterForTargetMonitor()
                    : nullptr;
            ThrowIfFailed(D3D11CreateDevice(
                targetAdapter.Get(),
                targetAdapter != nullptr ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE,
                nullptr,
                flags,
                nullptr,
                0,
                D3D11_SDK_VERSION,
                &device_,
                &featureLevel,
                &context_));

            ComPtr<IDXGIDevice> dxgiDevice;
            ComPtr<IDXGIAdapter> deviceAdapter;
            ComPtr<IDXGIAdapter1> deviceAdapter1;
            ThrowIfFailed(device_.As(&dxgiDevice));
            ThrowIfFailed(dxgiDevice->GetAdapter(&deviceAdapter));
            ThrowIfFailed(deviceAdapter.As(&deviceAdapter1));
            DXGI_ADAPTER_DESC1 deviceAdapterDescription{};
            ThrowIfFailed(deviceAdapter1->GetDesc1(&deviceAdapterDescription));
            encoderAdapterLuid_ = deviceAdapterDescription.AdapterLuid;
            hasEncoderAdapterLuid_ = true;
            encoderAdapterIsIntel_ = deviceAdapterDescription.VendorId == 0x8086;

            {
                std::wstringstream log;
                log << L"D3D11 encoder adapter | Name=" << deviceAdapterDescription.Description
                    << L" | VendorId=0x" << std::hex << deviceAdapterDescription.VendorId
                    << L" | LuidHigh=0x" << static_cast<uint32_t>(encoderAdapterLuid_.HighPart)
                    << L" | LuidLow=0x" << encoderAdapterLuid_.LowPart;
                LogNative(log.str());
            }

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
            LogNative(L"CreateCapture entered.");
            captureItem_ = CreateCaptureItem(config_);
            const auto size = captureItem_.Size();
            if (size.Width <= 0 || size.Height <= 0) throw std::runtime_error("Capture target has an invalid size");
            {
                std::wstringstream log;
                log << L"CaptureItem size=" << size.Width << L"x" << size.Height;
                LogNative(log.str());
            }
            if (config_.targetKind == EcTargetKind::Window)
            {
                initialWindowMonitor_ = MonitorFromWindow(
                    static_cast<HWND>(config_.targetHandle),
                    MONITOR_DEFAULTTONEAREST);

                RECT windowRect = GetExtendedWindowFrameBounds(static_cast<HWND>(config_.targetHandle));
                windowFrameWidth_ = static_cast<uint32_t>(std::max<LONG>(1, windowRect.right - windowRect.left));
                windowFrameHeight_ = static_cast<uint32_t>(std::max<LONG>(1, windowRect.bottom - windowRect.top));
                std::wstringstream log;
                log
                    << L"Initial window monitor=0x" << std::hex << reinterpret_cast<uintptr_t>(initialWindowMonitor_)
                    << std::dec
                    << L" | WindowRect=" << RectToString(windowRect);
                LogNative(log.str());
            }
            CreateLatestTexture(static_cast<uint32_t>(size.Width), static_cast<uint32_t>(size.Height));
            framePool_ = Direct3D11CaptureFramePool::CreateFreeThreaded(
                winRtDevice_,
                DirectXPixelFormat::B8G8R8A8UIntNormalized,
                3,
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
                        std::unique_lock lock(textureMutex_);
                        D3D11_TEXTURE2D_DESC description{};
                        texture->GetDesc(&description);
                        const uint32_t contentWidth = static_cast<uint32_t>(contentSize.Width);
                        const uint32_t contentHeight = static_cast<uint32_t>(contentSize.Height);
                        if (config_.targetKind == EcTargetKind::Window && !IsWindowOnInitialMonitor())
                        {
                            std::wstringstream log;
                            RECT windowRect = GetExtendedWindowFrameBounds(static_cast<HWND>(config_.targetHandle));
                            log
                                << L"Window source invalid | CurrentMonitor=0x" << std::hex
                                << reinterpret_cast<uintptr_t>(MonitorFromWindow(static_cast<HWND>(config_.targetHandle), MONITOR_DEFAULTTONEAREST))
                                << L" | InitialMonitor=0x"
                                << reinterpret_cast<uintptr_t>(initialWindowMonitor_)
                                << std::dec
                                << L" | Texture=" << description.Width << L"x" << description.Height
                                << L" | Source=" << sourceWidth_ << L"x" << sourceHeight_
                                << L" | Content=" << contentSize.Width << L"x" << contentSize.Height
                                << L" | WindowRect=" << RectToString(windowRect);
                            LogNative(log.str());

                            windowSourceInvalid_.store(true);
                            hasTexture_ = true;
                            ++textureVersion_;
                            frameCondition_.notify_one();
                            return;
                        }

                        if (config_.targetKind == EcTargetKind::Window)
                        {
                            RECT windowRect = GetExtendedWindowFrameBounds(static_cast<HWND>(config_.targetHandle));
                            windowFrameWidth_ = static_cast<uint32_t>(std::max<LONG>(1, windowRect.right - windowRect.left));
                            windowFrameHeight_ = static_cast<uint32_t>(std::max<LONG>(1, windowRect.bottom - windowRect.top));
                        }

                        bool recreateFramePool = false;
                        if (config_.targetKind == EcTargetKind::Window &&
                            (contentWidth != sourceContentWidth_ ||
                             contentHeight != sourceContentHeight_))
                        {
                            recreateFramePool = true;
                        }
                        else if (config_.targetKind != EcTargetKind::Window &&
                            (contentWidth != sourceContentWidth_ ||
                             contentHeight != sourceContentHeight_))
                        {
                            recreateFramePool = true;
                        }

                        if (description.Width != sourceWidth_ ||
                            description.Height != sourceHeight_)
                        {
                            CreateLatestTexture(
                                description.Width,
                                description.Height);
                        }

                        if (config_.targetKind == EcTargetKind::Monitor)
                        {
                            const int64_t captureTimestamp100ns = CurrentTimestamp100ns();
                            monitorQueueMaxDepth_.store(std::max<uint64_t>(
                                monitorQueueMaxDepth_.load(),
                                static_cast<uint64_t>(queuedMonitorFrameSlots_.size())));

                            size_t slotIndex = std::numeric_limits<size_t>::max();
                            for (size_t attempt = 0; attempt < monitorFrameSlots_.size(); ++attempt)
                            {
                                const size_t candidate = nextMonitorFrameSlot_;
                                nextMonitorFrameSlot_ = (nextMonitorFrameSlot_ + 1) % monitorFrameSlots_.size();
                                if (!monitorFrameSlots_[candidate].queued && !monitorFrameSlots_[candidate].encoding)
                                {
                                    slotIndex = candidate;
                                    break;
                                }
                            }

                            while (slotIndex == std::numeric_limits<size_t>::max() &&
                                   !queuedMonitorFrameSlots_.empty())
                            {
                                const size_t candidate = queuedMonitorFrameSlots_.front();
                                queuedMonitorFrameSlots_.pop_front();
                                if (candidate < monitorFrameSlots_.size() &&
                                    !monitorFrameSlots_[candidate].encoding)
                                {
                                    monitorFrameSlots_[candidate].queued = false;
                                    slotIndex = candidate;
                                    break;
                                }
                            }

                            if (slotIndex == std::numeric_limits<size_t>::max())
                            {
                                monitorQueueOverflowFrames_.fetch_add(1);
                                droppedFrames_.fetch_add(1);
                                return;
                            }

                            context_->CopyResource(monitorFrameSlots_[slotIndex].texture.Get(), texture.Get());
                            monitorFrameSlots_[slotIndex].captureTimestamp100ns = captureTimestamp100ns;
                            monitorFrameSlots_[slotIndex].queued = true;
                            monitorFrameSlots_[slotIndex].encoding = false;
                            queuedMonitorFrameSlots_.push_back(slotIndex);
                            monitorQueueMaxDepth_.store(std::max<uint64_t>(
                                monitorQueueMaxDepth_.load(),
                                static_cast<uint64_t>(queuedMonitorFrameSlots_.size())));
                        }
                        else
                        {
                            context_->CopyResource(latestTexture_.Get(), texture.Get());
                        }
                        sourceContentWidth_ = std::max<uint32_t>(1, std::min<uint32_t>(contentWidth, sourceWidth_));
                        sourceContentHeight_ = std::max<uint32_t>(1, std::min<uint32_t>(contentHeight, sourceHeight_));
                        windowSourceInvalid_.store(false);
                        hasTexture_ = true;
                        ++textureVersion_;
                        capturedFrames_.fetch_add(1);
                        AddDiagnosticTimestamp(capturedTimeline100ns_, CurrentTimestamp100ns());
                        frameCondition_.notify_one();
                        lock.unlock();

                        if (recreateFramePool)
                        {
                            framePool_.Recreate(
                                winRtDevice_,
                                DirectXPixelFormat::B8G8R8A8UIntNormalized,
                                3,
                                contentSize);
                        }
                    }
                    catch (const winrt::hresult_error& error)
                    {
                        std::wstringstream log;
                        log
                            << L"FrameArrived hresult_error | Code=0x" << std::hex
                            << static_cast<uint32_t>(error.code())
                            << L" | Message=" << error.message().c_str();
                        LogNative(log.str());
                        droppedFrames_.fetch_add(1);
                    }
                    catch (const std::exception& error)
                    {
                        LogNative(L"FrameArrived std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                        droppedFrames_.fetch_add(1);
                    }
                    catch (...)
                    {
                        LogNative(L"FrameArrived unknown exception.");
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
            LogNative(L"CreateCapture completed.");
        }

        void CreateDesktopDuplicationCapture()
        {
            if (config_.targetKind != EcTargetKind::Monitor)
            {
                throw std::runtime_error("Desktop Duplication backend supports monitor targets only");
            }

            LogNative(L"CreateDesktopDuplicationCapture entered.");

            ComPtr<IDXGIDevice> dxgiDevice;
            ThrowIfFailed(device_.As(&dxgiDevice));

            ComPtr<IDXGIAdapter> adapter;
            ThrowIfFailed(dxgiDevice->GetAdapter(&adapter));

            ComPtr<IDXGIOutput> selectedOutput;
            DXGI_OUTPUT_DESC selectedDescription{};

            for (UINT outputIndex = 0;; ++outputIndex)
            {
                ComPtr<IDXGIOutput> output;
                HRESULT result = adapter->EnumOutputs(outputIndex, &output);
                if (result == DXGI_ERROR_NOT_FOUND) break;
                ThrowIfFailed(result);

                DXGI_OUTPUT_DESC description{};
                ThrowIfFailed(output->GetDesc(&description));
                if (description.Monitor == static_cast<HMONITOR>(config_.targetHandle))
                {
                    selectedOutput = output;
                    selectedDescription = description;
                    break;
                }
            }

            if (selectedOutput == nullptr)
            {
                throw std::runtime_error("Desktop Duplication target output was not found on the selected adapter");
            }

            const uint32_t logicalWidth = static_cast<uint32_t>(
                std::max<LONG>(1, selectedDescription.DesktopCoordinates.right - selectedDescription.DesktopCoordinates.left));
            const uint32_t logicalHeight = static_cast<uint32_t>(
                std::max<LONG>(1, selectedDescription.DesktopCoordinates.bottom - selectedDescription.DesktopCoordinates.top));

            ComPtr<IDXGIOutput1> output1;
            ThrowIfFailed(selectedOutput.As(&output1));
            ThrowIfFailed(output1->DuplicateOutput(device_.Get(), &desktopDuplication_));

            DXGI_OUTDUPL_DESC duplicationDescription{};
            desktopDuplication_->GetDesc(&duplicationDescription);
            desktopDuplicationRotation_ = duplicationDescription.Rotation;

            const uint32_t surfaceWidth = duplicationDescription.ModeDesc.Width > 0
                ? duplicationDescription.ModeDesc.Width
                : logicalWidth;
            const uint32_t surfaceHeight = duplicationDescription.ModeDesc.Height > 0
                ? duplicationDescription.ModeDesc.Height
                : logicalHeight;

            {
                std::wstringstream log;
                log
                    << L"DDA output selected | DeviceName=" << selectedDescription.DeviceName
                    << L" | Bounds=" << RectToString(selectedDescription.DesktopCoordinates)
                    << L" | LogicalSize=" << logicalWidth << L"x" << logicalHeight
                    << L" | SurfaceSize=" << surfaceWidth << L"x" << surfaceHeight
                    << L" | Rotation=" << static_cast<uint32_t>(desktopDuplicationRotation_);
                LogNative(log.str());
            }

            CreateLatestTexture(surfaceWidth, surfaceHeight);

            LogNative(L"CreateDesktopDuplicationCapture completed.");
        }

        void DesktopDuplicationLoop()
        {
            DWORD mmcssTaskIndex = 0;
            HANDLE mmcssHandle = AvSetMmThreadCharacteristicsW(L"Capture", &mmcssTaskIndex);
            if (mmcssHandle != nullptr)
            {
                AvSetMmThreadPriority(mmcssHandle, AVRT_PRIORITY_HIGH);
                LogNative(L"DesktopDuplicationLoop MMCSS enabled | Task=Capture | Priority=High");
            }
            else
            {
                SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
                LogNative(L"DesktopDuplicationLoop MMCSS unavailable; using THREAD_PRIORITY_HIGHEST.");
            }

            LogNative(L"DesktopDuplicationLoop started.");

            while (running_)
            {
                bool frameAcquired = false;

                try
                {
                    DXGI_OUTDUPL_FRAME_INFO frameInfo{};
                    ComPtr<IDXGIResource> resource;
                    HRESULT result = desktopDuplication_->AcquireNextFrame(16, &frameInfo, &resource);

                    if (result == DXGI_ERROR_WAIT_TIMEOUT)
                    {
                        continue;
                    }

                    if (result == DXGI_ERROR_ACCESS_LOST)
                    {
                        LogNative(L"DesktopDuplicationLoop access lost.");
                        SetError(L"Desktop Duplication access was lost.");
                        break;
                    }

                    ThrowIfFailed(result);
                    frameAcquired = true;

                    if (frameInfo.LastPresentTime.QuadPart != 0)
                    {
                        ComPtr<ID3D11Texture2D> texture;
                        ThrowIfFailed(resource.As(&texture));
                        QueueDesktopDuplicationFrame(texture.Get());
                    }
                }
                catch (const winrt::hresult_error& error)
                {
                    std::wstringstream log;
                    log
                        << L"DesktopDuplicationLoop hresult_error | Code=0x" << std::hex
                        << static_cast<uint32_t>(error.code())
                        << L" | Message=" << error.message().c_str();
                    LogNative(log.str());
                    droppedFrames_.fetch_add(1);
                }
                catch (const std::exception& error)
                {
                    LogNative(L"DesktopDuplicationLoop std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                    droppedFrames_.fetch_add(1);
                }
                catch (...)
                {
                    LogNative(L"DesktopDuplicationLoop unknown exception.");
                    droppedFrames_.fetch_add(1);
                }

                if (frameAcquired && desktopDuplication_ != nullptr)
                {
                    desktopDuplication_->ReleaseFrame();
                }
            }

            LogNative(L"DesktopDuplicationLoop exited.");
            if (mmcssHandle != nullptr)
            {
                AvRevertMmThreadCharacteristics(mmcssHandle);
            }
        }

        bool TryAcquireDesktopDuplicationFrame(uint32_t timeoutMilliseconds)
        {
            if (desktopDuplication_ == nullptr) return false;

            bool frameAcquired = false;
            bool frameUpdated = false;
            try
            {
                DXGI_OUTDUPL_FRAME_INFO frameInfo{};
                ComPtr<IDXGIResource> resource;
                const auto acquireStart = std::chrono::steady_clock::now();
                const HRESULT result = desktopDuplication_->AcquireNextFrame(timeoutMilliseconds, &frameInfo, &resource);
                const double acquireMs = ElapsedMilliseconds(acquireStart);

                if (result == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    std::wstringstream details;
                    details << L"TimeoutMs=" << timeoutMilliseconds << L" | Result=WAIT_TIMEOUT";
                    LogNativeTimingIfSlow(L"DDA AcquireNextFrame timeout", acquireMs, timeoutMilliseconds == 0 ? 5.0 : timeoutMilliseconds + 12.0, details.str());
                    return false;
                }

                {
                    std::wstringstream details;
                    details << L"TimeoutMs=" << timeoutMilliseconds << L" | Result=0x" << std::hex << static_cast<uint32_t>(result);
                    LogNativeTimingIfSlow(L"DDA AcquireNextFrame", acquireMs, timeoutMilliseconds == 0 ? 5.0 : timeoutMilliseconds + 12.0, details.str());
                }

                if (result == DXGI_ERROR_ACCESS_LOST)
                {
                    LogNative(L"DesktopDuplication access lost.");
                    SetError(L"Desktop Duplication access was lost.");
                    running_ = false;
                    return false;
                }

                ThrowIfFailed(result);
                frameAcquired = true;

                const auto updateStart = std::chrono::steady_clock::now();
                ComPtr<ID3D11Texture2D> texture;
                ThrowIfFailed(resource.As(&texture));
                UpdateDesktopDuplicationLatestFrame(texture.Get());
                LogNativeTimingIfSlow(L"DDA update latest frame", ElapsedMilliseconds(updateStart), 10.0);
                frameUpdated = true;
            }
            catch (const winrt::hresult_error& error)
            {
                std::wstringstream log;
                log
                    << L"TryAcquireDesktopDuplicationFrame hresult_error | Code=0x" << std::hex
                    << static_cast<uint32_t>(error.code())
                    << L" | Message=" << error.message().c_str();
                LogNative(log.str());
                droppedFrames_.fetch_add(1);
            }
            catch (const std::exception& error)
            {
                LogNative(L"TryAcquireDesktopDuplicationFrame std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                droppedFrames_.fetch_add(1);
            }
            catch (...)
            {
                LogNative(L"TryAcquireDesktopDuplicationFrame unknown exception.");
                droppedFrames_.fetch_add(1);
            }

            if (frameAcquired && desktopDuplication_ != nullptr)
            {
                desktopDuplication_->ReleaseFrame();
            }

            return frameUpdated;
        }

        void PumpDesktopDuplicationFrames(uint32_t firstTimeoutMilliseconds)
        {
            if (!TryAcquireDesktopDuplicationFrame(firstTimeoutMilliseconds)) return;

            constexpr int MaxExtraFrames = 4;
            for (int index = 0; index < MaxExtraFrames && running_; ++index)
            {
                if (!TryAcquireDesktopDuplicationFrame(0)) break;
            }
        }
        void UpdateDesktopDuplicationLatestFrame(ID3D11Texture2D* texture)
        {
            if (texture == nullptr) return;

            const int64_t captureTimestamp100ns = CurrentTimestamp100ns();
            const auto lockStart = std::chrono::steady_clock::now();
            std::unique_lock lock(textureMutex_);
            const double lockMs = ElapsedMilliseconds(lockStart);

            if (monitorFrameSlots_.empty())
            {
                droppedFrames_.fetch_add(1);
                return;
            }

            const auto copyStart = std::chrono::steady_clock::now();
            context_->CopyResource(monitorFrameSlots_[0].texture.Get(), texture);
            const double copyMs = ElapsedMilliseconds(copyStart);
            if (lockMs >= 5.0 || copyMs >= 5.0)
            {
                std::wstringstream details;
                details << L"LockMs=" << std::fixed << std::setprecision(2) << lockMs
                    << L" | CopyMs=" << copyMs
                    << L" | TextureVersion=" << textureVersion_;
                LogNativeTimingIfSlow(L"DDA latest frame lock/copy", lockMs + copyMs, 5.0, details.str());
            }
            monitorFrameSlots_[0].captureTimestamp100ns = captureTimestamp100ns;

            hasTexture_ = true;
            ++textureVersion_;
            capturedFrames_.fetch_add(1);
            AddDiagnosticTimestamp(capturedTimeline100ns_, captureTimestamp100ns);
            frameCondition_.notify_one();
        }

        void QueueDesktopDuplicationFrame(ID3D11Texture2D* texture)
        {
            if (texture == nullptr) return;

            const int64_t captureTimestamp100ns = CurrentTimestamp100ns();
            std::unique_lock lock(textureMutex_);

            if (monitorFrameSlots_.empty())
            {
                droppedFrames_.fetch_add(1);
                return;
            }

            monitorQueueMaxDepth_.store(std::max<uint64_t>(
                monitorQueueMaxDepth_.load(),
                static_cast<uint64_t>(queuedMonitorFrameSlots_.size())));

            size_t slotIndex = std::numeric_limits<size_t>::max();
            for (size_t attempt = 0; attempt < monitorFrameSlots_.size(); ++attempt)
            {
                const size_t candidate = nextMonitorFrameSlot_;
                nextMonitorFrameSlot_ = (nextMonitorFrameSlot_ + 1) % monitorFrameSlots_.size();
                if (!monitorFrameSlots_[candidate].queued && !monitorFrameSlots_[candidate].encoding)
                {
                    slotIndex = candidate;
                    break;
                }
            }

            if (slotIndex == std::numeric_limits<size_t>::max())
            {
                while (!queuedMonitorFrameSlots_.empty())
                {
                    const size_t candidate = queuedMonitorFrameSlots_.front();
                    queuedMonitorFrameSlots_.pop_front();
                    if (candidate < monitorFrameSlots_.size() && !monitorFrameSlots_[candidate].encoding)
                    {
                        monitorFrameSlots_[candidate].queued = false;
                        slotIndex = candidate;
                        break;
                    }
                }

                monitorQueueOverflowFrames_.fetch_add(1);
                droppedFrames_.fetch_add(1);
            }

            if (slotIndex == std::numeric_limits<size_t>::max())
            {
                return;
            }

            context_->CopyResource(monitorFrameSlots_[slotIndex].texture.Get(), texture);
            monitorFrameSlots_[slotIndex].captureTimestamp100ns = captureTimestamp100ns;
            monitorFrameSlots_[slotIndex].queued = true;
            monitorFrameSlots_[slotIndex].encoding = false;
            queuedMonitorFrameSlots_.push_back(slotIndex);
            monitorQueueMaxDepth_.store(std::max<uint64_t>(
                monitorQueueMaxDepth_.load(),
                static_cast<uint64_t>(queuedMonitorFrameSlots_.size())));

            sourceContentWidth_ = sourceWidth_;
            sourceContentHeight_ = sourceHeight_;
            hasTexture_ = true;
            ++textureVersion_;
            capturedFrames_.fetch_add(1);
            AddDiagnosticTimestamp(capturedTimeline100ns_, captureTimestamp100ns);
            frameCondition_.notify_one();
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

            ComPtr<ID3D11Texture2D> nextLatestTexture;
            ComPtr<ID3D11RenderTargetView> nextLatestRenderTarget;
            ComPtr<ID3D11ShaderResourceView> nextLatestShaderResource;
            ComPtr<ID3D11Texture2D> nextEncoderTexture;
            ComPtr<ID3D11Texture2D> nextBlackTexture;
            ComPtr<ID3D11VideoProcessorEnumerator> nextProcessorEnumerator;
            ComPtr<ID3D11VideoProcessor> nextProcessor;
            ComPtr<ID3D11VideoProcessorInputView> nextProcessorInput;
            ComPtr<ID3D11VideoProcessorOutputView> nextProcessorOutput;

            ThrowIfFailed(device_->CreateTexture2D(&description, nullptr, &nextLatestTexture));
            ThrowIfFailed(device_->CreateRenderTargetView(nextLatestTexture.Get(), nullptr, &nextLatestRenderTarget));

            if (config_.targetKind == EcTargetKind::Window)
            {
                ThrowIfFailed(device_->CreateShaderResourceView(nextLatestTexture.Get(), nullptr, &nextLatestShaderResource));
                if (canvasTexture_ == nullptr)
                {
                    CreateWindowCompositorPipeline();
                }
            }
            else
            {
                CreateVideoProcessor(
                    nextLatestTexture.Get(),
                    width,
                    height,
                    nextEncoderTexture,
                    nextBlackTexture,
                    nextProcessorEnumerator,
                    nextProcessor,
                    nextProcessorInput,
                    nextProcessorOutput);

                encoderTexture_ = nextEncoderTexture;
                blackTexture_ = nextBlackTexture;
                processorEnumerator_ = nextProcessorEnumerator;
                processor_ = nextProcessor;
                processorInput_ = nextProcessorInput;
                processorOutput_ = nextProcessorOutput;
                CreateMonitorFrameQueue(width, height);
                CreateEncoderSurfacePool();
            }

            latestTexture_ = nextLatestTexture;
            latestRenderTarget_ = nextLatestRenderTarget;
            latestShaderResource_ = nextLatestShaderResource;
            sourceWidth_ = width;
            sourceHeight_ = height;
            if (sourceContentWidth_ == 0) sourceContentWidth_ = width;
            if (sourceContentHeight_ == 0) sourceContentHeight_ = height;
        }

        void CreateMonitorFrameQueue(uint32_t width, uint32_t height)
        {
            monitorFrameSlots_.clear();
            queuedMonitorFrameSlots_.clear();
            nextMonitorFrameSlot_ = 0;

            if (config_.targetKind != EcTargetKind::Monitor || processorEnumerator_ == nullptr) return;

            D3D11_TEXTURE2D_DESC description{};
            description.Width = width;
            description.Height = height;
            description.MipLevels = 1;
            description.ArraySize = 1;
            description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.Usage = D3D11_USAGE_DEFAULT;
            description.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;

            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputView{};
            inputView.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
            inputView.Texture2D.MipSlice = 0;
            inputView.Texture2D.ArraySlice = 0;

            constexpr size_t QueueSize = 8;
            monitorFrameSlots_.reserve(QueueSize);
            for (size_t index = 0; index < QueueSize; ++index)
            {
                MonitorFrameSlot slot;
                ThrowIfFailed(device_->CreateTexture2D(&description, nullptr, &slot.texture));
                ThrowIfFailed(videoDevice_->CreateVideoProcessorInputView(
                    slot.texture.Get(),
                    processorEnumerator_.Get(),
                    &inputView,
                    &slot.inputView));
                monitorFrameSlots_.push_back(std::move(slot));
            }
        }

        void CreateEncoderSurfacePool(bool qsvCompatible = true)
        {
            encoderSurfaceSlots_.clear();
            submittedEncoderSurfaces_.clear();
            nextEncoderSurface_ = 0;

            if (captureBackend_ != CaptureBackend::DesktopDuplication ||
                encoderTexture_ == nullptr ||
                processorEnumerator_ == nullptr)
            {
                return;
            }

            D3D11_TEXTURE2D_DESC description{};
            encoderTexture_->GetDesc(&description);
            if (encoderAdapterIsIntel_ && qsvCompatible)
            {
                description.Width = (description.Width + 15u) & ~15u;
                description.Height = (description.Height + 15u) & ~15u;
                description.BindFlags |= D3D11_BIND_VIDEO_ENCODER;
            }

            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputViewDescription{};
            outputViewDescription.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
            outputViewDescription.Texture2D.MipSlice = 0;

            constexpr size_t SurfaceCount = 16;
            encoderSurfaceSlots_.reserve(SurfaceCount);
            for (size_t index = 0; index < SurfaceCount; ++index)
            {
                EncoderSurfaceSlot slot;
                ThrowIfFailed(device_->CreateTexture2D(&description, nullptr, &slot.texture));
                ThrowIfFailed(videoDevice_->CreateVideoProcessorOutputView(
                    slot.texture.Get(),
                    processorEnumerator_.Get(),
                    &outputViewDescription,
                    &slot.outputView));
                encoderSurfaceSlots_.push_back(std::move(slot));
            }

            LogNative(L"DDA direct encoder surface pool created | Count=16");
        }

        bool IsWindowOnInitialMonitor() const
        {
            if (config_.targetKind != EcTargetKind::Window) return true;

            const HWND window = static_cast<HWND>(config_.targetHandle);
            if (!IsWindow(window)) return false;

            const HMONITOR currentMonitor =
                MonitorFromWindow(
                    window,
                    MONITOR_DEFAULTTONEAREST);

            return currentMonitor != nullptr &&
                currentMonitor == initialWindowMonitor_;
        }

        D3D11_VIDEO_PROCESSOR_ROTATION GetDesktopDuplicationVideoRotation() const
        {
            switch (desktopDuplicationRotation_)
            {
            case DXGI_MODE_ROTATION_ROTATE90:
                return D3D11_VIDEO_PROCESSOR_ROTATION_90;
            case DXGI_MODE_ROTATION_ROTATE180:
                return D3D11_VIDEO_PROCESSOR_ROTATION_180;
            case DXGI_MODE_ROTATION_ROTATE270:
                return D3D11_VIDEO_PROCESSOR_ROTATION_270;
            default:
                return D3D11_VIDEO_PROCESSOR_ROTATION_IDENTITY;
            }
        }

        void CreateVideoProcessor(
            ID3D11Texture2D* latestTexture,
            uint32_t sourceWidth,
            uint32_t sourceHeight,
            ComPtr<ID3D11Texture2D>& encoderTexture,
            ComPtr<ID3D11Texture2D>& blackTexture,
            ComPtr<ID3D11VideoProcessorEnumerator>& processorEnumerator,
            ComPtr<ID3D11VideoProcessor>& processor,
            ComPtr<ID3D11VideoProcessorInputView>& processorInput,
            ComPtr<ID3D11VideoProcessorOutputView>& processorOutput)
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
            ThrowIfFailed(device_->CreateTexture2D(&outputDescription, nullptr, &encoderTexture));
            CreateBlackTexture(outputDescription, blackTexture);

            D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
            content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            content.InputWidth = sourceWidth;
            content.InputHeight = sourceHeight;
            content.OutputWidth = config_.outputWidth;
            content.OutputHeight = config_.outputHeight;
            content.InputFrameRate = { config_.framesPerSecond, 1 };
            content.OutputFrameRate = { config_.framesPerSecond, 1 };
            content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            ThrowIfFailed(videoDevice_->CreateVideoProcessorEnumerator(&content, &processorEnumerator));
            ThrowIfFailed(videoDevice_->CreateVideoProcessor(processorEnumerator.Get(), 0, &processor));
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputView{};
            inputView.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
            inputView.Texture2D.MipSlice = 0;
            inputView.Texture2D.ArraySlice = 0;
            ThrowIfFailed(videoDevice_->CreateVideoProcessorInputView(latestTexture, processorEnumerator.Get(), &inputView, &processorInput));
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputView{};
            outputView.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
            outputView.Texture2D.MipSlice = 0;
            ThrowIfFailed(videoDevice_->CreateVideoProcessorOutputView(encoderTexture.Get(), processorEnumerator.Get(), &outputView, &processorOutput));
            const RECT sourceRect{ 0, 0, static_cast<LONG>(sourceWidth), static_cast<LONG>(sourceHeight) };
            const RECT outputRect{ 0, 0, static_cast<LONG>(config_.outputWidth), static_cast<LONG>(config_.outputHeight) };
            const RECT centeredWindowRect = CalculateCenteredActualSizeRect(sourceWidth, sourceHeight);
            const RECT& streamDestRect =
                config_.targetKind == EcTargetKind::Window
                    ? centeredWindowRect
                    : outputRect;
            videoContext_->VideoProcessorSetStreamSourceRect(processor.Get(), 0, TRUE, &sourceRect);
            videoContext_->VideoProcessorSetStreamDestRect(processor.Get(), 0, TRUE, &streamDestRect);
            videoContext_->VideoProcessorSetOutputTargetRect(processor.Get(), TRUE, &outputRect);
            if (captureBackend_ == CaptureBackend::DesktopDuplication)
            {
                const D3D11_VIDEO_PROCESSOR_ROTATION rotation = GetDesktopDuplicationVideoRotation();
                videoContext_->VideoProcessorSetStreamRotation(
                    processor.Get(),
                    0,
                    rotation != D3D11_VIDEO_PROCESSOR_ROTATION_IDENTITY,
                    rotation);
            }
            D3D11_VIDEO_COLOR background{};
            background.RGBA.R = 0.0f;
            background.RGBA.G = 0.0f;
            background.RGBA.B = 0.0f;
            background.RGBA.A = 1.0f;
            videoContext_->VideoProcessorSetOutputBackgroundColor(processor.Get(), FALSE, &background);

            D3D11_VIDEO_PROCESSOR_COLOR_SPACE inputColorSpace{};
            inputColorSpace.RGB_Range = 0;
            inputColorSpace.YCbCr_Matrix = 1;
            inputColorSpace.Nominal_Range = 2;

            D3D11_VIDEO_PROCESSOR_COLOR_SPACE outputColorSpace{};
            outputColorSpace.RGB_Range = 1;
            outputColorSpace.YCbCr_Matrix = 1;
            outputColorSpace.Nominal_Range = 1;

            videoContext_->VideoProcessorSetStreamColorSpace(processor.Get(), 0, &inputColorSpace);
            videoContext_->VideoProcessorSetOutputColorSpace(processor.Get(), &outputColorSpace);
        }

        size_t AcquireEncoderSurface()
        {
            if (captureBackend_ != CaptureBackend::DesktopDuplication || encoderSurfaceSlots_.empty())
            {
                return std::numeric_limits<size_t>::max();
            }

            for (size_t attempt = 0; attempt < encoderSurfaceSlots_.size(); ++attempt)
            {
                const size_t candidate = nextEncoderSurface_;
                nextEncoderSurface_ = (nextEncoderSurface_ + 1) % encoderSurfaceSlots_.size();
                if (!encoderSurfaceSlots_[candidate].inFlight)
                {
                    encoderSurfaceSlots_[candidate].inFlight = true;
                    return candidate;
                }
            }

            return std::numeric_limits<size_t>::max();
        }

        void ReleaseEncoderSurface(size_t index)
        {
            if (index < encoderSurfaceSlots_.size())
            {
                encoderSurfaceSlots_[index].inFlight = false;
            }
        }

        RECT CalculateCenteredActualSizeRect(uint32_t sourceWidth, uint32_t sourceHeight) const
        {
            const LONG width = static_cast<LONG>(std::min<uint32_t>(sourceWidth, config_.outputWidth));
            const LONG height = static_cast<LONG>(std::min<uint32_t>(sourceHeight, config_.outputHeight));
            const LONG left = (static_cast<LONG>(config_.outputWidth) - width) / 2;
            const LONG top = (static_cast<LONG>(config_.outputHeight) - height) / 2;

            return RECT
            {
                left,
                top,
                left + width,
                top + height
            };
        }

        void CreateBlackTexture(
            const D3D11_TEXTURE2D_DESC& description,
            ComPtr<ID3D11Texture2D>& blackTexture)
        {
            const uint32_t pitch = description.Width;
            const uint32_t chromaHeight = (description.Height + 1) / 2;
            std::vector<uint8_t> blackFrame(static_cast<size_t>(pitch) * (description.Height + chromaHeight));
            std::fill(
                blackFrame.begin(),
                blackFrame.begin() + static_cast<size_t>(pitch) * description.Height,
                static_cast<uint8_t>(16));
            std::fill(
                blackFrame.begin() + static_cast<size_t>(pitch) * description.Height,
                blackFrame.end(),
                static_cast<uint8_t>(128));

            D3D11_SUBRESOURCE_DATA data{};
            data.pSysMem = blackFrame.data();
            data.SysMemPitch = pitch;
            data.SysMemSlicePitch = pitch * description.Height;
            ThrowIfFailed(device_->CreateTexture2D(&description, &data, &blackTexture));
        }

        ComPtr<ID3DBlob> CompileShader(
            const char* source,
            const char* entryPoint,
            const char* target)
        {
            ComPtr<ID3DBlob> shader;
            ComPtr<ID3DBlob> error;
            const HRESULT result = D3DCompile(
                source,
                std::strlen(source),
                nullptr,
                nullptr,
                nullptr,
                entryPoint,
                target,
                D3DCOMPILE_OPTIMIZATION_LEVEL3,
                0,
                &shader,
                &error);

            if (FAILED(result))
            {
                if (error != nullptr)
                {
                    const char* text = static_cast<const char*>(error->GetBufferPointer());
                    throw std::runtime_error(text != nullptr ? text : "D3D shader compilation failed");
                }

                ThrowIfFailed(result);
            }

            return shader;
        }

        void CreateWindowCompositorPipeline()
        {
            D3D11_TEXTURE2D_DESC canvasDescription{};
            canvasDescription.Width = config_.outputWidth;
            canvasDescription.Height = config_.outputHeight;
            canvasDescription.MipLevels = 1;
            canvasDescription.ArraySize = 1;
            canvasDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            canvasDescription.SampleDesc.Count = 1;
            canvasDescription.Usage = D3D11_USAGE_DEFAULT;
            canvasDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;

            ThrowIfFailed(device_->CreateTexture2D(&canvasDescription, nullptr, &canvasTexture_));
            ThrowIfFailed(device_->CreateRenderTargetView(canvasTexture_.Get(), nullptr, &canvasRenderTarget_));

            const char shaderSource[] = R"(
                struct VSIn
                {
                    float2 position : POSITION;
                    float2 uv : TEXCOORD0;
                };

                struct VSOut
                {
                    float4 position : SV_POSITION;
                    float2 uv : TEXCOORD0;
                };

                VSOut VSMain(VSIn input)
                {
                    VSOut output;
                    output.position = float4(input.position, 0.0f, 1.0f);
                    output.uv = input.uv;
                    return output;
                }

                Texture2D sourceTexture : register(t0);
                SamplerState sourceSampler : register(s0);

                float4 PSMain(VSOut input) : SV_TARGET
                {
                    float4 color = sourceTexture.Sample(sourceSampler, input.uv);
                    color.rgb *= saturate(color.a);
                    color.a = 1.0f;
                    return color;
                }
            )";

            ComPtr<ID3DBlob> vertexBlob = CompileShader(shaderSource, "VSMain", "vs_5_0");
            ComPtr<ID3DBlob> pixelBlob = CompileShader(shaderSource, "PSMain", "ps_5_0");

            ThrowIfFailed(device_->CreateVertexShader(
                vertexBlob->GetBufferPointer(),
                vertexBlob->GetBufferSize(),
                nullptr,
                &compositorVertexShader_));

            ThrowIfFailed(device_->CreatePixelShader(
                pixelBlob->GetBufferPointer(),
                pixelBlob->GetBufferSize(),
                nullptr,
                &compositorPixelShader_));

            D3D11_INPUT_ELEMENT_DESC inputElements[]
            {
                { "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
                { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0 }
            };

            ThrowIfFailed(device_->CreateInputLayout(
                inputElements,
                static_cast<UINT>(std::size(inputElements)),
                vertexBlob->GetBufferPointer(),
                vertexBlob->GetBufferSize(),
                &compositorInputLayout_));

            D3D11_BUFFER_DESC vertexBufferDescription{};
            vertexBufferDescription.ByteWidth = sizeof(CompositorVertex) * 6;
            vertexBufferDescription.Usage = D3D11_USAGE_DYNAMIC;
            vertexBufferDescription.BindFlags = D3D11_BIND_VERTEX_BUFFER;
            vertexBufferDescription.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            ThrowIfFailed(device_->CreateBuffer(&vertexBufferDescription, nullptr, &compositorVertexBuffer_));

            D3D11_SAMPLER_DESC samplerDescription{};
            samplerDescription.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
            samplerDescription.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDescription.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDescription.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDescription.MaxLOD = D3D11_FLOAT32_MAX;
            ThrowIfFailed(device_->CreateSamplerState(&samplerDescription, &compositorSampler_));

            ComPtr<ID3D11Texture2D> nextEncoderTexture;
            ComPtr<ID3D11Texture2D> nextBlackTexture;
            ComPtr<ID3D11VideoProcessorEnumerator> nextProcessorEnumerator;
            ComPtr<ID3D11VideoProcessor> nextProcessor;
            ComPtr<ID3D11VideoProcessorInputView> nextProcessorInput;
            ComPtr<ID3D11VideoProcessorOutputView> nextProcessorOutput;

            CreateVideoProcessor(
                canvasTexture_.Get(),
                config_.outputWidth,
                config_.outputHeight,
                nextEncoderTexture,
                nextBlackTexture,
                nextProcessorEnumerator,
                nextProcessor,
                nextProcessorInput,
                nextProcessorOutput);

            encoderTexture_ = nextEncoderTexture;
            blackTexture_ = nextBlackTexture;
            processorEnumerator_ = nextProcessorEnumerator;
            processor_ = nextProcessor;
            processorInput_ = nextProcessorInput;
            processorOutput_ = nextProcessorOutput;
        }

        void RenderWindowSourceToCanvas()
        {
            const uint32_t contentWidth = std::min<uint32_t>(
                sourceContentWidth_ > 0 ? sourceContentWidth_ : sourceWidth_,
                sourceWidth_);

            const uint32_t contentHeight = std::min<uint32_t>(
                sourceContentHeight_ > 0 ? sourceContentHeight_ : sourceHeight_,
                sourceHeight_);

            const uint32_t frameWidth = contentWidth;
            const uint32_t frameHeight = contentHeight;

            constexpr uint32_t windowEdgeGuardPixels = 2;
            const uint32_t guardX = contentWidth > windowEdgeGuardPixels * 2 + 8
                ? windowEdgeGuardPixels
                : 0;

            const uint32_t guardY = contentHeight > windowEdgeGuardPixels * 2 + 8
                ? windowEdgeGuardPixels
                : 0;

            const uint32_t guardedContentWidth = contentWidth > guardX * 2
                ? contentWidth - guardX * 2
                : contentWidth;

            const uint32_t guardedContentHeight = contentHeight > guardY * 2
                ? contentHeight - guardY * 2
                : contentHeight;

            const uint32_t guardedFrameWidth = frameWidth > guardX * 2
                ? frameWidth - guardX * 2
                : frameWidth;

            const uint32_t guardedFrameHeight = frameHeight > guardY * 2
                ? frameHeight - guardY * 2
                : frameHeight;

            const uint32_t renderWidth = std::max<uint32_t>(
                1,
                std::min<uint32_t>(
                    std::min<uint32_t>(guardedContentWidth, guardedFrameWidth),
                    config_.outputWidth));

            const uint32_t renderHeight = std::max<uint32_t>(
                1,
                std::min<uint32_t>(
                    std::min<uint32_t>(guardedContentHeight, guardedFrameHeight),
                    config_.outputHeight));

            const RECT destination = CalculateCenteredActualSizeRect(renderWidth, renderHeight);
            const uint32_t cropLeft = guardX + (guardedContentWidth > renderWidth ? (guardedContentWidth - renderWidth) / 2 : 0);
            const uint32_t cropTop = guardY + (guardedContentHeight > renderHeight ? (guardedContentHeight - renderHeight) / 2 : 0);
            const uint32_t cropRight = std::min<uint32_t>(sourceWidth_, cropLeft + renderWidth);
            const uint32_t cropBottom = std::min<uint32_t>(sourceHeight_, cropTop + renderHeight);
            const float u0 = static_cast<float>(cropLeft) / static_cast<float>(sourceWidth_);
            const float v0 = static_cast<float>(cropTop) / static_cast<float>(sourceHeight_);
            const float u1 = static_cast<float>(cropRight) / static_cast<float>(sourceWidth_);
            const float v1 = static_cast<float>(cropBottom) / static_cast<float>(sourceHeight_);
            const float outputWidth = static_cast<float>(config_.outputWidth);
            const float outputHeight = static_cast<float>(config_.outputHeight);
            const float left = (static_cast<float>(destination.left) / outputWidth) * 2.0f - 1.0f;
            const float right = (static_cast<float>(destination.right) / outputWidth) * 2.0f - 1.0f;
            const float top = 1.0f - (static_cast<float>(destination.top) / outputHeight) * 2.0f;
            const float bottom = 1.0f - (static_cast<float>(destination.bottom) / outputHeight) * 2.0f;

            const CompositorVertex vertices[]
            {
                { left, top, u0, v0 },
                { right, top, u1, v0 },
                { left, bottom, u0, v1 },
                { left, bottom, u0, v1 },
                { right, top, u1, v0 },
                { right, bottom, u1, v1 }
            };

            D3D11_MAPPED_SUBRESOURCE mapped{};
            ThrowIfFailed(context_->Map(compositorVertexBuffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped));
            std::memcpy(mapped.pData, vertices, sizeof(vertices));
            context_->Unmap(compositorVertexBuffer_.Get(), 0);

            const FLOAT clearColor[4]{ 0.0f, 0.0f, 0.0f, 1.0f };
            context_->ClearRenderTargetView(canvasRenderTarget_.Get(), clearColor);

            D3D11_VIEWPORT viewport{};
            viewport.Width = static_cast<float>(config_.outputWidth);
            viewport.Height = static_cast<float>(config_.outputHeight);
            viewport.MinDepth = 0.0f;
            viewport.MaxDepth = 1.0f;
            context_->RSSetViewports(1, &viewport);

            const UINT stride = sizeof(CompositorVertex);
            const UINT offset = 0;
            ID3D11Buffer* vertexBuffers[] { compositorVertexBuffer_.Get() };
            ID3D11ShaderResourceView* shaderResources[] { latestShaderResource_.Get() };
            ID3D11SamplerState* samplers[] { compositorSampler_.Get() };
            ID3D11RenderTargetView* renderTargets[] { canvasRenderTarget_.Get() };

            context_->IASetInputLayout(compositorInputLayout_.Get());
            context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context_->IASetVertexBuffers(0, 1, vertexBuffers, &stride, &offset);
            context_->VSSetShader(compositorVertexShader_.Get(), nullptr, 0);
            context_->PSSetShader(compositorPixelShader_.Get(), nullptr, 0);
            context_->PSSetShaderResources(0, 1, shaderResources);
            context_->PSSetSamplers(0, 1, samplers);
            context_->OMSetRenderTargets(1, renderTargets, nullptr);
            context_->Draw(6, 0);

            ID3D11ShaderResourceView* nullShaderResources[] { nullptr };
            context_->PSSetShaderResources(0, 1, nullShaderResources);
        }

        void CreateEncoder()
        {
            if (captureBackend_ == CaptureBackend::DesktopDuplication &&
                encoderAdapterIsIntel_ &&
                TryCreateQsvEncoder())
            {
                return;
            }

            startupStage_ = L"H.264 MFT hardware configuration";

            if (TryCreateConfiguredEncoder(
                    MFT_ENUM_FLAG_HARDWARE |
                    MFT_ENUM_FLAG_SORTANDFILTER,
                    L"hardware"))
            {
                return;
            }

            startupStage_ = L"H.264 MFT fallback configuration";

            if (TryCreateConfiguredEncoder(
                    MFT_ENUM_FLAG_ALL,
                    L"fallback"))
            {
                return;
            }

            throw std::runtime_error("No compatible H.264 Media Foundation encoder is available");
        }

        bool TryCreateQsvEncoder()
        {
            startupStage_ = L"Intel QSV D3D11 encoder configuration";
            try
            {
                std::vector<ID3D11Texture2D*> textures;
                textures.reserve(encoderSurfaceSlots_.size());
                for (const auto& slot : encoderSurfaceSlots_)
                {
                    textures.push_back(slot.texture.Get());
                }

                auto encoder = std::make_unique<QsvEncoder>();
                encoder->Initialize(
                    device_.Get(),
                    textures,
                    config_.outputWidth,
                    config_.outputHeight,
                    config_.framesPerSecond,
                    config_.bitrateKbps,
                    [this](QsvEncodedPacket&& packet) { ConsumeQsvPacket(std::move(packet)); },
                    [](const std::wstring& message) { LogNative(message); });
                qsvEncoder_ = std::move(encoder);
                useQsvEncoder_ = true;
                StartQsvPacketWriter();
                LogNative(L"DDA encoder backend selected | Backend=Intel oneVPL QSV | AsyncDepth=4");
                return true;
            }
            catch (const std::exception& error)
            {
                LogNative(
                    L"Intel oneVPL QSV initialization failed; using adapter-bound MFT | Error=" +
                    std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                qsvEncoder_.reset();
                useQsvEncoder_ = false;
                CreateEncoderSurfacePool(false);
                LogNative(L"DDA encoder surface pool recreated for MFT fallback layout");
                return false;
            }
        }

        bool TryCreateConfiguredEncoder(
            UINT32 flags,
            const wchar_t* label)
        {
            MFT_REGISTER_TYPE_INFO inputInfo{ MFMediaType_Video, MFVideoFormat_NV12 };
            MFT_REGISTER_TYPE_INFO outputInfo{ MFMediaType_Video, MFVideoFormat_H264 };
            IMFActivate** activates = nullptr;
            UINT32 count = 0;

            ComPtr<IMFAttributes> enumerationAttributes;
            const bool enumerateForAdapter =
                captureBackend_ == CaptureBackend::DesktopDuplication &&
                hasEncoderAdapterLuid_ &&
                (flags & MFT_ENUM_FLAG_HARDWARE) != 0;

            HRESULT result = S_OK;
            if (enumerateForAdapter)
            {
                ThrowIfFailed(MFCreateAttributes(&enumerationAttributes, 1));
                ThrowIfFailed(enumerationAttributes->SetBlob(
                    MFT_ENUM_ADAPTER_LUID,
                    reinterpret_cast<const UINT8*>(&encoderAdapterLuid_),
                    sizeof(encoderAdapterLuid_)));
                result = MFTEnum2(
                    MFT_CATEGORY_VIDEO_ENCODER,
                    flags,
                    &inputInfo,
                    &outputInfo,
                    enumerationAttributes.Get(),
                    &activates,
                    &count);
            }
            else
            {
                result = MFTEnumEx(
                    MFT_CATEGORY_VIDEO_ENCODER,
                    flags,
                    &inputInfo,
                    &outputInfo,
                    &activates,
                    &count);
            }

            if (FAILED(result) || count == 0)
            {
                std::wstringstream log;
                log
                    << L"H.264 MFT enumeration failed | Label=" << label
                    << L" | HRESULT=0x" << std::hex << static_cast<uint32_t>(result)
                    << L" | Count=" << std::dec << count;
                LogNative(log.str());

                if (activates != nullptr)
                {
                    CoTaskMemFree(activates);
                }

                return false;
            }

            std::wstringstream countLog;
            countLog
                << L"H.264 MFT candidates | Label=" << label
                << L" | Enumeration=" << (enumerateForAdapter ? L"MFTEnum2-adapter" : L"MFTEnumEx")
                << L" | Count=" << count;
            LogNative(countLog.str());

            bool configured = false;

            for (UINT32 index = 0; index < count; ++index)
            {
                try
                {
                    wchar_t* friendlyName = nullptr;
                    UINT32 friendlyNameLength = 0;
                    std::wstring candidateName = L"unknown";
                    if (SUCCEEDED(activates[index]->GetAllocatedString(
                            MFT_FRIENDLY_NAME_Attribute,
                            &friendlyName,
                            &friendlyNameLength)) &&
                        friendlyName != nullptr)
                    {
                        candidateName.assign(friendlyName, friendlyNameLength);
                        CoTaskMemFree(friendlyName);
                    }

                    GUID candidateClsid{};
                    wchar_t clsidText[64]{};
                    if (SUCCEEDED(activates[index]->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &candidateClsid)))
                    {
                        StringFromGUID2(candidateClsid, clsidText, static_cast<int>(std::size(clsidText)));
                    }

                    {
                        std::wstringstream log;
                        log
                            << L"H.264 MFT try | Label=" << label
                            << L" | Index=" << index
                            << L" | Name=" << candidateName
                            << L" | CLSID=" << (clsidText[0] != L'\0' ? clsidText : L"unknown");
                        LogNative(log.str());
                    }

                    ComPtr<IMFTransform> candidate;
                    LogNative(L"H.264 MFT ActivateObject begin");
                    unsigned long activationException = 0;
                    HRESULT activationResult =
                        SafeActivateTransform(
                            activates[index],
                            candidate.GetAddressOf(),
                            &activationException);
                    LogNative(L"H.264 MFT ActivateObject completed");

                    if (activationException != 0 || FAILED(activationResult))
                    {
                        std::wstringstream log;
                        log
                            << L"H.264 MFT activation failed | Label=" << label
                            << L" | Index=" << index
                            << L" | HRESULT=0x" << std::hex << static_cast<uint32_t>(activationResult)
                            << L" | SEH=0x" << activationException;
                        LogNative(log.str());
                        continue;
                    }

                    ComPtr<IMFMediaEventGenerator> candidateEventGenerator;
                    ComPtr<ICodecAPI> candidateCodecApi;
                    bool candidateAsync = false;

                    LogNative(L"H.264 MFT ConfigureEncoderTransform begin");
                    ConfigureEncoderTransform(
                        candidate.Get(),
                        candidateEventGenerator,
                        candidateCodecApi,
                        candidateAsync);
                    LogNative(L"H.264 MFT ConfigureEncoderTransform completed");

                    encoder_ = candidate;
                    eventGenerator_ = candidateEventGenerator;
                    codecApi_ = candidateCodecApi;
                    asyncEncoder_ = candidateAsync;
                    configured = true;

                    std::wstringstream log;
                    log
                        << L"H.264 MFT configured | Label=" << label
                        << L" | Index=" << index
                        << L" | Name=" << candidateName
                        << L" | Async=" << (asyncEncoder_ ? 1 : 0);
                    LogNative(log.str());

                    break;
                }
                catch (const winrt::hresult_error& error)
                {
                    std::wstringstream log;
                    log
                        << L"H.264 MFT rejected | Label=" << label
                        << L" | Index=" << index
                        << L" | HRESULT=0x" << std::hex << static_cast<uint32_t>(error.code())
                        << L" | Message=" << error.message().c_str();
                    LogNative(log.str());
                }
                catch (const std::exception& error)
                {
                    LogNative(
                        L"H.264 MFT rejected | Label=" +
                        std::wstring(label) +
                        L" | Index=" +
                        std::to_wstring(index) +
                        L" | Exception=" +
                        std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                }
            }

            for (UINT32 index = 0; index < count; ++index)
            {
                activates[index]->Release();
            }

            CoTaskMemFree(activates);

            return configured;
        }

        void ConfigureEncoderTransform(
            IMFTransform* transform,
            ComPtr<IMFMediaEventGenerator>& candidateEventGenerator,
            ComPtr<ICodecAPI>& candidateCodecApi,
            bool& candidateAsync)
        {
            ComPtr<IMFAttributes> attributes;
            LogNative(L"ConfigureEncoderTransform GetAttributes begin");
            unsigned long getAttributesException = 0;
            HRESULT getAttributesResult = SafeGetTransformAttributes(
                transform,
                attributes.GetAddressOf(),
                &getAttributesException);
            if (getAttributesException != 0)
            {
                std::wstringstream log;
                log << L"ConfigureEncoderTransform GetAttributes SEH | Code=0x" << std::hex << getAttributesException;
                LogNative(log.str());
            }
            if (SUCCEEDED(getAttributesResult) && attributes != nullptr)
            {
                attributes->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
                attributes->SetUINT32(MF_SA_D3D11_AWARE, TRUE);
                UINT32 asynchronous = FALSE;
                if (SUCCEEDED(attributes->GetUINT32(MF_TRANSFORM_ASYNC, &asynchronous)))
                {
                    candidateAsync = asynchronous != FALSE;
                }
            }
            LogNative(L"ConfigureEncoderTransform GetAttributes completed");

            if (candidateAsync)
            {
                LogNative(L"ConfigureEncoderTransform QueryInterface IMFMediaEventGenerator begin");
                unsigned long eventGeneratorException = 0;
                HRESULT eventGeneratorResult = SafeQueryTransformInterface(
                    transform,
                    __uuidof(IMFMediaEventGenerator),
                    reinterpret_cast<void**>(candidateEventGenerator.GetAddressOf()),
                    &eventGeneratorException);
                if (eventGeneratorException != 0)
                {
                    std::wstringstream log;
                    log << L"ConfigureEncoderTransform QueryInterface IMFMediaEventGenerator SEH | Code=0x" << std::hex << eventGeneratorException;
                    LogNative(log.str());
                }
                ThrowIfFailed(eventGeneratorResult);
                LogNative(L"ConfigureEncoderTransform QueryInterface IMFMediaEventGenerator completed");
            }

            LogNative(L"ConfigureEncoderTransform SET_D3D_MANAGER begin");
            unsigned long setD3DManagerException = 0;
            HRESULT setD3DManagerResult = SafeProcessMessage(
                transform,
                MFT_MESSAGE_SET_D3D_MANAGER,
                reinterpret_cast<ULONG_PTR>(deviceManager_.Get()),
                &setD3DManagerException);
            if (setD3DManagerException != 0)
            {
                std::wstringstream log;
                log
                    << L"ConfigureEncoderTransform SET_D3D_MANAGER SEH | Code=0x"
                    << std::hex
                    << setD3DManagerException;
                LogNative(log.str());
            }
            ThrowIfFailed(setD3DManagerResult);
            LogNative(L"ConfigureEncoderTransform SET_D3D_MANAGER completed");

            ComPtr<IMFMediaType> outputType;
            LogNative(L"ConfigureEncoderTransform output type begin");
            ThrowIfFailed(MFCreateMediaType(&outputType));
            ThrowIfFailed(outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
            ThrowIfFailed(outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
            ThrowIfFailed(outputType->SetUINT32(MF_MT_AVG_BITRATE, config_.bitrateKbps * 1000));
            ThrowIfFailed(outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
            ThrowIfFailed(MFSetAttributeSize(outputType.Get(), MF_MT_FRAME_SIZE, config_.outputWidth, config_.outputHeight));
            ThrowIfFailed(MFSetAttributeRatio(outputType.Get(), MF_MT_FRAME_RATE, config_.framesPerSecond, 1));
            ThrowIfFailed(MFSetAttributeRatio(outputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
            ConfigureSdrBt709ColorMetadata(outputType.Get());
            LogNative(L"ConfigureEncoderTransform SetOutputType begin");
            unsigned long setOutputTypeException = 0;
            HRESULT setOutputTypeResult = SafeSetTransformOutputType(transform, outputType.Get(), &setOutputTypeException);
            if (setOutputTypeException != 0)
            {
                std::wstringstream log;
                log << L"ConfigureEncoderTransform SetOutputType SEH | Code=0x" << std::hex << setOutputTypeException;
                LogNative(log.str());
            }
            ThrowIfFailed(setOutputTypeResult);
            LogNative(L"ConfigureEncoderTransform SetOutputType completed");

            ComPtr<IMFMediaType> inputType;
            LogNative(L"ConfigureEncoderTransform input type begin");
            ThrowIfFailed(MFCreateMediaType(&inputType));
            ThrowIfFailed(inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
            ThrowIfFailed(inputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
            ThrowIfFailed(inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
            ThrowIfFailed(MFSetAttributeSize(inputType.Get(), MF_MT_FRAME_SIZE, config_.outputWidth, config_.outputHeight));
            ThrowIfFailed(MFSetAttributeRatio(inputType.Get(), MF_MT_FRAME_RATE, config_.framesPerSecond, 1));
            ThrowIfFailed(MFSetAttributeRatio(inputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
            ConfigureSdrBt709ColorMetadata(inputType.Get());
            LogNative(L"ConfigureEncoderTransform SetInputType begin");
            unsigned long setInputTypeException = 0;
            HRESULT setInputTypeResult = SafeSetTransformInputType(transform, inputType.Get(), &setInputTypeException);
            if (setInputTypeException != 0)
            {
                std::wstringstream log;
                log << L"ConfigureEncoderTransform SetInputType SEH | Code=0x" << std::hex << setInputTypeException;
                LogNative(log.str());
            }
            ThrowIfFailed(setInputTypeResult);
            LogNative(L"ConfigureEncoderTransform SetInputType completed");

            LogNative(L"ConfigureEncoderTransform codec API begin");
            unsigned long codecApiException = 0;
            HRESULT codecApiResult = SafeQueryTransformInterface(
                transform,
                __uuidof(ICodecAPI),
                reinterpret_cast<void**>(candidateCodecApi.GetAddressOf()),
                &codecApiException);
            if (codecApiException != 0)
            {
                std::wstringstream log;
                log << L"ConfigureEncoderTransform QueryInterface ICodecAPI SEH | Code=0x" << std::hex << codecApiException;
                LogNative(log.str());
            }
            else if (FAILED(codecApiResult))
            {
                std::wstringstream log;
                log << L"ConfigureEncoderTransform QueryInterface ICodecAPI failed | HRESULT=0x" << std::hex << static_cast<uint32_t>(codecApiResult);
                LogNative(log.str());
            }
            SetVariantUInt32(candidateCodecApi.Get(), CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_CBR);
            SetVariantUInt32(candidateCodecApi.Get(), CODECAPI_AVEncCommonMeanBitRate, config_.bitrateKbps * 1000);
            SetVariantUInt32(candidateCodecApi.Get(), CODECAPI_AVEncMPVGOPSize, config_.framesPerSecond);
            SetVariantBool(candidateCodecApi.Get(), CODECAPI_AVLowLatencyMode, true);
            if (captureBackend_ == CaptureBackend::DesktopDuplication)
            {
                SetVariantBool(candidateCodecApi.Get(), CODECAPI_AVEncCommonRealTime, true);
                SetVariantUInt32(candidateCodecApi.Get(), CODECAPI_AVEncCommonQualityVsSpeed, 0);
                LogNative(L"DDA H.264 encoder configured for real-time low-complexity operation");
            }
            LogNative(L"ConfigureEncoderTransform codec API completed");

            LogNative(L"ConfigureEncoderTransform stream messages begin");
            unsigned long flushException = 0;
            HRESULT flushResult = SafeProcessMessage(transform, MFT_MESSAGE_COMMAND_FLUSH, 0, &flushException);
            if (flushException != 0)
            {
                std::wstringstream log;
                log << L"ConfigureEncoderTransform COMMAND_FLUSH SEH | Code=0x" << std::hex << flushException;
                LogNative(log.str());
            }
            ThrowIfFailed(flushResult);

            unsigned long beginStreamingException = 0;
            HRESULT beginStreamingResult = SafeProcessMessage(transform, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0, &beginStreamingException);
            if (beginStreamingException != 0)
            {
                std::wstringstream log;
                log << L"ConfigureEncoderTransform BEGIN_STREAMING SEH | Code=0x" << std::hex << beginStreamingException;
                LogNative(log.str());
            }
            ThrowIfFailed(beginStreamingResult);

            unsigned long startStreamException = 0;
            HRESULT startStreamResult = SafeProcessMessage(transform, MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0, &startStreamException);
            if (startStreamException != 0)
            {
                std::wstringstream log;
                log << L"ConfigureEncoderTransform START_OF_STREAM SEH | Code=0x" << std::hex << startStreamException;
                LogNative(log.str());
            }
            ThrowIfFailed(startStreamResult);
            LogNative(L"ConfigureEncoderTransform stream messages completed");
        }

        void CreateRecordingWriter(const wchar_t* path, const EcAudioStreamConfig* systemAudio, const EcAudioStreamConfig* microphoneAudio)
        {
            ComPtr<IMFAttributes> attributes;
            ThrowIfFailed(MFCreateAttributes(&attributes, 2));
            ThrowIfFailed(attributes->SetUINT32(MF_LOW_LATENCY, TRUE));
            ThrowIfFailed(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));

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

            recordingAudioEnabled_[0] = false;
            recordingAudioEnabled_[1] = false;
            recordingAudioStreamIndex_[0] = static_cast<DWORD>(-1);
            recordingAudioStreamIndex_[1] = static_cast<DWORD>(-1);

            const EcAudioStreamConfig* audioConfig = nullptr;
            int audioConfigIndex = -1;
            if (systemAudio != nullptr && systemAudio->enabled != 0)
            {
                audioConfig = systemAudio;
                audioConfigIndex = 0;
            }
            else if (microphoneAudio != nullptr && microphoneAudio->enabled != 0)
            {
                audioConfig = microphoneAudio;
                audioConfigIndex = 1;
            }

            ComPtr<IMFMediaType> audioOutputType;
            if (audioConfig != nullptr)
            {
                const UINT32 sampleRate = audioConfig->sampleRate;
                const UINT32 channels = audioConfig->channels;
                ThrowIfFailed(MFCreateMediaType(&audioOutputType));
                ThrowIfFailed(audioOutputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
                ThrowIfFailed(audioOutputType->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC));
                ThrowIfFailed(audioOutputType->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, channels));
                ThrowIfFailed(audioOutputType->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate));
                ThrowIfFailed(audioOutputType->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
                ThrowIfFailed(audioOutputType->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24'000));
            }

            ComPtr<IMFByteStream> byteStream;
            ThrowIfFailed(MFCreateFile(
                MF_ACCESSMODE_WRITE,
                MF_OPENMODE_DELETE_IF_EXIST,
                MF_FILEFLAGS_NONE,
                path,
                &byteStream));

            ComPtr<IMFMediaSink> mediaSink;
            ThrowIfFailed(MFCreateFMPEG4MediaSink(
                byteStream.Get(),
                outputType.Get(),
                audioOutputType.Get(),
                &mediaSink));

            ComPtr<IMFSinkWriter> writer;
            ThrowIfFailed(MFCreateSinkWriterFromMediaSink(
                mediaSink.Get(),
                attributes.Get(),
                &writer));

            constexpr DWORD videoStreamIndex = 0;
            ThrowIfFailed(writer->SetInputMediaType(videoStreamIndex, outputType.Get(), nullptr));

            if (audioConfig != nullptr)
            {
                constexpr DWORD audioStreamIndex = 1;
                ConfigureRecordingAudioInput(
                    writer.Get(),
                    audioConfig,
                    audioConfigIndex,
                    audioStreamIndex);
            }

            ThrowIfFailed(writer->BeginWriting());

            recordingWriter_ = writer;
            recordingMediaSink_ = mediaSink;
            recordingByteStream_ = byteStream;
            recordingStreamIndex_ = videoStreamIndex;
            LogNative(L"Fragmented MP4 encoded sample writer started.");
        }

        void ConfigureRecordingAudioInput(
            IMFSinkWriter* writer,
            const EcAudioStreamConfig* config,
            int index,
            DWORD streamIndex)
        {
            if (writer == nullptr || config == nullptr || config->enabled == 0) return;
            if (config->sampleRate == 0 || config->channels == 0 || config->bitsPerSample == 0) return;

            const UINT32 sampleRate = config->sampleRate;
            const UINT32 channels = config->channels;
            const UINT32 bitsPerSample = config->bitsPerSample;
            const UINT32 blockAlign = channels * bitsPerSample / 8;
            const UINT32 avgBytesPerSecond = sampleRate * blockAlign;

            ComPtr<IMFMediaType> inputType;
            ThrowIfFailed(MFCreateMediaType(&inputType));
            ThrowIfFailed(inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
            ThrowIfFailed(inputType->SetGUID(MF_MT_SUBTYPE, bitsPerSample == 32 ? MFAudioFormat_Float : MFAudioFormat_PCM));
            ThrowIfFailed(inputType->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, channels));
            ThrowIfFailed(inputType->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate));
            ThrowIfFailed(inputType->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, bitsPerSample));
            ThrowIfFailed(inputType->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, blockAlign));
            ThrowIfFailed(inputType->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, avgBytesPerSecond));

            ThrowIfFailed(writer->SetInputMediaType(streamIndex, inputType.Get(), nullptr));
            recordingAudioStreamIndex_[index] = streamIndex;
            recordingAudioEnabled_[index] = true;

            std::wstringstream message;
            message << L"MP4 audio stream configured | Index=" << index
                << L" | Stream=" << streamIndex
                << L" | SampleRate=" << sampleRate
                << L" | Channels=" << channels
                << L" | Bits=" << bitsPerSample;
            LogNative(message.str());
        }

        void ReleaseRecordingWriterNoThrow()
        {
            try
            {
                if (recordingWriter_ != nullptr)
                {
                    recordingWriter_.Reset();
                }
            }
            catch (...) {}
            try
            {
                if (recordingMediaSink_ != nullptr)
                {
                    recordingMediaSink_->Shutdown();
                    recordingMediaSink_.Reset();
                }
            }
            catch (...) {}
            recordingByteStream_.Reset();
        }

        static void SetStreamingSampleTiming(
            IMFSample* sample,
            int64_t sampleTime100ns,
            int64_t sampleDuration100ns,
            bool keyFrame)
        {
            ThrowIfFailed(sample->SetSampleTime(sampleTime100ns));
            ThrowIfFailed(sample->SetSampleDuration(sampleDuration100ns));
            ThrowIfFailed(sample->SetUINT32(MFSampleExtension_CleanPoint, keyFrame ? TRUE : FALSE));
        }

        static ComPtr<IMFSample> CreateAudioSample(const RecordingAudioSample& queued)
        {
            ComPtr<IMFMediaBuffer> buffer;
            ThrowIfFailed(MFCreateMemoryBuffer(static_cast<DWORD>(queued.bytes.size()), &buffer));
            BYTE* destination = nullptr;
            DWORD maxLength = 0;
            DWORD currentLength = 0;
            ThrowIfFailed(buffer->Lock(&destination, &maxLength, &currentLength));
            if (!queued.bytes.empty())
            {
                std::memcpy(destination, queued.bytes.data(), queued.bytes.size());
            }
            ThrowIfFailed(buffer->Unlock());
            ThrowIfFailed(buffer->SetCurrentLength(static_cast<DWORD>(queued.bytes.size())));

            ComPtr<IMFSample> sample;
            ThrowIfFailed(MFCreateSample(&sample));
            ThrowIfFailed(sample->AddBuffer(buffer.Get()));
            ThrowIfFailed(sample->SetSampleTime(queued.sampleTime100ns));
            ThrowIfFailed(sample->SetSampleDuration(queued.duration100ns));
            return sample;
        }

        void ClearRecordingAudioQueueNoLock()
        {
            recordingAudioQueue_.clear();
            recordingAudioQueuedBytes_ = 0;
            recordingAudioDroppedSamples_ = 0;
            recordingAudioQueuedLastTimestamp100ns_[0] = -1;
            recordingAudioQueuedLastTimestamp100ns_[1] = -1;
            recordingAudioLastTimestamp100ns_[0] = -1;
            recordingAudioLastTimestamp100ns_[1] = -1;
        }

        void ClearRecordingAudioQueue()
        {
            std::scoped_lock lock(packetMutex_);
            ClearRecordingAudioQueueNoLock();
        }

        void DrainRecordingAudioQueue(int64_t maxAudioEnd100ns)
        {
            uint64_t writtenSamples = 0;
            uint64_t trimmedSamples = 0;
            uint64_t droppedSamples = 0;

            for (;;)
            {
                RecordingAudioSample queued;
                DWORD streamIndex = static_cast<DWORD>(-1);
                bool hasSample = false;

                {
                    std::scoped_lock lock(packetMutex_);
                    if (recordingWriter_ == nullptr || recordingAudioQueue_.empty()) break;

                    RecordingAudioSample& front = recordingAudioQueue_.front();
                    if (front.sampleTime100ns >= maxAudioEnd100ns) break;

                    queued = std::move(front);
                    recordingAudioQueuedBytes_ -= queued.bytes.size();
                    recordingAudioQueue_.pop_front();

                    const int64_t sampleEnd100ns = queued.sampleTime100ns + queued.duration100ns;
                    if (sampleEnd100ns > maxAudioEnd100ns)
                    {
                        queued.duration100ns = maxAudioEnd100ns - queued.sampleTime100ns;
                        ++trimmedSamples;
                    }

                    if (queued.duration100ns <= 0 ||
                        queued.index < 0 || queued.index > 1 ||
                        !recordingAudioEnabled_[queued.index] ||
                        recordingAudioStreamIndex_[queued.index] == static_cast<DWORD>(-1))
                    {
                        ++droppedSamples;
                        continue;
                    }

                    streamIndex = recordingAudioStreamIndex_[queued.index];
                    hasSample = true;
                }

                if (!hasSample) continue;

                const auto writeStart = std::chrono::steady_clock::now();
                ComPtr<IMFSample> sample = CreateAudioSample(queued);
                ThrowIfFailed(recordingWriter_->WriteSample(streamIndex, sample.Get()));
                const double writeMs = ElapsedMilliseconds(writeStart);
                ++writtenSamples;

                {
                    std::scoped_lock lock(packetMutex_);
                    recordingAudioLastTimestamp100ns_[queued.index] = queued.sampleTime100ns;
                }

                if (writeMs >= 10.0)
                {
                    std::wstringstream details;
                    details << L"Stream=" << queued.index
                        << L" | Bytes=" << queued.bytes.size()
                        << L" | SampleTime100ns=" << queued.sampleTime100ns
                        << L" | Duration100ns=" << queued.duration100ns
                        << L" | WriteMs=" << std::fixed << std::setprecision(2) << writeMs;
                    LogNativeTimingIfSlow(L"Audio drain WriteSample", writeMs, 10.0, details.str());
                }
            }

            const auto diagnosticNow = std::chrono::steady_clock::now();
            const bool periodicDiagnostic = writtenSamples > 0 &&
                (lastAudioDrainDiagnostic_ == std::chrono::steady_clock::time_point{} ||
                    diagnosticNow - lastAudioDrainDiagnostic_ >= std::chrono::seconds(2));
            if (periodicDiagnostic || trimmedSamples > 0 || droppedSamples > 0)
            {
                lastAudioDrainDiagnostic_ = diagnosticNow;
                std::wstringstream details;
                details << L"Written=" << writtenSamples
                    << L" | Trimmed=" << trimmedSamples
                    << L" | Dropped=" << droppedSamples
                    << L" | MaxAudioEnd100ns=" << maxAudioEnd100ns;
                LogNative(L"Audio queue drained | " + details.str());
            }
        }

        EncodedFrame CreateEncodedFrameFromSample(
            IMFSample* sample,
            int64_t timestamp100ns,
            int64_t duration100ns,
            bool keyFrame)
        {
            ComPtr<IMFMediaBuffer> buffer;
            ThrowIfFailed(sample->ConvertToContiguousBuffer(&buffer));

            BYTE* data = nullptr;
            DWORD maxLength = 0;
            DWORD currentLength = 0;

            ThrowIfFailed(buffer->Lock(&data, &maxLength, &currentLength));

            EncodedFrame frame{};
            frame.timestamp100ns = timestamp100ns;
            frame.duration100ns = duration100ns;
            frame.keyFrame = keyFrame;
            frame.bytes.resize(currentLength);

            if (currentLength > 0)
            {
                std::memcpy(frame.bytes.data(), data, currentLength);
            }

            buffer->Unlock();

            frame.storageOffset = 0;
            frame.storageLength = static_cast<uint32_t>(frame.bytes.size());

            return frame;
        }

        void WriteRecordingSample(
            IMFSample* sourceSample,
            int64_t nativeTimestamp100ns,
            int64_t fallbackDuration100ns,
            bool keyFrame)
        {
            const auto callStart = std::chrono::steady_clock::now();
            std::unique_lock writerLock(recordingWriterMutex_);

            ComPtr<IMFSinkWriter> writer;
            DWORD streamIndex = 0;
            uint64_t frameNumber = 0;
            int64_t sampleDuration = 0;
            int64_t sampleTime100ns = 0;
            int64_t audioDrainEnd100ns = 0;
            double lockMs = 0;

            {
                const auto lockStart = std::chrono::steady_clock::now();
                std::unique_lock lock(packetMutex_);
                lockMs = ElapsedMilliseconds(lockStart);

                if ((!recording_ && !recordingPending_) || sourceSample == nullptr)
                {
                    return;
                }

                if (recordingPending_ && !keyFrame)
                {
                    return;
                }

                if (recordingStart100ns_ < 0)
                {
                    recordingPending_ = false;
                    recording_ = true;
                    recordingStart100ns_ = nativeTimestamp100ns;
                    recordingLastTimestamp100ns_ = nativeTimestamp100ns - fallbackDuration100ns;
                    recordingLastKeyFrameRequest100ns_ = nativeTimestamp100ns;

                    std::wstringstream message;
                    message << L"Native state | Action=Recording first-buffered-sample"
                        << L" | Timestamp=" << nativeTimestamp100ns
                        << L" | Duration=" << fallbackDuration100ns
                        << L" | KeyFrame=" << keyFrame
                        << L" | ReplayEnabled=" << ReplayEnabled()
                        << L" | Packets=" << packets_.size()
                        << L" | Captured=" << capturedFrames_.load()
                        << L" | Submitted=" << submittedFrames_.load()
                        << L" | Encoded=" << encodedFrames_.load();

                    LogNative(message.str());
                }

                if (nativeTimestamp100ns <= recordingLastTimestamp100ns_)
                {
                    nativeTimestamp100ns = recordingLastTimestamp100ns_ + 1;
                }

                sampleDuration = recordingFrameCount_ == 0
                    ? std::max<int64_t>(1, fallbackDuration100ns)
                    : std::max<int64_t>(1, nativeTimestamp100ns - recordingLastTimestamp100ns_);

                if (recordingLastKeyFrameRequest100ns_ < 0 ||
                    nativeTimestamp100ns - recordingLastKeyFrameRequest100ns_ >= TicksPerSecond)
                {
                    ForceKeyFrame();
                    recordingLastKeyFrameRequest100ns_ = nativeTimestamp100ns;
                }

                if (recordingWriter_ == nullptr)
                {
                    throw std::runtime_error("Recording writer is not available.");
                }

                sampleTime100ns = std::max<int64_t>(0, nativeTimestamp100ns - recordingStart100ns_);
                SetStreamingSampleTiming(sourceSample, sampleTime100ns, sampleDuration, keyFrame);
                writer = recordingWriter_;
                streamIndex = recordingStreamIndex_;
                frameNumber = recordingFrameCount_;
            }

            const auto writeStart = std::chrono::steady_clock::now();
            ThrowIfFailed(writer->WriteSample(streamIndex, sourceSample));
            const double writeMs = ElapsedMilliseconds(writeStart);

            {
                std::scoped_lock lock(packetMutex_);
                if (recordingWriter_ == writer && (recording_ || recordingPending_))
                {
                    recordingLastTimestamp100ns_ = nativeTimestamp100ns;
                    recordingEnd100ns_ = nativeTimestamp100ns + sampleDuration;
                    ++recordingFrameCount_;
                    audioDrainEnd100ns = std::max<int64_t>(0, CurrentTimestamp100ns() - recordingStart100ns_) + RecordingAudioLead100ns;
                }
            }

            const double totalMs = ElapsedMilliseconds(callStart);
            if (lockMs >= 5.0 || writeMs >= 10.0 || totalMs >= 20.0)
            {
                std::wstringstream details;
                details << L"Frame=" << frameNumber
                    << L" | KeyFrame=" << keyFrame
                    << L" | SampleTime100ns=" << sampleTime100ns
                    << L" | SampleDuration100ns=" << sampleDuration
                    << L" | LockMs=" << std::fixed << std::setprecision(2) << lockMs
                    << L" | WriteMs=" << writeMs
                    << L" | TotalMs=" << totalMs;
                LogNativeTimingIfSlow(L"Video WriteRecordingSample", totalMs, 5.0, details.str());
            }

            if (audioDrainEnd100ns > 0)
            {
                DrainRecordingAudioQueue(audioDrainEnd100ns);
            }

            {
                std::scoped_lock lock(packetMutex_);
                if (recordingFrameCount_ == 1 ||
                    recordingFrameCount_ % std::max<uint32_t>(1, config_.framesPerSecond) == 0)
                {
                    std::wstringstream message;
                    message << L"Native state | Action=Recording streamed-sample"
                        << L" | Frames=" << recordingFrameCount_
                        << L" | NativeTimestamp=" << nativeTimestamp100ns
                        << L" | SampleTime=" << sampleTime100ns
                        << L" | SampleDuration=" << sampleDuration
                        << L" | RecordingStart=" << recordingStart100ns_
                        << L" | RecordingEnd=" << recordingEnd100ns_
                        << L" | ReplayEnabled=" << ReplayEnabled();

                    LogNative(message.str());
                }
            }
        }

        void EncodeLoop()
        {
            DWORD mmcssTaskIndex = 0;
            HANDLE mmcssHandle = AvSetMmThreadCharacteristicsW(L"Capture", &mmcssTaskIndex);
            if (mmcssHandle != nullptr)
            {
                AvSetMmThreadPriority(mmcssHandle, AVRT_PRIORITY_HIGH);
                LogNative(L"EncodeLoop MMCSS enabled | Task=Capture | Priority=High");
            }
            else
            {
                SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
                LogNative(L"EncodeLoop MMCSS unavailable; using THREAD_PRIORITY_HIGHEST.");
            }

            try
            {
                LogNative(L"EncodeLoop started.");
                const int64_t frameDuration = TicksPerSecond / config_.framesPerSecond;
                const bool useMonitorCfrScheduler =
                    config_.targetKind == EcTargetKind::Monitor;
                const auto fixedFrameInterval =
                    std::chrono::duration_cast<std::chrono::steady_clock::duration>(
                        std::chrono::nanoseconds(frameDuration * 100));
                int64_t nextFrameDue100ns = -1;
                uint64_t monitorCfrFrameIndex = 0;
                std::chrono::steady_clock::time_point nextMonitorCfrWake{};
                std::chrono::steady_clock::time_point lastBlitDiagnostic{};
                std::chrono::steady_clock::time_point lastSubmitDiagnostic{};
                uint64_t lastEncodedTextureVersion = 0;
                size_t lastMonitorCfrSlot = std::numeric_limits<size_t>::max();

                while (running_)
                {
                    bool framePrepared = false;
                    bool selectedMonitorCfrSlot = false;
                    size_t selectedSlotIndex = std::numeric_limits<size_t>::max();
                    uint64_t preparedTextureVersion = 0;
                    int64_t preparedCaptureTimestamp100ns = 0;
                    ComPtr<ID3D11Texture2D> preparedEncoderTexture;
                    ComPtr<ID3D11VideoProcessorInputView> selectedProcessorInput;
                    size_t preparedEncoderSurface = std::numeric_limits<size_t>::max();

                    if (useMonitorCfrScheduler)
                    {
                        {
                            std::unique_lock lock(textureMutex_);
                            if (nextMonitorCfrWake == std::chrono::steady_clock::time_point{})
                            {
                                frameCondition_.wait(lock, [this]
                                    {
                                        return !running_ || !queuedMonitorFrameSlots_.empty();
                                });
                                if (!running_) break;
                                nextMonitorCfrWake = std::chrono::steady_clock::now();
                            }
                            else
                            {
                                lock.unlock();
                                nextMonitorCfrWake += fixedFrameInterval;
                                const auto now = std::chrono::steady_clock::now();
                                if (nextMonitorCfrWake + fixedFrameInterval < now)
                                {
                                    const auto lateBy = now - nextMonitorCfrWake;
                                    const auto skippedSlots = static_cast<uint64_t>(lateBy / fixedFrameInterval);
                                    monitorCfrFrameIndex += skippedSlots;
                                    droppedFrames_.fetch_add(skippedSlots);
                                    nextMonitorCfrWake += fixedFrameInterval * skippedSlots;
                                }
                                std::this_thread::sleep_until(nextMonitorCfrWake);
                                lock.lock();
                                if (!running_) break;
                            }

                            while (!queuedMonitorFrameSlots_.empty())
                            {
                                const size_t candidate = queuedMonitorFrameSlots_.front();
                                queuedMonitorFrameSlots_.pop_front();
                                if (candidate >= monitorFrameSlots_.size()) continue;

                                monitorFrameSlots_[candidate].queued = false;
                                if (monitorFrameSlots_[candidate].encoding) continue;

                                if (selectedSlotIndex != std::numeric_limits<size_t>::max())
                                {
                                    monitorFrameSlots_[selectedSlotIndex].encoding = false;
                                    droppedFrames_.fetch_add(1);
                                }

                                selectedSlotIndex = candidate;
                                monitorFrameSlots_[selectedSlotIndex].encoding = true;
                            }

                            if (selectedSlotIndex == std::numeric_limits<size_t>::max())
                            {
                                if (lastMonitorCfrSlot == std::numeric_limits<size_t>::max() ||
                                    lastMonitorCfrSlot >= monitorFrameSlots_.size() ||
                                    monitorFrameSlots_[lastMonitorCfrSlot].encoding)
                                {
                                    continue;
                                }

                                selectedSlotIndex = lastMonitorCfrSlot;
                                monitorFrameSlots_[selectedSlotIndex].encoding = true;
                            }

                            lastMonitorCfrSlot = selectedSlotIndex;
                            selectedProcessorInput = monitorFrameSlots_[selectedSlotIndex].inputView;
                            preparedCaptureTimestamp100ns =
                                static_cast<int64_t>(monitorCfrFrameIndex) * frameDuration;
                            ++monitorCfrFrameIndex;
                            preparedTextureVersion = textureVersion_;
                            selectedMonitorCfrSlot = true;
                        }

                        if (selectedProcessorInput == nullptr) continue;

                        ComPtr<ID3D11VideoProcessorOutputView> selectedProcessorOutput = processorOutput_;
                        if (captureBackend_ == CaptureBackend::DesktopDuplication)
                        {
                            if (useQsvEncoder_)
                            {
                                qsvEncoder_->DrainReady();
                            }
                            else if (asyncEncoder_)
                            {
                                PumpEncoderEvents(false, std::chrono::milliseconds(0));
                            }
                            preparedEncoderSurface = AcquireEncoderSurface();
                            if (preparedEncoderSurface == std::numeric_limits<size_t>::max())
                            {
                                if (useQsvEncoder_)
                                {
                                    qsvEncoder_->DrainReady();
                                }
                                else if (asyncEncoder_)
                                {
                                    PumpEncoderEvents(false, std::chrono::milliseconds(5));
                                }
                                preparedEncoderSurface = AcquireEncoderSurface();
                            }

                            if (preparedEncoderSurface == std::numeric_limits<size_t>::max())
                            {
                                encoderSurfaceStarvationFrames_.fetch_add(1);
                                droppedFrames_.fetch_add(1);
                                std::scoped_lock lock(textureMutex_);
                                monitorFrameSlots_[selectedSlotIndex].encoding = false;
                                frameCondition_.notify_one();
                                continue;
                            }

                            preparedEncoderTexture = encoderSurfaceSlots_[preparedEncoderSurface].texture;
                            selectedProcessorOutput = encoderSurfaceSlots_[preparedEncoderSurface].outputView;
                        }
                        else
                        {
                            preparedEncoderTexture = encoderTexture_;
                        }

                        D3D11_VIDEO_PROCESSOR_STREAM stream{};
                        stream.Enable = TRUE;
                        stream.pInputSurface = selectedProcessorInput.Get();
                        const auto blitStart = std::chrono::steady_clock::now();
                        const HRESULT blit = videoContext_->VideoProcessorBlt(processor_.Get(), selectedProcessorOutput.Get(), 0, 1, &stream);
                        const double blitMs = ElapsedMilliseconds(blitStart);
                        const auto blitDiagnosticNow = std::chrono::steady_clock::now();
                        if (blitMs >= 10.0 &&
                            (lastBlitDiagnostic == std::chrono::steady_clock::time_point{} ||
                                blitDiagnosticNow - lastBlitDiagnostic >= std::chrono::seconds(1)))
                        {
                            lastBlitDiagnostic = blitDiagnosticNow;
                            LogNativeTimingIfSlow(L"Monitor CFR VideoProcessorBlt", blitMs, 10.0);
                        }
                        if (FAILED(blit))
                        {
                            ReleaseEncoderSurface(preparedEncoderSurface);
                            preparedEncoderSurface = std::numeric_limits<size_t>::max();
                            SetError(HResultMessage(blit));
                        }
                        else
                        {
                            framePrepared = true;
                        }

                        if (selectedMonitorCfrSlot)
                        {
                            std::scoped_lock lock(textureMutex_);
                            if (selectedSlotIndex < monitorFrameSlots_.size())
                            {
                                monitorFrameSlots_[selectedSlotIndex].encoding = false;
                            }
                            frameCondition_.notify_one();
                        }
                    }
                    else
                    {
                        {
                            std::unique_lock lock(textureMutex_);
                            frameCondition_.wait(lock, [this, lastEncodedTextureVersion]
                                {
                                    return !running_ ||
                                        (config_.targetKind == EcTargetKind::Monitor && !queuedMonitorFrameSlots_.empty()) ||
                                        (config_.targetKind != EcTargetKind::Monitor && hasTexture_ && textureVersion_ != lastEncodedTextureVersion);
                                });
                            if (!running_) break;

                            if (config_.targetKind == EcTargetKind::Monitor)
                            {
                                if (queuedMonitorFrameSlots_.empty()) continue;
                                const size_t slotIndex = queuedMonitorFrameSlots_.front();
                                queuedMonitorFrameSlots_.pop_front();
                                if (slotIndex >= monitorFrameSlots_.size()) continue;
                                selectedProcessorInput = monitorFrameSlots_[slotIndex].inputView;
                                preparedCaptureTimestamp100ns = monitorFrameSlots_[slotIndex].captureTimestamp100ns;
                                preparedTextureVersion = textureVersion_;
                            }
                            else
                            {
                                if (!hasTexture_ || textureVersion_ == lastEncodedTextureVersion) continue;

                                if (config_.targetKind == EcTargetKind::Window)
                                {
                                    context_->CopyResource(encoderTexture_.Get(), blackTexture_.Get());
                                    if (!windowSourceInvalid_.load())
                                    {
                                        RenderWindowSourceToCanvas();
                                    }
                                }

                                selectedProcessorInput = processorInput_;
                                preparedCaptureTimestamp100ns = CurrentTimestamp100ns();
                                preparedTextureVersion = textureVersion_;
                            }

                            if (!(config_.targetKind == EcTargetKind::Window && windowSourceInvalid_.load()))
                            {
                                D3D11_VIDEO_PROCESSOR_STREAM stream{};
                                stream.Enable = TRUE;
                                stream.pInputSurface = selectedProcessorInput.Get();
                                const auto blitStart = std::chrono::steady_clock::now();
                                const HRESULT blit = videoContext_->VideoProcessorBlt(processor_.Get(), processorOutput_.Get(), 0, 1, &stream);
                                const double blitMs = ElapsedMilliseconds(blitStart);
                                LogNativeTimingIfSlow(L"VideoProcessorBlt", blitMs, 10.0);
                                if (FAILED(blit))
                                {
                                    SetError(HResultMessage(blit));
                                    continue;
                                }
                            }

                            preparedEncoderTexture = encoderTexture_;

                            framePrepared = true;
                        }
                    }

                    if (framePrepared)
                    {
                        if (!useMonitorCfrScheduler)
                        {
                            if (nextFrameDue100ns < 0)
                            {
                                nextFrameDue100ns = preparedCaptureTimestamp100ns;
                            }

                            const int64_t pacingTolerance100ns = std::max<int64_t>(1, frameDuration / 4);
                            if (preparedCaptureTimestamp100ns + pacingTolerance100ns < nextFrameDue100ns)
                            {
                                lastEncodedTextureVersion = preparedTextureVersion;
                                continue;
                            }

                            do
                            {
                                nextFrameDue100ns += frameDuration;
                            }
                            while (nextFrameDue100ns <= preparedCaptureTimestamp100ns);
                        }

                        try
                        {
                            const uint64_t submittedIndex = submittedFrames_.fetch_add(1);
                            AddDiagnosticTimestamp(submittedTimeline100ns_, preparedCaptureTimestamp100ns);
                            const int64_t submitTimestamp = useMonitorCfrScheduler
                                ? preparedCaptureTimestamp100ns
                                : static_cast<int64_t>(submittedIndex) * frameDuration;
                            const auto submitStart = std::chrono::steady_clock::now();
                            SubmitFrame(preparedEncoderTexture.Get(), submitTimestamp, frameDuration, preparedEncoderSurface);
                            const double submitMs = ElapsedMilliseconds(submitStart);
                            const auto drainStart = std::chrono::steady_clock::now();
                            if (useQsvEncoder_) qsvEncoder_->DrainReady();
                            else if (asyncEncoder_) PumpEncoderEvents(false, std::chrono::milliseconds(0));
                            else DrainSynchronousEncoder();
                            const double drainMs = ElapsedMilliseconds(drainStart);
                            const auto submitDiagnosticNow = std::chrono::steady_clock::now();
                            if ((submitMs >= 10.0 || drainMs >= 10.0) &&
                                (lastSubmitDiagnostic == std::chrono::steady_clock::time_point{} ||
                                    submitDiagnosticNow - lastSubmitDiagnostic >= std::chrono::seconds(1)))
                            {
                                lastSubmitDiagnostic = submitDiagnosticNow;
                                std::wstringstream details;
                                details << L"SubmitMs=" << std::fixed << std::setprecision(2) << submitMs
                                    << L" | DrainMs=" << drainMs
                                    << L" | Async=" << (useQsvEncoder_ || asyncEncoder_)
                                    << L" | EncoderBackend=" << (useQsvEncoder_ ? L"QSV" : L"MFT")
                                    << L" | MonitorCfr=" << useMonitorCfrScheduler
                                    << L" | Submitted=" << submittedFrames_.load()
                                    << L" | Encoded=" << encodedFrames_.load()
                                    << L" | Captured=" << capturedFrames_.load()
                                    << L" | TextureVersion=" << preparedTextureVersion;
                                LogNativeTimingIfSlow(L"Encode submit/drain", submitMs + drainMs, 10.0, details.str());
                            }
                            lastEncodedTextureVersion = preparedTextureVersion;
                        }
                        catch (const winrt::hresult_error& error)
                        {
                            ReleaseEncoderSurface(preparedEncoderSurface);
                            SetError(error.message().c_str());
                            droppedFrames_.fetch_add(1);
                            bool recordingActive = false;
                            {
                                std::scoped_lock lock(packetMutex_);
                                recordingActive = recording_ || recordingPending_;
                            }
                            if (recordingActive)
                            {
                                std::wstringstream message;
                                message << L"Fatal recording writer/encoder HRESULT | Code=0x" << std::hex
                                    << static_cast<uint32_t>(error.code())
                                    << L" | Message=" << error.message().c_str();
                                LogNative(message.str());
                                running_ = false;
                                frameCondition_.notify_all();
                                break;
                            }
                        }
                        catch (const std::exception& error)
                        {
                            ReleaseEncoderSurface(preparedEncoderSurface);
                            SetError(std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                            droppedFrames_.fetch_add(1);
                            bool recordingActive = false;
                            {
                                std::scoped_lock lock(packetMutex_);
                                recordingActive = recording_ || recordingPending_;
                            }
                            if (recordingActive)
                            {
                                LogNative(L"Fatal recording writer/encoder exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                                running_ = false;
                                frameCondition_.notify_all();
                                break;
                            }
                        }
                    }
                }
                if (useQsvEncoder_ && qsvEncoder_)
                {
                    try { qsvEncoder_->Flush(); }
                    catch (const std::exception& error)
                    {
                        LogNative(L"QSV drain failed | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                    }
                }
                else if (encoder_)
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
            catch (const winrt::hresult_error& error)
            {
                std::wstringstream log;
                log
                    << L"EncodeLoop fatal hresult_error | Code=0x" << std::hex
                    << static_cast<uint32_t>(error.code())
                    << L" | Message=" << error.message().c_str();
                LogNative(log.str());
                SetError(error.message().c_str());
                running_ = false;
            }
            catch (const std::exception& error)
            {
                LogNative(L"EncodeLoop fatal std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                SetError(std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                running_ = false;
            }
            catch (...)
            {
                LogNative(L"EncodeLoop fatal unknown exception.");
                SetError(L"Unexpected native encoder failure.");
                running_ = false;
            }

            LogNative(L"EncodeLoop exited.");
            if (mmcssHandle != nullptr)
            {
                AvRevertMmThreadCharacteristics(mmcssHandle);
            }
        }

        void SubmitFrame(
            ID3D11Texture2D* texture,
            int64_t timestamp,
            int64_t duration,
            size_t encoderSurfaceIndex)
        {
            if (useQsvEncoder_)
            {
                if (qsvEncoder_ == nullptr || encoderSurfaceIndex == std::numeric_limits<size_t>::max())
                    throw std::runtime_error("QSV submission does not have an encoder surface");

                submittedEncoderSurfaces_.push_back(encoderSurfaceIndex);
                try
                {
                    qsvEncoder_->Submit(
                        encoderSurfaceIndex,
                        timestamp,
                        duration,
                        qsvForceKeyFrame_.exchange(false));
                }
                catch (...)
                {
                    const auto found = std::find(
                        submittedEncoderSurfaces_.rbegin(),
                        submittedEncoderSurfaces_.rend(),
                        encoderSurfaceIndex);
                    if (found != submittedEncoderSurfaces_.rend())
                    {
                        submittedEncoderSurfaces_.erase(std::next(found).base());
                    }
                    throw;
                }
                return;
            }

            ComPtr<IMFMediaBuffer> buffer;
            ThrowIfFailed(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), texture, 0, FALSE, &buffer));
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
                if (encoderSurfaceIndex != std::numeric_limits<size_t>::max())
                {
                    submittedEncoderSurfaces_.push_back(encoderSurfaceIndex);
                }
                return;
            }

            HRESULT result = encoder_->ProcessInput(0, sample.Get(), 0);
            if (result == MF_E_NOTACCEPTING)
            {
                DrainSynchronousEncoder();
                result = encoder_->ProcessInput(0, sample.Get(), 0);
            }
            ThrowIfFailed(result);
            if (encoderSurfaceIndex != std::numeric_limits<size_t>::max())
            {
                submittedEncoderSurfaces_.push_back(encoderSurfaceIndex);
            }
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
            if (sample != nullptr)
            {
                if (!submittedEncoderSurfaces_.empty())
                {
                    const size_t completedSurface = submittedEncoderSurfaces_.front();
                    submittedEncoderSurfaces_.pop_front();
                    ReleaseEncoderSurface(completedSurface);
                }
                ConsumeEncodedSample(sample.Get());
            }
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
            const int64_t configuredFrameDuration = TicksPerSecond / std::max<uint32_t>(1, config_.framesPerSecond);
            int64_t timestamp = CurrentTimestamp100ns();
            int64_t duration = configuredFrameDuration;

            if (config_.targetKind == EcTargetKind::Monitor)
            {
                int64_t sampleTimestamp = 0;
                if (SUCCEEDED(sample->GetSampleTime(&sampleTimestamp)))
                {
                    timestamp = sampleTimestamp;
                }

                int64_t sampleDuration = 0;
                if (SUCCEEDED(sample->GetSampleDuration(&sampleDuration)) && sampleDuration > 0)
                {
                    duration = sampleDuration;
                }
            }
            else
            {
                if (lastPacketTimestamp100ns_ >= 0 && timestamp <= lastPacketTimestamp100ns_) timestamp = lastPacketTimestamp100ns_ + 1;
                duration = lastPacketTimestamp100ns_ >= 0
                    ? std::max<int64_t>(1, timestamp - lastPacketTimestamp100ns_)
                    : configuredFrameDuration;
            }

            if (lastPacketTimestamp100ns_ >= 0 && timestamp <= lastPacketTimestamp100ns_)
            {
                timestamp = lastPacketTimestamp100ns_ + std::max<int64_t>(1, duration);
            }

            lastPacketTimestamp100ns_ = timestamp;

            UINT32 cleanPoint = FALSE;
            sample->GetUINT32(MFSampleExtension_CleanPoint, &cleanPoint);
            WriteRecordingSample(sample, timestamp, duration, cleanPoint != FALSE);

            ComPtr<IMFMediaBuffer> buffer;
            ThrowIfFailed(sample->ConvertToContiguousBuffer(&buffer));
            BYTE* data = nullptr;
            DWORD maxLength = 0;
            DWORD currentLength = 0;
            ThrowIfFailed(buffer->Lock(&data, &maxLength, &currentLength));
            EncodedFrame frame;
            try
            {
                frame.bytes.assign(data, data + currentLength);
            }
            catch (...)
            {
                buffer->Unlock();
                throw;
            }
            buffer->Unlock();
            frame.timestamp100ns = timestamp;
            frame.duration100ns = duration;
            frame.keyFrame = cleanPoint != FALSE;
            if (ReplayEnabled()) AddPacket(std::move(frame));
            AddDiagnosticTimestamp(encodedTimeline100ns_, timestamp);
            encodedFrames_.fetch_add(1);
        }

        void StartQsvPacketWriter()
        {
            std::scoped_lock lock(qsvPacketMutex_);
            qsvPacketQueue_.clear();
            qsvPacketsQueued_ = 0;
            qsvPacketsProcessed_ = 0;
            qsvPacketWriterStopping_ = false;
            qsvPacketWriterFailed_ = false;
            qsvPacketWriterThread_ = std::thread([this] { QsvPacketWriterLoop(); });
        }

        void StopQsvPacketWriter()
        {
            {
                std::scoped_lock lock(qsvPacketMutex_);
                qsvPacketWriterStopping_ = true;
            }
            qsvPacketCondition_.notify_all();
            qsvPacketDrainedCondition_.notify_all();
            if (qsvPacketWriterThread_.joinable())
            {
                qsvPacketWriterThread_.join();
            }
        }

        bool WaitForQsvPacketWriterSnapshot()
        {
            if (!useQsvEncoder_ || !qsvPacketWriterThread_.joinable()) return true;
            std::unique_lock lock(qsvPacketMutex_);
            const uint64_t target = qsvPacketsQueued_;
            qsvPacketDrainedCondition_.wait(lock, [this, target]
                {
                    return qsvPacketsProcessed_ >= target || qsvPacketWriterFailed_;
                });
            return !qsvPacketWriterFailed_;
        }

        void QsvPacketWriterLoop()
        {
            const HRESULT apartmentResult = RoInitialize(RO_INIT_MULTITHREADED);
            const bool apartmentInitialized = SUCCEEDED(apartmentResult);
            std::chrono::steady_clock::time_point lastQueueDiagnostic{};
            LogNative(L"QSV packet writer thread started.");

            for (;;)
            {
                QsvEncodedPacket packet;
                {
                    std::unique_lock lock(qsvPacketMutex_);
                    qsvPacketCondition_.wait(lock, [this]
                        {
                            return qsvPacketWriterStopping_ || !qsvPacketQueue_.empty();
                        });
                    if (qsvPacketQueue_.empty())
                    {
                        if (qsvPacketWriterStopping_) break;
                        continue;
                    }
                    packet = std::move(qsvPacketQueue_.front());
                    qsvPacketQueue_.pop_front();
                    qsvPacketCondition_.notify_all();
                }

                bool processingFailed = false;
                try
                {
                    ProcessQsvPacket(std::move(packet));
                }
                catch (const winrt::hresult_error& error)
                {
                    SetError(error.message().c_str());
                    LogNative(L"QSV packet writer fatal HRESULT | " + std::wstring(error.message().c_str()));
                    running_ = false;
                    std::scoped_lock lock(qsvPacketMutex_);
                    qsvPacketWriterFailed_ = true;
                    processingFailed = true;
                }
                catch (const std::exception& error)
                {
                    const std::wstring message(error.what(), error.what() + std::char_traits<char>::length(error.what()));
                    SetError(message);
                    LogNative(L"QSV packet writer fatal exception | " + message);
                    running_ = false;
                    std::scoped_lock lock(qsvPacketMutex_);
                    qsvPacketWriterFailed_ = true;
                    processingFailed = true;
                }
                catch (...)
                {
                    SetError(L"Unexpected QSV packet writer failure.");
                    LogNative(L"QSV packet writer fatal unknown exception.");
                    running_ = false;
                    std::scoped_lock lock(qsvPacketMutex_);
                    qsvPacketWriterFailed_ = true;
                    processingFailed = true;
                }

                uint64_t processed = 0;
                size_t queueDepth = 0;
                {
                    std::scoped_lock lock(qsvPacketMutex_);
                    ++qsvPacketsProcessed_;
                    processed = qsvPacketsProcessed_;
                    queueDepth = qsvPacketQueue_.size();
                }
                qsvPacketDrainedCondition_.notify_all();
                if (processingFailed) break;

                const auto diagnosticNow = std::chrono::steady_clock::now();
                if (lastQueueDiagnostic == std::chrono::steady_clock::time_point{} ||
                    diagnosticNow - lastQueueDiagnostic >= std::chrono::seconds(2))
                {
                    lastQueueDiagnostic = diagnosticNow;
                    std::wstringstream message;
                    message << L"QSV packet writer status | Processed=" << processed
                        << L" | QueueDepth=" << queueDepth
                        << L" | Capacity=" << MaxQsvPacketQueue;
                    LogNative(message.str());
                }
            }

            qsvPacketCondition_.notify_all();
            qsvPacketDrainedCondition_.notify_all();
            if (apartmentInitialized) RoUninitialize();
            LogNative(L"QSV packet writer thread exited.");
        }

        void ConsumeQsvPacket(QsvEncodedPacket&& packet)
        {
            if (!submittedEncoderSurfaces_.empty())
            {
                const size_t completedSurface = submittedEncoderSurfaces_.front();
                submittedEncoderSurfaces_.pop_front();
                ReleaseEncoderSurface(completedSurface);
            }

            std::unique_lock lock(qsvPacketMutex_);
            qsvPacketCondition_.wait(lock, [this]
                {
                    return qsvPacketQueue_.size() < MaxQsvPacketQueue ||
                        qsvPacketWriterStopping_ || qsvPacketWriterFailed_;
                });
            if (qsvPacketWriterStopping_ || qsvPacketWriterFailed_)
                throw std::runtime_error("QSV packet writer is not available");
            qsvPacketQueue_.push_back(std::move(packet));
            ++qsvPacketsQueued_;
            lock.unlock();
            qsvPacketCondition_.notify_one();
        }

        void ProcessQsvPacket(QsvEncodedPacket&& packet)
        {

            ComPtr<IMFMediaBuffer> buffer;
            ThrowIfFailed(MFCreateMemoryBuffer(static_cast<DWORD>(packet.bytes.size()), &buffer));
            BYTE* destination = nullptr;
            DWORD maxLength = 0;
            DWORD currentLength = 0;
            ThrowIfFailed(buffer->Lock(&destination, &maxLength, &currentLength));
            if (!packet.bytes.empty()) std::memcpy(destination, packet.bytes.data(), packet.bytes.size());
            ThrowIfFailed(buffer->Unlock());
            ThrowIfFailed(buffer->SetCurrentLength(static_cast<DWORD>(packet.bytes.size())));

            ComPtr<IMFSample> sample;
            ThrowIfFailed(MFCreateSample(&sample));
            ThrowIfFailed(sample->AddBuffer(buffer.Get()));
            ThrowIfFailed(sample->SetSampleTime(packet.timestamp100ns));
            ThrowIfFailed(sample->SetSampleDuration(packet.duration100ns));
            ThrowIfFailed(sample->SetUINT32(MFSampleExtension_CleanPoint, packet.keyFrame ? TRUE : FALSE));

            int64_t timestamp = packet.timestamp100ns;
            const int64_t duration = std::max<int64_t>(1, packet.duration100ns);
            if (lastPacketTimestamp100ns_ >= 0 && timestamp <= lastPacketTimestamp100ns_)
            {
                timestamp = lastPacketTimestamp100ns_ + duration;
            }
            lastPacketTimestamp100ns_ = timestamp;

            WriteRecordingSample(sample.Get(), timestamp, duration, packet.keyFrame);

            EncodedFrame frame;
            frame.bytes = std::move(packet.bytes);
            frame.timestamp100ns = timestamp;
            frame.duration100ns = duration;
            frame.keyFrame = packet.keyFrame;
            if (ReplayEnabled()) AddPacket(std::move(frame));
            AddDiagnosticTimestamp(encodedTimeline100ns_, timestamp);
            encodedFrames_.fetch_add(1);
        }

        void AddPacket(EncodedFrame frame)
        {
            std::scoped_lock lock(packetMutex_);
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
            if (config_.targetKind == EcTargetKind::Monitor && !packets_.empty())
            {
                packets_.back().duration100ns = std::max<int64_t>(
                    1,
                    frame.timestamp100ns - packets_.back().timestamp100ns);
            }
            packets_.push_back(std::move(frame));
            ++activeSpoolFrameCount_;
            if (activeSpoolFrameCount_ == 1 || activeSpoolFrameCount_ % std::max<uint32_t>(1, config_.framesPerSecond * 2) == 0)
            {
                std::wstringstream message;
                message << L"Native state | Action=Replay packet-added | SpoolFrames=" << activeSpoolFrameCount_
                    << L" | Packets=" << packets_.size()
                    << L" | PacketTimestamp=" << packets_.back().timestamp100ns
                    << L" | PacketDuration=" << packets_.back().duration100ns
                    << L" | Recording=" << recording_
                    << L" | RecordingPending=" << recordingPending_
                    << L" | RecordingFrames=" << recordingFrameCount_;
                LogNative(message.str());
            }
            const int64_t cutoff = packets_.back().timestamp100ns - static_cast<int64_t>(config_.replaySeconds + 2) * TicksPerSecond;
            while (packets_.size() > config_.framesPerSecond && packets_.front().timestamp100ns < cutoff)
            {
                bufferedBytes_.fetch_sub(packets_.front().storageLength);
                packets_.pop_front();
            }
            packets_.back().bytes.clear();
            packets_.back().bytes.shrink_to_fit();
        }

        int64_t CurrentTimestamp100ns() const
        {
            const auto elapsed = std::chrono::steady_clock::now() - encodeClockStart_;
            return std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count() / 100;
        }

        void ResetCaptureDiagnostics()
        {
            std::scoped_lock lock(diagnosticsMutex_);
            capturedTimeline100ns_.clear();
            submittedTimeline100ns_.clear();
            encodedTimeline100ns_.clear();
            monitorQueueOverflowFrames_.store(0);
            monitorQueueMaxDepth_.store(0);
            encoderSurfaceStarvationFrames_.store(0);
        }

        void AddDiagnosticTimestamp(std::deque<int64_t>& timeline, int64_t timestamp100ns)
        {
            std::scoped_lock lock(diagnosticsMutex_);
            timeline.push_back(timestamp100ns);
            TrimDiagnosticTimelineLocked(timeline, timestamp100ns);
        }

        void TrimDiagnosticTimelineLocked(std::deque<int64_t>& timeline, int64_t newestTimestamp100ns) const
        {
            const int64_t retention100ns =
                (static_cast<int64_t>(std::max<uint32_t>(config_.replaySeconds, 1)) + 10LL) * TicksPerSecond;
            const int64_t minimumTimestamp100ns = newestTimestamp100ns - retention100ns;
            while (!timeline.empty() && timeline.front() < minimumTimestamp100ns)
            {
                timeline.pop_front();
            }
        }

        static size_t CountTimelineRange(
            const std::deque<int64_t>& timeline,
            int64_t startTimestamp100ns,
            int64_t endTimestamp100ns)
        {
            return static_cast<size_t>(std::count_if(
                timeline.begin(),
                timeline.end(),
                [startTimestamp100ns, endTimestamp100ns](int64_t timestamp)
                {
                    return timestamp >= startTimestamp100ns && timestamp < endTimestamp100ns;
                }));
        }

        void LogReplayCaptureDiagnostics(
            uint32_t requestedSeconds,
            int64_t windowStart100ns,
            int64_t windowEnd100ns,
            const std::vector<EncodedFrame>& frames)
        {
            const int64_t windowDuration100ns = std::max<int64_t>(1, windowEnd100ns - windowStart100ns);
            const double windowSeconds = static_cast<double>(windowDuration100ns) / static_cast<double>(TicksPerSecond);
            const uint64_t expectedFrames = static_cast<uint64_t>(
                std::llround(windowSeconds * static_cast<double>(config_.framesPerSecond)));

            size_t capturedInWindow = 0;
            size_t submittedInWindow = 0;
            size_t encodedInWindow = 0;
            {
                std::scoped_lock lock(diagnosticsMutex_);
                capturedInWindow = CountTimelineRange(capturedTimeline100ns_, windowStart100ns, windowEnd100ns);
                submittedInWindow = CountTimelineRange(submittedTimeline100ns_, windowStart100ns, windowEnd100ns);
                encodedInWindow = CountTimelineRange(encodedTimeline100ns_, windowStart100ns, windowEnd100ns);
            }

            const int64_t idealFrameDuration100ns = TicksPerSecond / std::max<uint32_t>(1, config_.framesPerSecond);
            size_t gapsOverOneAndHalfFrames = 0;
            size_t gapsOverTwoFrames = 0;
            size_t gapsOverThreeFrames = 0;
            int64_t largestGap100ns = 0;

            for (size_t index = 1; index < frames.size(); ++index)
            {
                const int64_t gap100ns = frames[index].timestamp100ns - frames[index - 1].timestamp100ns;
                largestGap100ns = std::max(largestGap100ns, gap100ns);
                if (gap100ns > idealFrameDuration100ns * 3 / 2) ++gapsOverOneAndHalfFrames;
                if (gap100ns > idealFrameDuration100ns * 2) ++gapsOverTwoFrames;
                if (gap100ns > idealFrameDuration100ns * 3) ++gapsOverThreeFrames;
            }

            const uint64_t replayFrames = static_cast<uint64_t>(frames.size());
            const uint64_t missingFrames = expectedFrames > replayFrames ? expectedFrames - replayFrames : 0;
            const int64_t capturedToSubmittedLoss = static_cast<int64_t>(capturedInWindow) - static_cast<int64_t>(submittedInWindow);
            const int64_t submittedToEncodedLoss = static_cast<int64_t>(submittedInWindow) - static_cast<int64_t>(encodedInWindow);
            const int64_t encodedToReplayLoss = static_cast<int64_t>(encodedInWindow) - static_cast<int64_t>(replayFrames);

            const wchar_t* likelyBottleneck = L"none";
            if (missingFrames > 0)
            {
                if (capturedInWindow + 2 < expectedFrames)
                {
                    likelyBottleneck = L"WGC/capture source did not deliver enough frames";
                }
                else if (capturedToSubmittedLoss > static_cast<int64_t>(std::max<uint64_t>(3, missingFrames / 4)))
                {
                    likelyBottleneck = L"EncodeLoop did not submit enough captured frames";
                }
                else if (submittedToEncodedLoss > static_cast<int64_t>(std::max<uint64_t>(3, missingFrames / 4)))
                {
                    likelyBottleneck = L"H.264 encoder did not output enough submitted frames";
                }
                else if (encodedToReplayLoss > 3)
                {
                    likelyBottleneck = L"Replay buffer/export window excluded encoded frames";
                }
                else
                {
                    likelyBottleneck = L"capture intervals are irregular inside the replay window";
                }
            }

            std::wstringstream log;
            log
                << L"Replay frame diagnostics | RequestedSec=" << requestedSeconds
                << L" | WindowSec=" << std::fixed << std::setprecision(3) << windowSeconds
                << L" | ConfiguredFps=" << config_.framesPerSecond
                << L" | ExpectedFrames=" << expectedFrames
                << L" | WgcCapturedInWindow=" << capturedInWindow
                << L" | SubmittedInWindow=" << submittedInWindow
                << L" | EncodedInWindow=" << encodedInWindow
                << L" | ReplayFrames=" << replayFrames
                << L" | MissingForConfiguredFps=" << missingFrames
                << L" | CapturedMinusSubmitted=" << capturedToSubmittedLoss
                << L" | SubmittedMinusEncoded=" << submittedToEncodedLoss
                << L" | EncodedMinusReplay=" << encodedToReplayLoss
                << L" | MonitorQueueOverflow=" << monitorQueueOverflowFrames_.load()
                << L" | MonitorQueueMaxDepth=" << monitorQueueMaxDepth_.load()
                << L" | EncoderSurfaceStarvation=" << encoderSurfaceStarvationFrames_.load()
                << L" | GapsOver1_5Frames=" << gapsOverOneAndHalfFrames
                << L" | GapsOver2Frames=" << gapsOverTwoFrames
                << L" | GapsOver3Frames=" << gapsOverThreeFrames
                << L" | LargestGapMs=" << static_cast<double>(largestGap100ns) / 10'000.0
                << L" | LikelyBottleneck=" << likelyBottleneck;
            LogNative(log.str());
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

        bool WriteFramesToMp4(
            const wchar_t* path,
            const std::vector<EncodedFrame>& frames,
            int64_t windowStart100ns,
            int64_t windowEnd100ns)
        {
            if (frames.empty()) return false;
            if (windowEnd100ns <= windowStart100ns) return false;

            try
            {
                ComPtr<IMFAttributes> attributes;
                ThrowIfFailed(MFCreateAttributes(&attributes, 1));
                ThrowIfFailed(attributes->SetUINT32(MF_LOW_LATENCY, TRUE));

                ComPtr<IMFSinkWriter> writer;
                ThrowIfFailed(MFCreateSinkWriterFromURL(path, nullptr, attributes.Get(), &writer));

                ComPtr<IMFMediaType> videoType;
                ThrowIfFailed(MFCreateMediaType(&videoType));
                ThrowIfFailed(videoType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
                ThrowIfFailed(videoType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
                ThrowIfFailed(videoType->SetUINT32(MF_MT_AVG_BITRATE, config_.bitrateKbps * 1000));
                ThrowIfFailed(videoType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
                ThrowIfFailed(MFSetAttributeSize(videoType.Get(), MF_MT_FRAME_SIZE, config_.outputWidth, config_.outputHeight));
                ThrowIfFailed(MFSetAttributeRatio(videoType.Get(), MF_MT_FRAME_RATE, config_.framesPerSecond, 1));
                ThrowIfFailed(MFSetAttributeRatio(videoType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
                ConfigureSdrBt709ColorMetadata(videoType.Get());

                DWORD streamIndex = 0;
                ThrowIfFailed(writer->AddStream(videoType.Get(), &streamIndex));
                ThrowIfFailed(writer->SetInputMediaType(streamIndex, videoType.Get(), nullptr));
                ThrowIfFailed(writer->BeginWriting());

                std::wstring currentStoragePath;
                std::ifstream input;
                std::vector<uint8_t> bytes;

                for (const auto& frame : frames)
                {
                    if (!frame.bytes.empty())
                    {
                        bytes = frame.bytes;
                    }
                    else
                    {
                        if (frame.storage == nullptr || frame.storageLength == 0)
                        {
                            SetError(L"Encoded frame data is unavailable.");
                            return false;
                        }

                        if (frame.storage->path != currentStoragePath)
                        {
                            input.close();
                            currentStoragePath = frame.storage->path;
                            input.open(std::filesystem::path(currentStoragePath), std::ios::binary);
                            if (!input)
                            {
                                SetError(L"Could not open replay spool file.");
                                return false;
                            }
                        }

                        bytes.resize(frame.storageLength);
                        input.seekg(static_cast<std::streamoff>(frame.storageOffset), std::ios::beg);
                        input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
                        if (input.gcount() != static_cast<std::streamsize>(bytes.size()))
                        {
                            SetError(L"Could not read replay spool packet.");
                            return false;
                        }
                    }

                    ComPtr<IMFMediaBuffer> buffer;
                    ThrowIfFailed(MFCreateMemoryBuffer(static_cast<DWORD>(bytes.size()), &buffer));

                    BYTE* destination = nullptr;
                    DWORD maxLength = 0;
                    DWORD currentLength = 0;

                    ThrowIfFailed(buffer->Lock(&destination, &maxLength, &currentLength));
                    std::memcpy(destination, bytes.data(), bytes.size());
                    ThrowIfFailed(buffer->Unlock());
                    ThrowIfFailed(buffer->SetCurrentLength(static_cast<DWORD>(bytes.size())));

                    ComPtr<IMFSample> sample;
                    ThrowIfFailed(MFCreateSample(&sample));
                    ThrowIfFailed(sample->AddBuffer(buffer.Get()));

                    const int64_t frameStart100ns = std::max(frame.timestamp100ns, windowStart100ns);
                    const int64_t frameEnd100ns = std::min(
                        frame.timestamp100ns + std::max<int64_t>(1, frame.duration100ns),
                        windowEnd100ns);

                    const int64_t sampleTime100ns = std::max<int64_t>(0, frameStart100ns - windowStart100ns);
                    const int64_t sampleDuration100ns = std::max<int64_t>(1, frameEnd100ns - frameStart100ns);

                    ThrowIfFailed(sample->SetSampleTime(sampleTime100ns));
                    ThrowIfFailed(sample->SetSampleDuration(sampleDuration100ns));
                    ThrowIfFailed(sample->SetUINT32(MFSampleExtension_CleanPoint, frame.keyFrame ? TRUE : FALSE));
                    ThrowIfFailed(writer->WriteSample(streamIndex, sample.Get()));
                }

                ThrowIfFailed(writer->Finalize());
                LogNative(L"MP4 export completed.");
                return true;
            }
            catch (const winrt::hresult_error& error)
            {
                std::wstringstream message;
                message << L"MP4 export failed with HRESULT 0x" << std::hex
                    << static_cast<uint32_t>(error.code()) << L": " << error.message().c_str();
                SetError(message.str());
                LogNative(message.str());
                return false;
            }
            catch (const std::exception& error)
            {
                SetError(std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                LogNative(L"MP4 export failed | std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                return false;
            }
            catch (...)
            {
                SetError(L"Unknown MP4 export failure.");
                LogNative(L"MP4 export failed | unknown exception");
                return false;
            }
        }

        void ForceKeyFrame()
        {
            if (useQsvEncoder_)
            {
                qsvForceKeyFrame_.store(true);
                return;
            }
            SetVariantUInt32(codecApi_.Get(), CODECAPI_AVEncVideoForceKeyFrame, 1);
        }

        void StopCore()
        {
            LogNative(L"StopCore entered.");
            running_ = false;
            frameCondition_.notify_all();

            if (desktopDuplicationThread_.joinable())
            {
                if (desktopDuplicationThread_.get_id() == std::this_thread::get_id())
                {
                    LogNative(L"StopCore skipped desktopDuplicationThread join because current thread is desktopDuplicationThread.");
                    desktopDuplicationThread_.detach();
                }
                else
                {
                    try
                    {
                        LogNative(L"StopCore joining desktopDuplicationThread.");
                        desktopDuplicationThread_.join();
                        LogNative(L"StopCore joined desktopDuplicationThread.");
                    }
                    catch (const std::exception& error)
                    {
                        LogNative(L"StopCore desktopDuplicationThread join failed | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                    }
                }
            }

            if (encoderThread_.joinable())
            {
                if (encoderThread_.get_id() == std::this_thread::get_id())
                {
                    LogNative(L"StopCore skipped encoderThread join because current thread is encoderThread.");
                    encoderThread_.detach();
                }
                else
                {
                    try
                    {
                        LogNative(L"StopCore joining encoderThread.");
                        encoderThread_.join();
                        LogNative(L"StopCore joined encoderThread.");
                    }
                    catch (const std::exception& error)
                    {
                        LogNative(L"StopCore encoderThread join failed | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
                    }
                }
            }

            StopQsvPacketWriter();

            if (recordingStream_.is_open()) recordingStream_.close();
            ReleaseRecordingWriterNoThrow();
            if (activeSpoolStream_.is_open()) activeSpoolStream_.close();
            recording_ = false;
            recordingPending_ = false;
            {
                std::scoped_lock packetLock(packetMutex_);
                packets_.clear();
                ClearRecordingAudioQueueNoLock();
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
            desktopDuplication_.Reset();
            qsvEncoder_.reset();
            useQsvEncoder_ = false;
            encoder_.Reset();
            codecApi_.Reset();
            eventGenerator_.Reset();
            processorOutput_.Reset();
            processorInput_.Reset();
            processor_.Reset();
            processorEnumerator_.Reset();
            encoderSurfaceSlots_.clear();
            submittedEncoderSurfaces_.clear();
            encoderTexture_.Reset();
            blackTexture_.Reset();
            compositorSampler_.Reset();
            compositorVertexBuffer_.Reset();
            compositorInputLayout_.Reset();
            compositorPixelShader_.Reset();
            compositorVertexShader_.Reset();
            canvasRenderTarget_.Reset();
            canvasTexture_.Reset();
            latestShaderResource_.Reset();
            latestTexture_.Reset();
            latestRenderTarget_.Reset();
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

            LogNative(L"StopCore exited.");
        }

        void SetError(const std::wstring& message) const
        {
            std::scoped_lock lock(errorMutex_);
            lastError_ = message;
        }

        EcVideoConfig config_{};
        CaptureBackend captureBackend_{ CaptureBackend::WindowsGraphicsCapture };
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
        LUID encoderAdapterLuid_{};
        bool hasEncoderAdapterLuid_{};
        bool encoderAdapterIsIntel_{};
        ComPtr<IMFDXGIDeviceManager> deviceManager_;
        UINT deviceManagerToken_{};
        IDirect3DDevice winRtDevice_{ nullptr };
        GraphicsCaptureItem captureItem_{ nullptr };
        Direct3D11CaptureFramePool framePool_{ nullptr };
        GraphicsCaptureSession captureSession_{ nullptr };
        winrt::event_token frameToken_{};
        ComPtr<IDXGIOutputDuplication> desktopDuplication_;
        std::thread desktopDuplicationThread_;

        mutable std::mutex textureMutex_;
        std::condition_variable frameCondition_;
        ComPtr<ID3D11Texture2D> latestTexture_;
        ComPtr<ID3D11RenderTargetView> latestRenderTarget_;
        ComPtr<ID3D11ShaderResourceView> latestShaderResource_;
        ComPtr<ID3D11Texture2D> canvasTexture_;
        ComPtr<ID3D11RenderTargetView> canvasRenderTarget_;
        ComPtr<ID3D11VertexShader> compositorVertexShader_;
        ComPtr<ID3D11PixelShader> compositorPixelShader_;
        ComPtr<ID3D11InputLayout> compositorInputLayout_;
        ComPtr<ID3D11Buffer> compositorVertexBuffer_;
        ComPtr<ID3D11SamplerState> compositorSampler_;
        ComPtr<ID3D11Texture2D> encoderTexture_;
        std::vector<EncoderSurfaceSlot> encoderSurfaceSlots_;
        std::deque<size_t> submittedEncoderSurfaces_;
        size_t nextEncoderSurface_{};
        ComPtr<ID3D11Texture2D> blackTexture_;
        ComPtr<ID3D11VideoProcessorEnumerator> processorEnumerator_;
        ComPtr<ID3D11VideoProcessor> processor_;
        ComPtr<ID3D11VideoProcessorInputView> processorInput_;
        ComPtr<ID3D11VideoProcessorOutputView> processorOutput_;
        uint32_t sourceWidth_{};
        uint32_t sourceHeight_{};
        DXGI_MODE_ROTATION desktopDuplicationRotation_{ DXGI_MODE_ROTATION_IDENTITY };
        uint32_t sourceContentWidth_{};
        uint32_t sourceContentHeight_{};
        uint32_t windowFrameWidth_{};
        uint32_t windowFrameHeight_{};
        bool hasTexture_{ false };
        uint64_t textureVersion_{};
        std::vector<MonitorFrameSlot> monitorFrameSlots_;
        std::deque<size_t> queuedMonitorFrameSlots_;
        size_t nextMonitorFrameSlot_{};
        HMONITOR initialWindowMonitor_{ nullptr };
        std::atomic_bool windowSourceInvalid_{ false };

        ComPtr<IMFTransform> encoder_;
        std::unique_ptr<QsvEncoder> qsvEncoder_;
        bool useQsvEncoder_{};
        std::atomic_bool qsvForceKeyFrame_{};
        ComPtr<ICodecAPI> codecApi_;
        ComPtr<IMFMediaEventGenerator> eventGenerator_;
        bool asyncEncoder_{ false };
        int inputCredits_{};
        std::thread encoderThread_;

        mutable std::mutex qsvPacketMutex_;
        std::condition_variable qsvPacketCondition_;
        std::condition_variable qsvPacketDrainedCondition_;
        std::deque<QsvEncodedPacket> qsvPacketQueue_;
        std::thread qsvPacketWriterThread_;
        uint64_t qsvPacketsQueued_{};
        uint64_t qsvPacketsProcessed_{};
        bool qsvPacketWriterStopping_{};
        bool qsvPacketWriterFailed_{};
        static constexpr size_t MaxQsvPacketQueue = 240;

        mutable std::mutex packetMutex_;
        mutable std::mutex recordingWriterMutex_;
        std::deque<EncodedFrame> packets_;
        std::wstring recordingPath_;
        std::vector<EncodedFrame> recordingFrames_;
        std::ofstream recordingStream_;
        ComPtr<IMFSinkWriter> recordingWriter_;
        ComPtr<IMFMediaSink> recordingMediaSink_;
        ComPtr<IMFByteStream> recordingByteStream_;
        DWORD recordingStreamIndex_{};
        DWORD recordingAudioStreamIndex_[2]{ static_cast<DWORD>(-1), static_cast<DWORD>(-1) };
        bool recordingAudioEnabled_[2]{ false, false };
        int64_t recordingAudioLastTimestamp100ns_[2]{ -1, -1 };
        int64_t recordingAudioQueuedLastTimestamp100ns_[2]{ -1, -1 };
        std::deque<RecordingAudioSample> recordingAudioQueue_;
        uint64_t recordingAudioQueuedBytes_{};
        uint64_t recordingAudioDroppedSamples_{};
        std::chrono::steady_clock::time_point lastAudioDrainDiagnostic_{};
        static constexpr uint64_t MaxRecordingAudioQueueBytes = 64ull * 1024ull * 1024ull;
        static constexpr int64_t RecordingAudioLead100ns = 2'500'000;
        std::filesystem::path spoolDirectory_;
        std::shared_ptr<EncodedStorageFile> activeSpool_;
        std::ofstream activeSpoolStream_;
        uint64_t spoolSequence_{};
        uint32_t activeSpoolFrameCount_{};
        bool recordingPending_{ false };
        bool recording_{ false };
        int64_t recordingStart100ns_{};
        int64_t recordingEnd100ns_{};
        int64_t recordingLastTimestamp100ns_{ -1 };
        int64_t recordingLastKeyFrameRequest100ns_{ -1 };
        int64_t lastPacketTimestamp100ns_{ -1 };
        uint64_t recordingFrameCount_{};
        std::chrono::steady_clock::time_point encodeClockStart_{};

        mutable std::mutex diagnosticsMutex_;
        std::deque<int64_t> capturedTimeline100ns_;
        std::deque<int64_t> submittedTimeline100ns_;
        std::deque<int64_t> encodedTimeline100ns_;

        std::atomic_uint64_t capturedFrames_{};
        std::atomic_uint64_t submittedFrames_{};
        std::atomic_uint64_t encodedFrames_{};
        std::atomic_uint64_t droppedFrames_{};
        std::atomic_uint64_t bufferedBytes_{};
        std::atomic_uint64_t monitorQueueOverflowFrames_{};
        std::atomic_uint64_t monitorQueueMaxDepth_{};
        std::atomic_uint64_t encoderSurfaceStarvationFrames_{};
    };

    VideoEngine::VideoEngine(const EcVideoConfig& config) : implementation_(std::make_unique<Implementation>(config)) {}
    VideoEngine::~VideoEngine() = default;
    EcResult VideoEngine::Start()
    {
        std::wstringstream log;
        log << L"VideoEngine::Start wrapper enter | This=0x" << std::hex << reinterpret_cast<uintptr_t>(this)
            << L" | Implementation=0x" << reinterpret_cast<uintptr_t>(implementation_.get());
        LogNative(log.str());
        if (!implementation_)
        {
            LogNative(L"VideoEngine::Start wrapper failed | Implementation=null");
            return EcResult::NativeFailure;
        }
        return implementation_->Start();
    }
    EcResult VideoEngine::Stop() { return implementation_->Stop(); }
    EcResult VideoEngine::SaveReplay(const wchar_t* path, uint32_t seconds, EcExportResult& result) { return implementation_->SaveReplay(path, seconds, result); }
    EcResult VideoEngine::StartRecording(const wchar_t* path) { return implementation_->StartRecording(path); }
    EcResult VideoEngine::StartRecordingWithAudio(const wchar_t* path, const EcAudioStreamConfig* systemAudio, const EcAudioStreamConfig* microphoneAudio) { return implementation_->StartRecordingWithAudio(path, systemAudio, microphoneAudio); }
    EcResult VideoEngine::WriteRecordingAudio(EcAudioStreamKind streamKind, const uint8_t* data, uint32_t byteCount, int64_t timestamp100ns, int64_t duration100ns) { return implementation_->WriteRecordingAudio(streamKind, data, byteCount, timestamp100ns, duration100ns); }
    EcResult VideoEngine::StopRecording(EcExportResult& result) { return implementation_->StopRecording(result); }
    EcResult VideoEngine::GetStats(EcVideoStats& stats) const { return implementation_->GetStats(stats); }
    std::wstring VideoEngine::LastError() const { return implementation_->LastError(); }
}
