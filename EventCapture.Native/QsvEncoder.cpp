#include "QsvEncoder.h"

#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <sstream>
#include <stdexcept>
#include <thread>
#include <limits>

using Microsoft::WRL::ComPtr;

namespace EventCaptureNative
{
    namespace
    {
        constexpr mfxU32 SyncWaitMilliseconds = 2'000;
        constexpr size_t AsyncDepth = 4;
        constexpr size_t MinimumBitstreamCapacity = 4u * 1024u * 1024u;
        constexpr size_t MaximumBitstreamCapacity = 64u * 1024u * 1024u;
        constexpr int64_t TicksPerSecond = 10'000'000;

        mfxU16 Align16(uint32_t value)
        {
            return static_cast<mfxU16>((value + 15u) & ~15u);
        }

        mfxU64 ToMfxTimestamp(int64_t timestamp100ns)
        {
            return static_cast<mfxU64>(std::max<int64_t>(0, timestamp100ns)) * 9u / 1'000u;
        }

        int64_t FromMfxTimestamp(mfxU64 timestamp)
        {
            return timestamp == MFX_TIMESTAMP_UNKNOWN
                ? -1
                : static_cast<int64_t>(timestamp * 1'000u / 9u);
        }

        bool ContainsH264IdrNal(const uint8_t* data, size_t size)
        {
            if (data == nullptr || size == 0) return false;

            bool foundAnnexB = false;
            for (size_t offset = 0; offset + 3 < size; ++offset)
            {
                size_t prefixLength = 0;
                if (data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 1)
                {
                    prefixLength = 3;
                }
                else if (offset + 4 < size && data[offset] == 0 && data[offset + 1] == 0 &&
                    data[offset + 2] == 0 && data[offset + 3] == 1)
                {
                    prefixLength = 4;
                }

                if (prefixLength == 0) continue;
                foundAnnexB = true;
                const size_t nalOffset = offset + prefixLength;
                if (nalOffset < size && (data[nalOffset] & 0x1fu) == 5u) return true;
                offset = nalOffset;
            }

            if (foundAnnexB) return false;

            size_t offset = 0;
            while (offset + 4 <= size)
            {
                const uint32_t nalLength =
                    (static_cast<uint32_t>(data[offset]) << 24) |
                    (static_cast<uint32_t>(data[offset + 1]) << 16) |
                    (static_cast<uint32_t>(data[offset + 2]) << 8) |
                    static_cast<uint32_t>(data[offset + 3]);
                offset += 4;
                if (nalLength == 0 || nalLength > size - offset) return false;
                if ((data[offset] & 0x1fu) == 5u) return true;
                offset += nalLength;
            }
            return false;
        }
    }

    struct QsvEncoder::SurfaceHandle
    {
        ComPtr<ID3D11Texture2D> texture;
        UINT subresource{};
    };

    struct QsvEncoder::Surface
    {
        SurfaceHandle handle;
        mfxFrameSurface1 value{};
    };

    struct QsvEncoder::Task
    {
        ~Task()
        {
            if (data != nullptr) _aligned_free(data);
        }

        mfxBitstream bitstream{};
        mfxSyncPoint syncPoint{};
        mfxU8* data{};
        int64_t fallbackTimestamp100ns{};
        int64_t duration100ns{};
    };

    QsvEncoder::QsvEncoder() = default;

    QsvEncoder::~QsvEncoder()
    {
        Close();
    }

    void QsvEncoder::Initialize(
        ID3D11Device* device,
        const std::vector<ID3D11Texture2D*>& textures,
        uint32_t visibleWidth,
        uint32_t visibleHeight,
        uint32_t framesPerSecond,
        uint32_t bitrateKbps,
        PacketHandler packetHandler,
        LogHandler logHandler)
    {
        Close();
        if (device == nullptr || textures.size() < AsyncDepth + 2 || visibleWidth == 0 ||
            visibleHeight == 0 || framesPerSecond == 0 || bitrateKbps == 0)
        {
            throw std::invalid_argument("Invalid QSV encoder configuration");
        }

        packetHandler_ = std::move(packetHandler);
        logHandler_ = std::move(logHandler);
        frameDuration100ns_ = TicksPerSecond / static_cast<int64_t>(framesPerSecond);

        mfxInitParam init{};
        init.Implementation = MFX_IMPL_HARDWARE | MFX_IMPL_VIA_D3D11;
        init.Version.Major = 1;
        init.Version.Minor = 35;
        init.GPUCopy = MFX_GPUCOPY_ON;
#pragma warning(push)
#pragma warning(disable: 4996)
        const mfxStatus initializationStatus = MFXInitEx(init, &session_);
#pragma warning(pop)
        CheckStatus(initializationStatus, "MFXInitEx");

        try
        {
            CheckStatus(
                MFXVideoCORE_SetHandle(session_, MFX_HANDLE_D3D11_DEVICE, device),
                "MFXVideoCORE_SetHandle");

            allocator_.pthis = this;
            allocator_.Alloc = &AllocateFrames;
            allocator_.Lock = &LockFrame;
            allocator_.Unlock = &UnlockFrame;
            allocator_.GetHDL = &GetFrameHandle;
            allocator_.Free = &FreeFrames;
            CheckStatus(MFXVideoCORE_SetFrameAllocator(session_, &allocator_), "MFXVideoCORE_SetFrameAllocator");

            parameters_ = {};
            parameters_.AsyncDepth = static_cast<mfxU16>(AsyncDepth);
            parameters_.IOPattern = MFX_IOPATTERN_IN_VIDEO_MEMORY;
            parameters_.mfx.CodecId = MFX_CODEC_AVC;
            parameters_.mfx.CodecProfile = MFX_PROFILE_AVC_HIGH;
            parameters_.mfx.TargetUsage = MFX_TARGETUSAGE_BALANCED;
            parameters_.mfx.RateControlMethod = MFX_RATECONTROL_CBR;
            parameters_.mfx.TargetKbps = static_cast<mfxU16>(std::min<uint32_t>(bitrateKbps, 65'535));
            parameters_.mfx.MaxKbps = parameters_.mfx.TargetKbps;
            // BufferSizeInKB is bytes-per-second expressed in KB, while TargetKbps is kilobits-per-second.
            parameters_.mfx.BufferSizeInKB = static_cast<mfxU16>(std::min<uint32_t>(bitrateKbps / 8 + 1, 65'535));
            parameters_.mfx.GopPicSize = static_cast<mfxU16>(std::min<uint32_t>(framesPerSecond, 65'535));
            parameters_.mfx.GopRefDist = 1;
            parameters_.mfx.NumRefFrame = 1;
            parameters_.mfx.LowPower = MFX_CODINGOPTION_ON;
            parameters_.mfx.FrameInfo.FourCC = MFX_FOURCC_NV12;
            parameters_.mfx.FrameInfo.ChromaFormat = MFX_CHROMAFORMAT_YUV420;
            parameters_.mfx.FrameInfo.PicStruct = MFX_PICSTRUCT_PROGRESSIVE;
            parameters_.mfx.FrameInfo.FrameRateExtN = framesPerSecond;
            parameters_.mfx.FrameInfo.FrameRateExtD = 1;
            parameters_.mfx.FrameInfo.Width = Align16(visibleWidth);
            parameters_.mfx.FrameInfo.Height = Align16(visibleHeight);
            parameters_.mfx.FrameInfo.CropW = static_cast<mfxU16>(visibleWidth);
            parameters_.mfx.FrameInfo.CropH = static_cast<mfxU16>(visibleHeight);

            mfxVideoParam queried = parameters_;
            const mfxStatus queryStatus = MFXVideoENCODE_Query(session_, &parameters_, &queried);
            if (queryStatus < MFX_ERR_NONE) CheckStatus(queryStatus, "MFXVideoENCODE_Query");
            parameters_ = queried;
            CheckStatus(MFXVideoENCODE_Init(session_, &parameters_), "MFXVideoENCODE_Init");

            mfxVideoParam active{};
            CheckStatus(MFXVideoENCODE_GetVideoParam(session_, &active), "MFXVideoENCODE_GetVideoParam");
            parameters_ = active;

            surfaces_.reserve(textures.size());
            for (ID3D11Texture2D* texture : textures)
            {
                if (texture == nullptr) throw std::invalid_argument("QSV surface texture is null");
                D3D11_TEXTURE2D_DESC description{};
                texture->GetDesc(&description);
                if (description.Format != DXGI_FORMAT_NV12 ||
                    description.Width < parameters_.mfx.FrameInfo.Width ||
                    description.Height < parameters_.mfx.FrameInfo.Height)
                {
                    throw std::runtime_error("QSV surface dimensions or format do not match encoder parameters");
                }

                auto surface = std::make_unique<Surface>();
                surface->handle.texture = texture;
                surface->value.Info = parameters_.mfx.FrameInfo;
                surface->value.Data.MemId = &surface->handle;
                surfaces_.push_back(std::move(surface));
            }

            const size_t runtimeBufferCapacity =
                static_cast<size_t>(parameters_.mfx.BufferSizeInKB) * 1024u + 64u * 1024u;
            const size_t bitstreamCapacity = std::min(
                MaximumBitstreamCapacity,
                std::max(MinimumBitstreamCapacity, runtimeBufferCapacity));
            tasks_.reserve(AsyncDepth);
            for (size_t index = 0; index < AsyncDepth; ++index)
            {
                auto task = std::make_unique<Task>();
                ResizeTaskBuffer(*task, bitstreamCapacity);
                task->bitstream.TimeStamp = std::numeric_limits<mfxU64>::max();
                tasks_.push_back(std::move(task));
            }

            mfxVersion version{};
            mfxIMPL implementation{};
            MFXQueryVersion(session_, &version);
            MFXQueryIMPL(session_, &implementation);
            initialized_ = true;
            keyFrameMismatchLogged_ = false;

            std::wstringstream message;
            message << L"QSV encoder initialized | API=" << version.Major << L'.' << version.Minor
                << L" | Impl=0x" << std::hex << implementation << std::dec
                << L" | AsyncDepth=" << parameters_.AsyncDepth
                << L" | Surfaces=" << surfaces_.size()
                << L" | Frame=" << parameters_.mfx.FrameInfo.CropW << L'x' << parameters_.mfx.FrameInfo.CropH
                << L" | Allocated=" << parameters_.mfx.FrameInfo.Width << L'x' << parameters_.mfx.FrameInfo.Height
                << L" | TargetKbps=" << parameters_.mfx.TargetKbps
                << L" | VbvKB=" << parameters_.mfx.BufferSizeInKB
                << L" | BitstreamCapacity=" << bitstreamCapacity;
            Log(message.str());
        }
        catch (...)
        {
            Close();
            throw;
        }
    }

    void QsvEncoder::Submit(
        size_t surfaceIndex,
        int64_t timestamp100ns,
        int64_t duration100ns,
        bool forceKeyFrame)
    {
        if (!initialized_ || surfaceIndex >= surfaces_.size())
            throw std::runtime_error("QSV encoder is not initialized for this surface");

        DrainReady();
        Task& task = AcquireTask();
        Surface& surface = *surfaces_[surfaceIndex];
        surface.value.Data.TimeStamp = ToMfxTimestamp(timestamp100ns);
        surface.value.Data.FrameOrder = static_cast<mfxU32>(timestamp100ns / std::max<int64_t>(1, frameDuration100ns_));

        mfxEncodeCtrl control{};
        if (forceKeyFrame)
        {
            control.FrameType = MFX_FRAMETYPE_I | MFX_FRAMETYPE_IDR | MFX_FRAMETYPE_REF;
        }

        task.fallbackTimestamp100ns = timestamp100ns;
        task.duration100ns = std::max<int64_t>(1, duration100ns);

        for (;;)
        {
            task.syncPoint = nullptr;
            const mfxStatus status = MFXVideoENCODE_EncodeFrameAsync(
                session_,
                forceKeyFrame ? &control : nullptr,
                &surface.value,
                &task.bitstream,
                &task.syncPoint);

            if (status == MFX_WRN_DEVICE_BUSY)
            {
                if (!pendingTasks_.empty()) DrainOldest(true);
                else std::this_thread::sleep_for(std::chrono::milliseconds(1));
                continue;
            }
            if (status == MFX_ERR_NOT_ENOUGH_BUFFER)
            {
                const size_t currentCapacity = task.bitstream.MaxLength;
                if (currentCapacity >= MaximumBitstreamCapacity)
                    throw std::runtime_error("QSV encoded frame exceeded the maximum bitstream buffer");

                const size_t nextCapacity = std::min(
                    MaximumBitstreamCapacity,
                    std::max(currentCapacity * 2u, currentCapacity + MinimumBitstreamCapacity));
                ResizeTaskBuffer(task, nextCapacity);

                std::wstringstream message;
                message << L"QSV bitstream buffer expanded | Previous=" << currentCapacity
                    << L" | Current=" << nextCapacity
                    << L" | Timestamp100ns=" << timestamp100ns;
                Log(message.str());
                continue;
            }
            if (status < MFX_ERR_NONE && status != MFX_ERR_MORE_DATA) CheckStatus(status, "MFXVideoENCODE_EncodeFrameAsync");

            if (task.syncPoint != nullptr)
            {
                const auto iterator = std::find_if(tasks_.begin(), tasks_.end(), [&task](const auto& value) { return value.get() == &task; });
                pendingTasks_.push_back(static_cast<size_t>(std::distance(tasks_.begin(), iterator)));
            }
            return;
        }
    }

    void QsvEncoder::DrainReady()
    {
        while (!pendingTasks_.empty())
        {
            const size_t before = pendingTasks_.size();
            DrainOldest(false);
            if (pendingTasks_.size() == before) break;
        }
    }

    void QsvEncoder::Flush()
    {
        if (!initialized_) return;

        for (;;)
        {
            Task& task = AcquireTask();
            task.fallbackTimestamp100ns = 0;
            task.duration100ns = frameDuration100ns_;
            task.syncPoint = nullptr;
            const mfxStatus status = MFXVideoENCODE_EncodeFrameAsync(
                session_, nullptr, nullptr, &task.bitstream, &task.syncPoint);

            if (status == MFX_WRN_DEVICE_BUSY)
            {
                if (!pendingTasks_.empty()) DrainOldest(true);
                else std::this_thread::sleep_for(std::chrono::milliseconds(1));
                continue;
            }
            if (status == MFX_ERR_MORE_DATA) break;
            if (status < MFX_ERR_NONE) CheckStatus(status, "MFXVideoENCODE_EncodeFrameAsync(drain)");
            if (task.syncPoint != nullptr)
            {
                const auto iterator = std::find_if(tasks_.begin(), tasks_.end(), [&task](const auto& value) { return value.get() == &task; });
                pendingTasks_.push_back(static_cast<size_t>(std::distance(tasks_.begin(), iterator)));
            }
        }

        while (!pendingTasks_.empty()) DrainOldest(true);
    }

    void QsvEncoder::Close() noexcept
    {
        if (session_ != nullptr)
        {
            if (initialized_)
            {
                try { Flush(); }
                catch (...) {}
                MFXVideoENCODE_Close(session_);
            }
            MFXClose(session_);
        }
        session_ = nullptr;
        initialized_ = false;
        keyFrameMismatchLogged_ = false;
        pendingTasks_.clear();
        tasks_.clear();
        surfaces_.clear();
        parameters_ = {};
        allocator_ = {};
        packetHandler_ = {};
        logHandler_ = {};
    }

    mfxStatus MFX_CDECL QsvEncoder::AllocateFrames(mfxHDL, mfxFrameAllocRequest*, mfxFrameAllocResponse*)
    {
        return MFX_ERR_UNSUPPORTED;
    }

    mfxStatus MFX_CDECL QsvEncoder::LockFrame(mfxHDL, mfxMemId, mfxFrameData*)
    {
        return MFX_ERR_UNSUPPORTED;
    }

    mfxStatus MFX_CDECL QsvEncoder::UnlockFrame(mfxHDL, mfxMemId, mfxFrameData*)
    {
        return MFX_ERR_NONE;
    }

    mfxStatus MFX_CDECL QsvEncoder::GetFrameHandle(mfxHDL, mfxMemId memoryId, mfxHDL* handle)
    {
        if (memoryId == nullptr || handle == nullptr) return MFX_ERR_INVALID_HANDLE;
        const auto* surface = static_cast<const SurfaceHandle*>(memoryId);
        auto* pair = reinterpret_cast<mfxHDLPair*>(handle);
        pair->first = surface->texture.Get();
        pair->second = reinterpret_cast<mfxHDL>(static_cast<UINT_PTR>(surface->subresource));
        return pair->first != nullptr ? MFX_ERR_NONE : MFX_ERR_INVALID_HANDLE;
    }

    mfxStatus MFX_CDECL QsvEncoder::FreeFrames(mfxHDL, mfxFrameAllocResponse*)
    {
        return MFX_ERR_NONE;
    }

    QsvEncoder::Task& QsvEncoder::AcquireTask()
    {
        for (;;)
        {
            for (auto& task : tasks_)
            {
                if (task->syncPoint == nullptr) return *task;
            }
            if (pendingTasks_.empty()) throw std::runtime_error("QSV task pool is exhausted without a pending sync point");
            DrainOldest(true);
        }
    }

    void QsvEncoder::DrainOldest(bool wait)
    {
        if (pendingTasks_.empty()) return;
        Task& task = *tasks_[pendingTasks_.front()];
        const mfxStatus status = MFXVideoCORE_SyncOperation(session_, task.syncPoint, wait ? SyncWaitMilliseconds : 0);
        if (!wait && status == MFX_WRN_IN_EXECUTION) return;
        CheckStatus(status, "MFXVideoCORE_SyncOperation");
        pendingTasks_.erase(pendingTasks_.begin());
        EmitTask(task);
        ResetTask(task);
    }

    void QsvEncoder::EmitTask(Task& task)
    {
        if (task.bitstream.DataLength == 0) return;
        QsvEncodedPacket packet;
        const mfxU8* begin = task.bitstream.Data + task.bitstream.DataOffset;
        packet.bytes.assign(begin, begin + task.bitstream.DataLength);
        const int64_t outputTimestamp = FromMfxTimestamp(task.bitstream.TimeStamp);
        packet.timestamp100ns = outputTimestamp >= 0 ? outputTimestamp : task.fallbackTimestamp100ns;
        packet.duration100ns = std::max<int64_t>(1, task.duration100ns);
        const bool runtimeKeyFrame =
            (task.bitstream.FrameType & (MFX_FRAMETYPE_IDR | MFX_FRAMETYPE_I)) != 0;
        packet.keyFrame = ContainsH264IdrNal(packet.bytes.data(), packet.bytes.size());
        if (runtimeKeyFrame != packet.keyFrame && !keyFrameMismatchLogged_)
        {
            std::wstringstream message;
            message << L"QSV FrameType differs from H.264 NAL classification"
                << L" | FrameType=0x" << std::hex << task.bitstream.FrameType << std::dec
                << L" | RuntimeKey=" << runtimeKeyFrame
                << L" | IdrNal=" << packet.keyFrame
                << L" | Bytes=" << packet.bytes.size();
            Log(message.str());
            keyFrameMismatchLogged_ = true;
        }
        if (packetHandler_) packetHandler_(std::move(packet));
    }

    void QsvEncoder::ResetTask(Task& task)
    {
        task.syncPoint = nullptr;
        task.bitstream.DataOffset = 0;
        task.bitstream.DataLength = 0;
        task.bitstream.TimeStamp = std::numeric_limits<mfxU64>::max();
        task.bitstream.FrameType = 0;
        task.fallbackTimestamp100ns = 0;
        task.duration100ns = 0;
    }

    void QsvEncoder::ResizeTaskBuffer(Task& task, size_t capacity)
    {
        if (capacity == 0 || capacity > MaximumBitstreamCapacity ||
            capacity > static_cast<size_t>(std::numeric_limits<mfxU32>::max()))
        {
            throw std::runtime_error("Invalid QSV bitstream buffer capacity");
        }
        if (task.syncPoint != nullptr || task.bitstream.DataLength != 0)
            throw std::runtime_error("Cannot resize an in-flight QSV bitstream buffer");

        auto* replacement = static_cast<mfxU8*>(_aligned_malloc(capacity, 32));
        if (replacement == nullptr) throw std::bad_alloc();
        if (task.data != nullptr) _aligned_free(task.data);
        task.data = replacement;
        task.bitstream.Data = replacement;
        task.bitstream.DataOffset = 0;
        task.bitstream.DataLength = 0;
        task.bitstream.MaxLength = static_cast<mfxU32>(capacity);
    }

    void QsvEncoder::CheckStatus(mfxStatus status, const char* operation) const
    {
        if (status >= MFX_ERR_NONE) return;
        std::ostringstream message;
        message << operation << " failed with oneVPL status " << status;
        throw std::runtime_error(message.str());
    }

    void QsvEncoder::Log(const std::wstring& message) const
    {
        if (logHandler_) logHandler_(message);
    }
}
