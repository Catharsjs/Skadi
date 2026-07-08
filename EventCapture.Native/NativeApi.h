#pragma once

#include <Windows.h>
#include <cstdint>

#ifdef EVENTCAPTURENATIVE_EXPORTS
#define EC_API extern "C" __declspec(dllexport)
#else
#define EC_API extern "C" __declspec(dllimport)
#endif

enum class EcResult : int32_t
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidState = 2,
    NotSupported = 3,
    Timeout = 4,
    NativeFailure = 5
};

enum class EcTargetKind : int32_t
{
    Monitor = 0,
    Window = 1
};

struct EcVideoConfig
{
    uint32_t structSize;
    EcTargetKind targetKind;
    void* targetHandle;
    uint32_t outputWidth;
    uint32_t outputHeight;
    uint32_t framesPerSecond;
    uint32_t bitrateKbps;
    uint32_t replaySeconds;
    int32_t enableReplay;
};

struct EcExportResult
{
    uint32_t structSize;
    int64_t startTimestamp100ns;
    int64_t endTimestamp100ns;
    uint64_t frameCount;
};

struct EcVideoStats
{
    uint32_t structSize;
    uint64_t capturedFrames;
    uint64_t encodedFrames;
    uint64_t droppedFrames;
    uint64_t bufferedBytes;
    uint64_t bufferedFrames;
    int32_t isRunning;
    int32_t isRecording;
};

using EcEngineHandle = void*;

enum class EcAudioStreamKind : int32_t
{
    System = 0,
    Microphone = 1
};

struct EcAudioStreamConfig
{
    uint32_t sampleRate;
    uint32_t channels;
    uint32_t bitsPerSample;
    int32_t enabled;
};

EC_API EcResult __cdecl EcCreateVideoEngine(const EcVideoConfig* config, EcEngineHandle* handle);
EC_API EcResult __cdecl EcStartVideoEngine(EcEngineHandle handle);
EC_API EcResult __cdecl EcSaveReplay(EcEngineHandle handle, const wchar_t* h264Path, uint32_t seconds, EcExportResult* result);
EC_API EcResult __cdecl EcStartRecording(EcEngineHandle handle, const wchar_t* h264Path);
EC_API EcResult __cdecl EcStartRecordingWithAudio(EcEngineHandle handle, const wchar_t* h264Path, const EcAudioStreamConfig* systemAudio, const EcAudioStreamConfig* microphoneAudio);
EC_API EcResult __cdecl EcWriteRecordingAudio(EcEngineHandle handle, EcAudioStreamKind streamKind, const uint8_t* data, uint32_t byteCount, int64_t timestamp100ns, int64_t duration100ns);
EC_API EcResult __cdecl EcStopRecording(EcEngineHandle handle, EcExportResult* result);
EC_API EcResult __cdecl EcGetVideoStats(EcEngineHandle handle, EcVideoStats* stats);
EC_API EcResult __cdecl EcStopVideoEngine(EcEngineHandle handle);
EC_API void __cdecl EcDestroyVideoEngine(EcEngineHandle handle);
EC_API uint32_t __cdecl EcGetLastError(EcEngineHandle handle, wchar_t* buffer, uint32_t characterCapacity);
