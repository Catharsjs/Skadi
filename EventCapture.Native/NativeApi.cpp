#include "NativeApi.h"
#include "VideoEngine.h"
#include <algorithm>
#include <cstring>
#include <new>
#include <filesystem>
#include <sstream>

using EventCaptureNative::VideoEngine;

namespace
{
    VideoEngine* ToEngine(EcEngineHandle handle) noexcept
    {
        return static_cast<VideoEngine*>(handle);
    }

    std::wstring NativeDiagnosticsPath()
    {
        wchar_t temporaryPath[MAX_PATH]{};
        if (GetTempPathW(MAX_PATH, temporaryPath) == 0) return L"Skadi-Native-Diagnostics.log";
        return std::filesystem::path(temporaryPath).append(L"Skadi-Native-Diagnostics.log").wstring();
    }

    void AppendNativeApiLog(const std::wstring& message) noexcept
    {
        try
        {
            SYSTEMTIME time{};
            GetLocalTime(&time);

            std::wstringstream line;
            line
                << L"["
                << time.wYear << L"-"
                << time.wMonth << L"-"
                << time.wDay << L" "
                << time.wHour << L":"
                << time.wMinute << L":"
                << time.wSecond << L"."
                << time.wMilliseconds
                << L"] [T" << GetCurrentThreadId() << L"] "
                << L"[NativeApi] "
                << message
                << L"\r\n";

            std::wstring text = line.str();
            int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
            if (size <= 0) return;

            std::string utf8(static_cast<size_t>(size), '\0');
            WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), utf8.data(), size, nullptr, nullptr);

            std::wstring path = NativeDiagnosticsPath();
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
        catch (...)
        {
        }
    }

    EcResult LogSehFailure(const wchar_t* functionName, unsigned long code) noexcept
    {
        std::wstringstream message;
        message
            << functionName
            << L" caught SEH exception | Code=0x"
            << std::hex
            << code;

        AppendNativeApiLog(message.str());
        return EcResult::NativeFailure;
    }

    EcResult EcCreateVideoEngineCore(const EcVideoConfig* config, EcEngineHandle* handle) noexcept
    {
        AppendNativeApiLog(L"EcCreateVideoEngine enter");
        if (config == nullptr || handle == nullptr || config->structSize != sizeof(EcVideoConfig))
        {
            AppendNativeApiLog(L"EcCreateVideoEngine invalid argument");
            return EcResult::InvalidArgument;
        }

        {
            std::wstringstream log;
            log
                << L"EcCreateVideoEngine config"
                << L" | TargetKind=" << static_cast<int32_t>(config->targetKind)
                << L" | TargetHandle=0x" << std::hex << reinterpret_cast<uintptr_t>(config->targetHandle)
                << std::dec
                << L" | Output=" << config->outputWidth << L"x" << config->outputHeight
                << L" | FPS=" << config->framesPerSecond
                << L" | BitrateKbps=" << config->bitrateKbps
                << L" | ReplaySeconds=" << config->replaySeconds
                << L" | EnableReplay=" << config->enableReplay;
            AppendNativeApiLog(log.str());
        }

        *handle = nullptr;
        try
        {
            *handle = new VideoEngine(*config);
            std::wstringstream log;
            log << L"EcCreateVideoEngine exit | Result=Ok | Handle=0x" << std::hex << reinterpret_cast<uintptr_t>(*handle);
            AppendNativeApiLog(log.str());
            return EcResult::Ok;
        }
        catch (const std::bad_alloc&)
        {
            AppendNativeApiLog(L"EcCreateVideoEngine caught bad_alloc");
            return EcResult::NativeFailure;
        }
        catch (const std::exception& error)
        {
            AppendNativeApiLog(L"EcCreateVideoEngine caught std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
            return EcResult::NativeFailure;
        }
        catch (...)
        {
            AppendNativeApiLog(L"EcCreateVideoEngine caught unknown C++ exception");
            return EcResult::NativeFailure;
        }
    }

    EcResult EcStartVideoEngineCore(EcEngineHandle handle) noexcept
    {
        {
            std::wstringstream log;
            log << L"EcStartVideoEngine enter | Handle=0x" << std::hex << reinterpret_cast<uintptr_t>(handle);
            AppendNativeApiLog(log.str());
        }

        try
        {
            VideoEngine* engine = ToEngine(handle);
            {
                std::wstringstream log;
                log << L"EcStartVideoEngine dispatch | Engine=0x" << std::hex << reinterpret_cast<uintptr_t>(engine);
                AppendNativeApiLog(log.str());
            }

            EcResult result = engine->Start();
            std::wstringstream log;
            log << L"EcStartVideoEngine exit | Result=" << static_cast<int32_t>(result);
            AppendNativeApiLog(log.str());
            return result;
        }
        catch (const std::exception& error)
        {
            AppendNativeApiLog(L"EcStartVideoEngine caught std::exception | " + std::wstring(error.what(), error.what() + std::char_traits<char>::length(error.what())));
            return EcResult::NativeFailure;
        }
        catch (...)
        {
            AppendNativeApiLog(L"EcStartVideoEngine caught unknown C++ exception");
            return EcResult::NativeFailure;
        }
    }

    EcResult EcSaveReplayCore(EcEngineHandle handle, const wchar_t* h264Path, uint32_t seconds, EcExportResult* result) noexcept
    {
        try
        {
            return ToEngine(handle)->SaveReplay(h264Path, seconds, *result);
        }
        catch (...)
        {
            AppendNativeApiLog(L"EcSaveReplay caught C++ exception");
            return EcResult::NativeFailure;
        }
    }

    EcResult EcStartRecordingCore(EcEngineHandle handle, const wchar_t* h264Path) noexcept
    {
        try
        {
            return ToEngine(handle)->StartRecording(h264Path);
        }
        catch (...)
        {
            AppendNativeApiLog(L"EcStartRecording caught C++ exception");
            return EcResult::NativeFailure;
        }
    }

    EcResult EcStopRecordingCore(EcEngineHandle handle, EcExportResult* result) noexcept
    {
        try
        {
            return ToEngine(handle)->StopRecording(*result);
        }
        catch (...)
        {
            AppendNativeApiLog(L"EcStopRecording caught C++ exception");
            return EcResult::NativeFailure;
        }
    }

    EcResult EcGetVideoStatsCore(EcEngineHandle handle, EcVideoStats* stats) noexcept
    {
        try
        {
            return ToEngine(handle)->GetStats(*stats);
        }
        catch (...)
        {
            AppendNativeApiLog(L"EcGetVideoStats caught C++ exception");
            return EcResult::NativeFailure;
        }
    }

    EcResult EcStopVideoEngineCore(EcEngineHandle handle) noexcept
    {
        try
        {
            return ToEngine(handle)->Stop();
        }
        catch (...)
        {
            AppendNativeApiLog(L"EcStopVideoEngine caught C++ exception");
            return EcResult::NativeFailure;
        }
    }
}

EcResult __cdecl EcCreateVideoEngine(const EcVideoConfig* config, EcEngineHandle* handle)
{
    __try
    {
        return EcCreateVideoEngineCore(config, handle);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return LogSehFailure(L"EcCreateVideoEngine", GetExceptionCode());
    }
}

EcResult __cdecl EcStartVideoEngine(EcEngineHandle handle)
{
    if (handle == nullptr) return EcResult::InvalidArgument;

    __try
    {
        return EcStartVideoEngineCore(handle);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return LogSehFailure(L"EcStartVideoEngine", GetExceptionCode());
    }
}

EcResult __cdecl EcSaveReplay(EcEngineHandle handle, const wchar_t* h264Path, uint32_t seconds, EcExportResult* result)
{
    if (handle == nullptr || h264Path == nullptr || result == nullptr) return EcResult::InvalidArgument;
    result->structSize = sizeof(EcExportResult);

    __try
    {
        return EcSaveReplayCore(handle, h264Path, seconds, result);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return LogSehFailure(L"EcSaveReplay", GetExceptionCode());
    }
}

EcResult __cdecl EcStartRecording(EcEngineHandle handle, const wchar_t* h264Path)
{
    if (handle == nullptr || h264Path == nullptr) return EcResult::InvalidArgument;

    __try
    {
        return EcStartRecordingCore(handle, h264Path);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return LogSehFailure(L"EcStartRecording", GetExceptionCode());
    }
}

EcResult __cdecl EcStopRecording(EcEngineHandle handle, EcExportResult* result)
{
    if (handle == nullptr || result == nullptr) return EcResult::InvalidArgument;
    result->structSize = sizeof(EcExportResult);

    __try
    {
        return EcStopRecordingCore(handle, result);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return LogSehFailure(L"EcStopRecording", GetExceptionCode());
    }
}

EcResult __cdecl EcGetVideoStats(EcEngineHandle handle, EcVideoStats* stats)
{
    if (handle == nullptr || stats == nullptr || stats->structSize != sizeof(EcVideoStats)) return EcResult::InvalidArgument;

    __try
    {
        return EcGetVideoStatsCore(handle, stats);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return LogSehFailure(L"EcGetVideoStats", GetExceptionCode());
    }
}

EcResult __cdecl EcStopVideoEngine(EcEngineHandle handle)
{
    if (handle == nullptr) return EcResult::InvalidArgument;

    __try
    {
        return EcStopVideoEngineCore(handle);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return LogSehFailure(L"EcStopVideoEngine", GetExceptionCode());
    }
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
