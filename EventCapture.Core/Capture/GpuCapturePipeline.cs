using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using EventCapture.Core.Diagnostics;
using NAudio.Wave;

namespace EventCapture.Core.Capture;

public sealed class GpuCapturePipeline : IDisposable, IContinuousAudioSink
{
    private const long FinalizeReserveBytes = 256L * 1024 * 1024;
    private readonly int _fps;
    private readonly SafeVideoEngineHandle _handle;
    private readonly string _sessionDirectory;
    private long _startTimestamp;
    private string? _continuousRawPath;
    private string? _continuousFinalPath;
    private string? _continuousReservePath;
    private long _continuousAudioWriteCount;
    private long _continuousAudioWriteBytes;
    private long _continuousAudioLastTimestamp100ns = -1;
    private long _continuousAudioLastLogTimestamp;
    private long _continuousStartedTimestamp;
    private bool _disposed;

    public GpuCapturePipeline(
        int fps,
        int width,
        int height,
        int bitrateKbps,
        int bufferSeconds,
        string captureTarget,
        bool enableReplay)
    {
        _fps = Math.Max(1, fps);
        IntPtr targetHandle = ResolveTarget(captureTarget);
        var config = new NativeVideoConfig
        {
            StructSize = (uint)Marshal.SizeOf<NativeVideoConfig>(),
            TargetKind = NativeTargetKind.Monitor,
            TargetHandle = targetHandle,
            OutputWidth = (uint)Math.Max(2, width & ~1),
            OutputHeight = (uint)Math.Max(2, height & ~1),
            FramesPerSecond = (uint)_fps,
            BitrateKbps = (uint)Math.Max(1_000, bitrateKbps),
            ReplaySeconds = (uint)Math.Max(5, bufferSeconds),
            EnableReplay = enableReplay ? 1 : 0
        };

        NativeResult result = NativeMethods.CreateVideoEngine(in config, out _handle);
        ThrowIfFailed(result, _handle);
        _sessionDirectory = ReplaySessionStorage.CreateSessionDirectory("native-video");
    }

    public bool IsRunning { get; private set; }
    public bool IsContinuousRecording => _continuousRawPath is not null;
    public long StartTimestamp => _startTimestamp;
    public long FramesCaptured => checked((long)GetStats().CapturedFrames);

    // Запуск GPU pipeline захоплення ...
    public void Start()
    {
        ThrowIfDisposed();
        if (IsRunning) return;
        ThrowIfFailed(NativeMethods.StartVideoEngine(_handle), _handle);
        _startTimestamp = Environment.TickCount64;
        IsRunning = true;
    }
    // ...Запуск GPU pipeline захоплення

    // Збереження відео з replay buffer ...
    public async Task<(string videoPath, long videoElapsedMs, long videoStartTimestamp)>
        SaveLastSecondsAsync(string outputFolder, int seconds)
    {
        ThrowIfDisposed();
        if (!IsRunning) throw new InvalidOperationException("GPU capture pipeline is not running.");
        string outputPath = OutputFileName.Create(outputFolder, "Replay", ".mp4");
        var export = NativeExportResult.Create();
        ThrowIfFailed(
            NativeMethods.SaveReplay(_handle, outputPath, (uint)Math.Max(1, seconds), ref export),
            _handle);
        double exportFps = CalculateExportFps(export);
        await Task.CompletedTask;

        long startMilliseconds = export.StartTimestamp100ns / 10_000;
        long elapsedMilliseconds = Math.Max(
            1,
            (export.EndTimestamp100ns - export.StartTimestamp100ns) / 10_000);

        LogExportDiagnostics(
            "Replay",
            export,
            exportFps,
            outputPath);

        return (outputPath, elapsedMilliseconds, _startTimestamp + startMilliseconds);
    }
    // ...Збереження відео з replay buffer

    // Об'єднання replay-відео з аудіо ...
    internal static void MuxReplayAudio(
        string videoPath,
        string audioPath,
        string outputPath)
    {
        NativeResult result = NativeMethods.MuxReplayAudio(videoPath, audioPath, outputPath);
        if (result != NativeResult.Ok)
        {
            throw new InvalidOperationException(
                $"Native Media Foundation replay mux failed with result {result}.");
        }
    }
    // ...Об'єднання replay-відео з аудіо

    // Запуск безперервного запису відео з аудіо ...
    public void StartContinuousRecording(
        string outputFolder,
        WaveFormat? audioFormat)
    {
        ThrowIfDisposed();
        if (!IsRunning) throw new InvalidOperationException("GPU capture pipeline is not running.");
        if (_continuousRawPath is not null)
            throw new InvalidOperationException("Continuous video recording is already active.");
        var paths = CreateContinuousRecordingPaths(outputFolder);
        CreateFinalizeReserve(paths.ReservePath);
        NativeAudioStreamConfig audioConfig = NativeAudioStreamConfig.From(audioFormat);
        NativeAudioStreamConfig disabledConfig = default;
        try
        {
            ThrowIfFailed(
                NativeMethods.StartRecordingWithAudio(
                    _handle,
                    paths.PartPath,
                    ref audioConfig,
                    ref disabledConfig),
                _handle);
        }
        catch
        {
            TryDelete(paths.ReservePath);
            TryDelete(paths.PartPath);
            throw;
        }
        _continuousAudioWriteCount = 0;
        _continuousAudioWriteBytes = 0;
        _continuousAudioLastTimestamp100ns = -1;
        _continuousAudioLastLogTimestamp = 0;
        AppLogger.Info($"Native combined recording audio stream started | PartPath={paths.PartPath} | FinalPath={paths.FinalPath} | ReservePath={paths.ReservePath} | AudioFormat={audioFormat}");
        _continuousRawPath = paths.PartPath;
        _continuousFinalPath = paths.FinalPath;
        _continuousReservePath = paths.ReservePath;
        _continuousStartedTimestamp = Environment.TickCount64;
    }
    // ...Запуск безперервного запису відео з аудіо

    // Запуск безперервного запису відео без аудіо ...
    public void StartContinuousRecording(string outputFolder)
    {
        ThrowIfDisposed();
        if (!IsRunning) throw new InvalidOperationException("GPU capture pipeline is not running.");
        if (_continuousRawPath is not null)
            throw new InvalidOperationException("Continuous video recording is already active.");
        var paths = CreateContinuousRecordingPaths(outputFolder);
        CreateFinalizeReserve(paths.ReservePath);
        try
        {
            ThrowIfFailed(NativeMethods.StartRecording(_handle, paths.PartPath), _handle);
        }
        catch
        {
            TryDelete(paths.ReservePath);
            TryDelete(paths.PartPath);
            throw;
        }
        _continuousRawPath = paths.PartPath;
        _continuousFinalPath = paths.FinalPath;
        _continuousReservePath = paths.ReservePath;
        _continuousStartedTimestamp = Environment.TickCount64;
        AppLogger.Info($"Native video recording started | PartPath={paths.PartPath} | FinalPath={paths.FinalPath} | ReservePath={paths.ReservePath}");
    }
    // ...Запуск безперервного запису відео без аудіо

    // Зупинка та фіналізація безперервного відеозапису ...
    public async Task<ContinuousVideoResult> StopContinuousRecordingAsync(
        string outputFolder)
    {
        ThrowIfDisposed();
        string nativeMp4Path = _continuousRawPath
            ?? throw new InvalidOperationException("Continuous video recording is not active.");
        string outputPath = _continuousFinalPath
            ?? throw new InvalidOperationException("Continuous recording final path is unavailable.");
        long continuousStartedTimestamp = _continuousStartedTimestamp;
        ReleaseFinalizeReserve();
        var export = NativeExportResult.Create();
        Exception? finalizeFailure = null;
        try
        {
            ThrowIfFailed(NativeMethods.StopRecording(_handle, ref export), _handle);
        }
        catch (Exception ex)
        {
            finalizeFailure = ex;
            AppLogger.Error(nameof(GpuCapturePipeline), $"Fragmented MP4 finalize failed; preserving playable fragments: {ex}");
        }
        finally
        {
            _continuousRawPath = null;
            _continuousFinalPath = null;
            _continuousStartedTimestamp = 0;
        }

        if (!File.Exists(nativeMp4Path) || new FileInfo(nativeMp4Path).Length == 0)
            throw finalizeFailure ?? new InvalidOperationException("Continuous recording produced an empty file.");

        await Task.Run(() => File.Move(nativeMp4Path, outputPath, true));
        if (finalizeFailure is not null)
        {
            GpuCaptureStats stats = GetStats();
            long recoveredStart = continuousStartedTimestamp > 0
                ? continuousStartedTimestamp
                : _startTimestamp;
            export.StartTimestamp100ns = Math.Max(0, recoveredStart - _startTimestamp) * 10_000;
            export.EndTimestamp100ns = Math.Max(
                export.StartTimestamp100ns + 1,
                (Environment.TickCount64 - _startTimestamp) * 10_000);
            export.FrameCount = stats.EncodedFrames;
            AppLogger.Info($"Fragmented MP4 recovered without finalization | Path={outputPath} | Bytes={new FileInfo(outputPath).Length}");
        }

        AppLogger.Info($"Native combined recording audio stream stopped | Writes={_continuousAudioWriteCount} | Bytes={_continuousAudioWriteBytes} | LastTimestamp100ns={_continuousAudioLastTimestamp100ns}");
        double exportFps = CalculateExportFps(export);

        LogExportDiagnostics(
            "Continuous",
            export,
            exportFps,
            outputPath);

        return new ContinuousVideoResult(
            outputPath,
            _startTimestamp + export.StartTimestamp100ns / 10_000,
            _startTimestamp + export.EndTimestamp100ns / 10_000,
            export.FrameCount);
    }
    // ...Зупинка та фіналізація безперервного відеозапису


    // Передавання змішаного аудіо в native writer ...
    public void WriteContinuousAudio(WaveFormat format, byte[] buffer, int count, long packetStartTimestamp, long packetDurationMilliseconds)
    {
        if (_continuousRawPath is null || count <= 0 || packetDurationMilliseconds <= 0) return;
        long timestamp100ns = Math.Max(0, packetStartTimestamp - _startTimestamp) * 10_000L;
        long duration100ns = Math.Max(1, packetDurationMilliseconds) * 10_000L;
        if (_continuousAudioLastTimestamp100ns >= 0 && timestamp100ns <= _continuousAudioLastTimestamp100ns)
        {
            AppLogger.Info($"Native combined audio timestamp adjusted by native writer | Timestamp100ns={timestamp100ns} | LastTimestamp100ns={_continuousAudioLastTimestamp100ns} | Duration100ns={duration100ns} | Bytes={count}");
        }

        NativeResult result = NativeMethods.WriteRecordingAudio(_handle, NativeAudioStreamKind.System, buffer, (uint)count, timestamp100ns, duration100ns);
        if (result == NativeResult.Ok)
        {
            _continuousAudioWriteCount++;
            _continuousAudioWriteBytes += count;
            _continuousAudioLastTimestamp100ns = timestamp100ns;
            long now = Environment.TickCount64;
            if (_continuousAudioWriteCount <= 3 || now - _continuousAudioLastLogTimestamp >= 2_000)
            {
                _continuousAudioLastLogTimestamp = now;
                AppLogger.Info($"Native combined audio write | Count={_continuousAudioWriteCount} | Bytes={count} | TotalBytes={_continuousAudioWriteBytes} | Timestamp100ns={timestamp100ns} | Duration100ns={duration100ns} | Format={format}");
            }
            return;
        }

        AppLogger.Info($"Native combined audio write skipped | Result={result} | Bytes={count} | Timestamp100ns={timestamp100ns} | Duration100ns={duration100ns} | Format={format} | Path={_continuousRawPath}");
        if (result != NativeResult.InvalidState)
            ThrowIfFailed(result, _handle);
    }
    // ...Передавання змішаного аудіо в native writer

    public GpuCaptureStats GetStats()
    {
        if (_handle.IsInvalid || _handle.IsClosed) return default;
        var native = NativeVideoStats.Create();
        ThrowIfFailed(NativeMethods.GetVideoStats(_handle, ref native), _handle);
        return new GpuCaptureStats(
            native.CapturedFrames,
            native.EncodedFrames,
            native.DroppedFrames,
            native.BufferedBytes,
            native.BufferedFrames,
            native.IsRunning != 0,
            native.IsRecording != 0);
    }

    // Зупинка GPU pipeline захоплення ...
    public void Stop()
    {
        if (!_handle.IsInvalid && !_handle.IsClosed && IsRunning)
            NativeMethods.StopVideoEngine(_handle);
        IsRunning = false;
        ReleaseFinalizeReserve();
        _continuousRawPath = null;
        _continuousFinalPath = null;
        _continuousStartedTimestamp = 0;
    }
    // ...Зупинка GPU pipeline захоплення

    // Звільнення ресурсів GPU pipeline ...
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _handle.Dispose();
        TryDeleteDirectory(_sessionDirectory);
    }
    // ...Звільнення ресурсів GPU pipeline

    private double CalculateExportFps(NativeExportResult export)
    {
        long elapsed100ns = export.EndTimestamp100ns - export.StartTimestamp100ns;
        if (elapsed100ns <= 0 || export.FrameCount == 0)
            return _fps;

        double elapsedSeconds = elapsed100ns / 10_000_000.0;
        double actualFps = export.FrameCount / elapsedSeconds;
        return Math.Clamp(actualFps, 1.0, _fps);
    }

    private void LogExportDiagnostics(
        string exportKind,
        NativeExportResult export,
        double remuxFps,
        string outputPath)
    {
        long elapsed100ns = export.EndTimestamp100ns - export.StartTimestamp100ns;
        double elapsedSeconds = Math.Max(0, elapsed100ns / 10_000_000.0);
        double actualFps = elapsedSeconds > 0 ? export.FrameCount / elapsedSeconds : 0;
        long outputBytes = 0;

        try
        {
            if (File.Exists(outputPath))
                outputBytes = new FileInfo(outputPath).Length;
        }
        catch
        {
        }

        AppLogger.Info(
            $"Video export diagnostics | Kind={exportKind} | " +
            $"ConfiguredFps={_fps} | RemuxFps={remuxFps.ToString("0.###", CultureInfo.InvariantCulture)} | " +
            $"FrameCount={export.FrameCount} | DurationSec={elapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture)} | " +
            $"ActualFps={actualFps.ToString("0.###", CultureInfo.InvariantCulture)} | " +
            $"Start100ns={export.StartTimestamp100ns} | End100ns={export.EndTimestamp100ns} | " +
            $"OutputBytes={outputBytes}");
    }

    private static IntPtr ResolveTarget(string captureTarget)
    {
        DisplayMonitor screen = DisplayMonitorService.Resolve(captureTarget);
        IntPtr monitor = DisplayMonitorService.MonitorFromPoint(
            screen.Bounds.Left + 1,
            screen.Bounds.Top + 1);

        if (monitor == IntPtr.Zero)
            throw new InvalidOperationException("The selected monitor is no longer available.");

        return monitor;
    }

    private static (string PartPath, string FinalPath, string ReservePath)
        CreateContinuousRecordingPaths(string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        string finalPath = OutputFileName.Create(outputFolder, "Record", ".mp4");
        string sessionFolder = Path.Combine(outputFolder, ".skadi-session");
        Directory.CreateDirectory(sessionFolder);
        try
        {
            File.SetAttributes(
                sessionFolder,
                File.GetAttributes(sessionFolder) | FileAttributes.Hidden);
        }
        catch
        {
        }

        string token = Guid.NewGuid().ToString("N");
        string partPath = Path.Combine(
            sessionFolder,
            $"{Path.GetFileNameWithoutExtension(finalPath)}-{token}.part.mp4");
        string reservePath = Path.Combine(sessionFolder, $"{token}.finalize-reserve");
        return (partPath, finalPath, reservePath);
    }

    // Відновлення перерваних відеозаписів ...
    public static void RecoverInterruptedRecordings(string outputFolder)
    {
        try
        {
            string sessionFolder = Path.Combine(outputFolder, ".skadi-session");
            if (!Directory.Exists(sessionFolder)) return;

            foreach (string reservePath in Directory.EnumerateFiles(sessionFolder, "*.finalize-reserve"))
            {
                TryDelete(reservePath);
                AppLogger.Info($"Stale recording finalize reserve removed | Path={reservePath}");
            }

            foreach (string partPath in Directory.EnumerateFiles(sessionFolder, "*.part.mp4"))
            {
                try
                {
                    var info = new FileInfo(partPath);
                    if (info.Length == 0)
                    {
                        TryDelete(partPath);
                        continue;
                    }

                    string recoveredPath = OutputFileName.Create(
                        outputFolder,
                        "Recovered Record",
                        ".mp4",
                        info.LastWriteTime);
                    File.Move(partPath, recoveredPath);
                    AppLogger.Info($"Interrupted fragmented MP4 recovered | Source={partPath} | Path={recoveredPath} | Bytes={info.Length}");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(nameof(GpuCapturePipeline), $"Interrupted recording recovery failed | Path={partPath} | Error={ex}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(GpuCapturePipeline), $"Interrupted recording scan failed | Folder={outputFolder} | Error={ex}");
        }
    }
    // ...Відновлення перерваних відеозаписів

    private static void CreateFinalizeReserve(string reservePath)
    {
        using var reserve = new FileStream(
            reservePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        reserve.SetLength(FinalizeReserveBytes);
        reserve.Flush(flushToDisk: true);
    }

    private void ReleaseFinalizeReserve()
    {
        string? reservePath = _continuousReservePath;
        _continuousReservePath = null;
        if (string.IsNullOrWhiteSpace(reservePath)) return;
        TryDelete(reservePath);
        AppLogger.Info($"Recording finalize reserve released | Path={reservePath} | Bytes={FinalizeReserveBytes}");
    }

    private static void ThrowIfFailed(NativeResult result, SafeVideoEngineHandle? handle)
    {
        if (result == NativeResult.Ok) return;
        string message = "Native GPU pipeline failed.";
        if (handle is not null && !handle.IsInvalid && !handle.IsClosed)
        {
            uint required = NativeMethods.GetLastError(handle, null, 0);
            if (required > 1)
            {
                var buffer = new StringBuilder((int)required);
                NativeMethods.GetLastError(handle, buffer, (uint)buffer.Capacity);
                message = buffer.ToString();
            }
        }
        throw new InvalidOperationException($"{message} NativeResult={result}.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}

public sealed record ContinuousVideoResult(
    string VideoPath,
    long StartTimestamp,
    long EndTimestamp,
    ulong FrameCount);

public readonly record struct GpuCaptureStats(
    ulong CapturedFrames,
    ulong EncodedFrames,
    ulong DroppedFrames,
    ulong BufferedBytes,
    ulong BufferedFrames,
    bool IsRunning,
    bool IsRecording);
