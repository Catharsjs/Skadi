#pragma once

#include "NativeApi.h"
#include <atomic>
#include <deque>
#include <fstream>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace EventCaptureNative
{
    struct EncodedStorageFile
    {
        explicit EncodedStorageFile(std::wstring value) : path(std::move(value)) {}
        ~EncodedStorageFile();
        std::wstring path;
    };

    struct EncodedFrame
    {
        int64_t timestamp100ns{};
        int64_t duration100ns{};
        bool keyFrame{};
        std::vector<uint8_t> bytes;
        std::shared_ptr<EncodedStorageFile> storage;
        uint64_t storageOffset{};
        uint32_t storageLength{};
    };

    class VideoEngine final
    {
    public:
        explicit VideoEngine(const EcVideoConfig& config);
        ~VideoEngine();

        VideoEngine(const VideoEngine&) = delete;
        VideoEngine& operator=(const VideoEngine&) = delete;

        EcResult Start();
        EcResult Stop();
        EcResult SaveReplay(const wchar_t* path, uint32_t seconds, EcExportResult& result);
        EcResult StartRecording(const wchar_t* path);
        EcResult StartRecordingWithAudio(const wchar_t* path, const EcAudioStreamConfig* systemAudio, const EcAudioStreamConfig* microphoneAudio);
        EcResult WriteRecordingAudio(EcAudioStreamKind streamKind, const uint8_t* data, uint32_t byteCount, int64_t timestamp100ns, int64_t duration100ns);
        EcResult StopRecording(EcExportResult& result);
        EcResult GetStats(EcVideoStats& stats) const;
        std::wstring LastError() const;

    private:
        class Implementation;
        std::unique_ptr<Implementation> implementation_;
    };
}
