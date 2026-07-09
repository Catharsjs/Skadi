using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using EventCapture.Core.Diagnostics;
using NAudio.Wave;
using Microsoft.Win32.SafeHandles;

namespace EventCapture.Core.Capture;

public sealed class GpuCapturePipeline : IVideoCapturePipeline, IContinuousAudioSink
{
    private readonly int _fps;
    private readonly SafeVideoEngineHandle _handle;
    private readonly string _sessionDirectory;
    private long _startTimestamp;
    private string? _continuousRawPath;
    private long _continuousAudioWriteCount;
    private long _continuousAudioWriteBytes;
    private long _continuousAudioLastTimestamp100ns = -1;
    private long _continuousAudioLastLogTimestamp;
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

    public void Start()
    {
        ThrowIfDisposed();
        if (IsRunning) return;
        ThrowIfFailed(NativeMethods.StartVideoEngine(_handle), _handle);
        _startTimestamp = Environment.TickCount64;
        IsRunning = true;
    }

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

    public void StartContinuousRecording(WaveFormat? audioFormat)
    {
        ThrowIfDisposed();
        if (!IsRunning) throw new InvalidOperationException("GPU capture pipeline is not running.");
        if (_continuousRawPath is not null)
            throw new InvalidOperationException("Continuous video recording is already active.");
        string path = Path.Combine(_sessionDirectory, $"recording-{Guid.NewGuid():N}.mp4");
        NativeAudioStreamConfig audioConfig = NativeAudioStreamConfig.From(audioFormat);
        NativeAudioStreamConfig disabledConfig = default;
        ThrowIfFailed(NativeMethods.StartRecordingWithAudio(_handle, path, ref audioConfig, ref disabledConfig), _handle);
        _continuousAudioWriteCount = 0;
        _continuousAudioWriteBytes = 0;
        _continuousAudioLastTimestamp100ns = -1;
        _continuousAudioLastLogTimestamp = 0;
        AppLogger.Info($"Native combined recording audio stream started | Path={path} | AudioFormat={audioFormat}");
        _continuousRawPath = path;
    }

    public void StartContinuousRecording()
    {
        ThrowIfDisposed();
        if (!IsRunning) throw new InvalidOperationException("GPU capture pipeline is not running.");
        if (_continuousRawPath is not null)
            throw new InvalidOperationException("Continuous video recording is already active.");
        string path = Path.Combine(_sessionDirectory, $"recording-{Guid.NewGuid():N}.mp4");
        ThrowIfFailed(NativeMethods.StartRecording(_handle, path), _handle);
        _continuousRawPath = path;
    }

    public async Task<ContinuousVideoResult> StopContinuousRecordingAsync(
        string outputFolder)
    {
        ThrowIfDisposed();
        string nativeMp4Path = _continuousRawPath
            ?? throw new InvalidOperationException("Continuous video recording is not active.");
        var export = NativeExportResult.Create();
        ThrowIfFailed(NativeMethods.StopRecording(_handle, ref export), _handle);
        AppLogger.Info($"Native combined recording audio stream stopped | Writes={_continuousAudioWriteCount} | Bytes={_continuousAudioWriteBytes} | LastTimestamp100ns={_continuousAudioLastTimestamp100ns}");
        _continuousRawPath = null;
        string outputPath = OutputFileName.Create(outputFolder, "Record", ".mp4");
        double exportFps = CalculateExportFps(export);
        await Task.Run(() => File.Move(nativeMp4Path, outputPath, true));

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


    public void WriteContinuousAudio(ContinuousAudioSource source, WaveFormat format, byte[] buffer, int count, long packetStartTimestamp, long packetDurationMilliseconds)
    {
        if (_continuousRawPath is null || count <= 0 || packetDurationMilliseconds <= 0) return;
        long timestamp100ns = Math.Max(0, packetStartTimestamp - _startTimestamp) * 10_000L;
        long duration100ns = Math.Max(1, packetDurationMilliseconds) * 10_000L;
        if (_continuousAudioLastTimestamp100ns >= 0 && timestamp100ns <= _continuousAudioLastTimestamp100ns)
        {
            AppLogger.Info($"Native combined audio timestamp adjusted by native writer | Source={source} | Timestamp100ns={timestamp100ns} | LastTimestamp100ns={_continuousAudioLastTimestamp100ns} | Duration100ns={duration100ns} | Bytes={count}");
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
                AppLogger.Info($"Native combined audio write | Source={source} | Count={_continuousAudioWriteCount} | Bytes={count} | TotalBytes={_continuousAudioWriteBytes} | Timestamp100ns={timestamp100ns} | Duration100ns={duration100ns} | Format={format}");
            }
            return;
        }

        AppLogger.Info($"Native combined audio write skipped | Source={source} | Result={result} | Bytes={count} | Timestamp100ns={timestamp100ns} | Duration100ns={duration100ns} | Format={format} | Path={_continuousRawPath}");
        if (result != NativeResult.InvalidState)
            ThrowIfFailed(result, _handle);
    }
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

    public void Stop()
    {
        if (_handle.IsInvalid || _handle.IsClosed || !IsRunning) return;
        NativeMethods.StopVideoEngine(_handle);
        IsRunning = false;
        _continuousRawPath = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _handle.Dispose();
        TryDeleteDirectory(_sessionDirectory);
    }

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

    private static async Task RemuxH264Async(string inputPath, string outputPath, double fps)
    {
        string ffmpegPath = FfmpegLocator.GetFfmpegPath();
        string fpsText = fps.ToString("0.###", CultureInfo.InvariantCulture);
        string arguments =
            $"-y -hide_banner -loglevel error -fflags +genpts -r {fpsText} " +
            $"-i \"{inputPath}\" -c:v copy -movflags +faststart \"{outputPath}\"";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.Start();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"H.264 remux failed: {error}");
    }

    private static IntPtr ResolveTarget(string captureTarget)
    {
        if (captureTarget.StartsWith("Window|", StringComparison.Ordinal))
        {
            captureTarget = "PrimaryMonitor";
        }

        DisplayMonitor screen = DisplayMonitorService.Resolve(captureTarget);
        IntPtr monitor = DisplayMonitorService.MonitorFromPoint(
            screen.Bounds.Left + 1,
            screen.Bounds.Top + 1);

        if (monitor == IntPtr.Zero)
            throw new InvalidOperationException("The selected monitor is no longer available.");

        return monitor;
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


internal enum NativeAudioStreamKind { System = 0, Microphone = 1 }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAudioStreamConfig
{
    public uint SampleRate;
    public uint Channels;
    public uint BitsPerSample;
    public int Enabled;

    public static NativeAudioStreamConfig From(WaveFormat? format)
    {
        if (format is null) return default;
        int bits = format.Encoding == WaveFormatEncoding.IeeeFloat ? 16 : format.BitsPerSample;
        if (format.SampleRate <= 0 || format.Channels <= 0 || bits <= 0) return default;
        return new NativeAudioStreamConfig
        {
            SampleRate = (uint)format.SampleRate,
            Channels = (uint)format.Channels,
            BitsPerSample = (uint)bits,
            Enabled = 1
        };
    }
}
internal enum NativeResult { Ok, InvalidArgument, InvalidState, NotSupported, Timeout, NativeFailure }
internal enum NativeTargetKind { Monitor, Window }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeVideoConfig
{
    public uint StructSize;
    public NativeTargetKind TargetKind;
    public IntPtr TargetHandle;
    public uint OutputWidth;
    public uint OutputHeight;
    public uint FramesPerSecond;
    public uint BitrateKbps;
    public uint ReplaySeconds;
    public int EnableReplay;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeExportResult
{
    public uint StructSize;
    public long StartTimestamp100ns;
    public long EndTimestamp100ns;
    public ulong FrameCount;
    public static NativeExportResult Create() => new() { StructSize = (uint)Marshal.SizeOf<NativeExportResult>() };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeVideoStats
{
    public uint StructSize;
    public ulong CapturedFrames;
    public ulong EncodedFrames;
    public ulong DroppedFrames;
    public ulong BufferedBytes;
    public ulong BufferedFrames;
    public int IsRunning;
    public int IsRecording;
    public static NativeVideoStats Create() => new() { StructSize = (uint)Marshal.SizeOf<NativeVideoStats>() };
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativePoint(int x, int y)
{
    public readonly int X = x;
    public readonly int Y = y;
}


internal sealed class SafeVideoEngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeVideoEngineHandle() : base(true) { }
    protected override bool ReleaseHandle()
    {
        NativeMethods.DestroyVideoEngine(handle);
        return true;
    }
}

internal static class NativeMethods
{
    private const string Library = "EventCapture.Native.dll";
    [DllImport(Library, EntryPoint = "EcCreateVideoEngine")]
    internal static extern NativeResult CreateVideoEngine(in NativeVideoConfig config, out SafeVideoEngineHandle handle);
    [DllImport(Library, EntryPoint = "EcStartVideoEngine")]
    internal static extern NativeResult StartVideoEngine(SafeVideoEngineHandle handle);
    [DllImport(Library, EntryPoint = "EcSaveReplay", CharSet = CharSet.Unicode)]
    internal static extern NativeResult SaveReplay(SafeVideoEngineHandle handle, string path, uint seconds, ref NativeExportResult result);
    [DllImport(Library, EntryPoint = "EcStartRecording", CharSet = CharSet.Unicode)]
    internal static extern NativeResult StartRecording(SafeVideoEngineHandle handle, string path);
    [DllImport(Library, EntryPoint = "EcStartRecordingWithAudio", CharSet = CharSet.Unicode)]
    internal static extern NativeResult StartRecordingWithAudio(SafeVideoEngineHandle handle, string path, ref NativeAudioStreamConfig systemAudio, ref NativeAudioStreamConfig microphoneAudio);
    [DllImport(Library, EntryPoint = "EcWriteRecordingAudio")]
    internal static extern NativeResult WriteRecordingAudio(SafeVideoEngineHandle handle, NativeAudioStreamKind streamKind, byte[] data, uint byteCount, long timestamp100ns, long duration100ns);
    [DllImport(Library, EntryPoint = "EcStopRecording")]
    internal static extern NativeResult StopRecording(SafeVideoEngineHandle handle, ref NativeExportResult result);
    [DllImport(Library, EntryPoint = "EcGetVideoStats")]
    internal static extern NativeResult GetVideoStats(SafeVideoEngineHandle handle, ref NativeVideoStats stats);
    [DllImport(Library, EntryPoint = "EcStopVideoEngine")]
    internal static extern NativeResult StopVideoEngine(SafeVideoEngineHandle handle);
    [DllImport(Library, EntryPoint = "EcDestroyVideoEngine")]
    internal static extern void DestroyVideoEngine(IntPtr handle);
    [DllImport(Library, EntryPoint = "EcGetLastError", CharSet = CharSet.Unicode)]
    internal static extern uint GetLastError(SafeVideoEngineHandle handle, StringBuilder? buffer, uint capacity);
    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
}
