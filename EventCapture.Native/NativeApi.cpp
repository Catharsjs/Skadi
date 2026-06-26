#include "NativeApi.h"
#include "VideoEngine.h"
#include <algorithm>
#include <cstring>
#include <new>

using EventCaptureNative::VideoEngine;

namespace
{
    VideoEngine* ToEngine(EcEngineHandle handle) noexcept
    {
        return static_cast<VideoEngine*>(handle);
    }
}

EcResult __cdecl EcCreateVideoEngine(const EcVideoConfig* config, EcEngineHandle* handle)
{
    if (config == nullptr || handle == nullptr || config->structSize != sizeof(EcVideoConfig)) return EcResult::InvalidArgument;
    *handle = nullptr;
    try
    {
        *handle = new VideoEngine(*config);
        return EcResult::Ok;
    }
    catch (const std::bad_alloc&)
    {
        return EcResult::NativeFailure;
    }
    catch (...)
    {
        return EcResult::NativeFailure;
    }
}

EcResult __cdecl EcStartVideoEngine(EcEngineHandle handle)
{
    return handle == nullptr ? EcResult::InvalidArgument : ToEngine(handle)->Start();
}

EcResult __cdecl EcSaveReplay(EcEngineHandle handle, const wchar_t* h264Path, uint32_t seconds, EcExportResult* result)
{
    if (handle == nullptr || h264Path == nullptr || result == nullptr) return EcResult::InvalidArgument;
    result->structSize = sizeof(EcExportResult);
    return ToEngine(handle)->SaveReplay(h264Path, seconds, *result);
}

EcResult __cdecl EcStartRecording(EcEngineHandle handle, const wchar_t* h264Path)
{
    if (handle == nullptr || h264Path == nullptr) return EcResult::InvalidArgument;
    return ToEngine(handle)->StartRecording(h264Path);
}

EcResult __cdecl EcStopRecording(EcEngineHandle handle, EcExportResult* result)
{
    if (handle == nullptr || result == nullptr) return EcResult::InvalidArgument;
    result->structSize = sizeof(EcExportResult);
    return ToEngine(handle)->StopRecording(*result);
}

EcResult __cdecl EcGetVideoStats(EcEngineHandle handle, EcVideoStats* stats)
{
    if (handle == nullptr || stats == nullptr || stats->structSize != sizeof(EcVideoStats)) return EcResult::InvalidArgument;
    return ToEngine(handle)->GetStats(*stats);
}

EcResult __cdecl EcStopVideoEngine(EcEngineHandle handle)
{
    return handle == nullptr ? EcResult::InvalidArgument : ToEngine(handle)->Stop();
}

void __cdecl EcDestroyVideoEngine(EcEngineHandle handle)
{
    delete ToEngine(handle);
}

uint32_t __cdecl EcGetLastError(EcEngineHandle handle, wchar_t* buffer, uint32_t characterCapacity)
{
    if (handle == nullptr) return 0;
    const std::wstring message = ToEngine(handle)->LastError();
    const uint32_t required = static_cast<uint32_t>(message.size() + 1);
    if (buffer != nullptr && characterCapacity > 0)
    {
        const size_t count = std::min<size_t>(message.size(), characterCapacity - 1);
        std::wmemcpy(buffer, message.data(), count);
        buffer[count] = L'\0';
    }
    return required;
}
