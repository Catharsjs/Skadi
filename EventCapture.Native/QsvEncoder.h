#pragma once

#include <d3d11.h>
#pragma warning(push)
#pragma warning(disable: 4201)
#include <vpl/mfxvideo.h>
#pragma warning(pop)
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace EventCaptureNative
{
    struct QsvEncodedPacket
    {
        std::vector<uint8_t> bytes;
        int64_t timestamp100ns{};
        int64_t duration100ns{};
        bool keyFrame{};
    };

    class QsvEncoder final
    {
    public:
        using PacketHandler = std::function<void(QsvEncodedPacket&&)>;
        using LogHandler = std::function<void(const std::wstring&)>;

        QsvEncoder();
        ~QsvEncoder();
        QsvEncoder(const QsvEncoder&) = delete;
        QsvEncoder& operator=(const QsvEncoder&) = delete;

        void Initialize(
            ID3D11Device* device,
            const std::vector<ID3D11Texture2D*>& textures,
            uint32_t visibleWidth,
            uint32_t visibleHeight,
            uint32_t framesPerSecond,
            uint32_t bitrateKbps,
            PacketHandler packetHandler,
            LogHandler logHandler);
        void Submit(size_t surfaceIndex, int64_t timestamp100ns, int64_t duration100ns, bool forceKeyFrame);
        void DrainReady();
        void Flush();
        void Close() noexcept;

    private:
        struct SurfaceHandle;
        struct Surface;
        struct Task;

        static mfxStatus MFX_CDECL AllocateFrames(mfxHDL, mfxFrameAllocRequest*, mfxFrameAllocResponse*);
        static mfxStatus MFX_CDECL LockFrame(mfxHDL, mfxMemId, mfxFrameData*);
        static mfxStatus MFX_CDECL UnlockFrame(mfxHDL, mfxMemId, mfxFrameData*);
        static mfxStatus MFX_CDECL GetFrameHandle(mfxHDL, mfxMemId, mfxHDL*);
        static mfxStatus MFX_CDECL FreeFrames(mfxHDL, mfxFrameAllocResponse*);

        Task& AcquireTask();
        void DrainOldest(bool wait);
        void EmitTask(Task& task);
        void ResetTask(Task& task);
        void CheckStatus(mfxStatus status, const char* operation) const;
        void Log(const std::wstring& message) const;

        mfxSession session_{};
        mfxFrameAllocator allocator_{};
        mfxVideoParam parameters_{};
        std::vector<std::unique_ptr<Surface>> surfaces_;
        std::vector<std::unique_ptr<Task>> tasks_;
        std::vector<size_t> pendingTasks_;
        PacketHandler packetHandler_;
        LogHandler logHandler_;
        int64_t frameDuration100ns_{};
        bool initialized_{};
    };
}
