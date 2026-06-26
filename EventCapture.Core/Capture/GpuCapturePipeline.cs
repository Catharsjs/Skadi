using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EventCapture.Core.Capture;

public sealed class GpuCapturePipeline : IDisposable
{
    private readonly int _fps;
    private readonly SafeVideoEngineHandle _handle;
    private readonly string _sessionDirectory;
    private long _startTimestamp;
    private string? _continuousRawPath;
    private bool _disposed;

    public GpuCapturePipeline(
        int fps,
        int width,
        int height,
        int bitrateKbps,
        int bufferSeconds,
        string captureTarget)
    {
        _fps = Math.Max(1, fps);
        var target = ResolveTarget(captureTarget);
        var config = new NativeVideoConfig
        {
            StructSize = (uint)Marshal.SizeOf<NativeVideoConfig>(),
            TargetKind = target.IsWindow ? NativeTargetKind.Window : NativeTargetKind.Monitor,
            TargetHandle = target.Handle,
            OutputWidth = (uint)Math.Max(2, width & ~1),
            OutputHeight = (uint)Math.Max(2, height & ~1),
            FramesPerSecond = (uint)_fps,
            BitrateKbps = (uint)Math.Max(1_000, bitrateKbps),
            ReplaySeconds = (uint)Math.Max(5, bufferSeconds)
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
        Directory.CreateDirectory(outputFolder);
        string rawPath = Path.Combine(_sessionDirectory, $"replay-{Guid.NewGuid():N}.h264");
        var export = NativeExportResult.Create();
        ThrowIfFailed(
            NativeMethods.SaveReplay(_handle, rawPath, (uint)Math.Max(1, seconds), ref export),
            _handle);

        string outputPath = Path.Combine(
            outputFolder,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..8]}.mp4");
        try
        {
            await RemuxH264Async(rawPath, outputPath, _fps);
        }
        finally
        {
            TryDelete(rawPath);
        }

        long startMilliseconds = export.StartTimestamp100ns / 10_000;
        long elapsedMilliseconds = Math.Max(
            1,
            (export.EndTimestamp100ns - export.StartTimestamp100ns) / 10_000);
        return (outputPath, elapsedMilliseconds, _startTimestamp + startMilliseconds);
    }

    public void StartContinuousRecording()
    {
        ThrowIfDisposed();
        if (!IsRunning) throw new InvalidOperationException("GPU capture pipeline is not running.");
        if (_continuousRawPath is not null)
            throw new InvalidOperationException("Continuous video recording is already active.");
        string path = Path.Combine(_sessionDirectory, $"recording-{Guid.NewGuid():N}.h264");
        ThrowIfFailed(NativeMethods.StartRecording(_handle, path), _handle);
        _continuousRawPath = path;
    }

    public async Task<ContinuousVideoResult> StopContinuousRecordingAsync(
        string outputFolder)
    {
        ThrowIfDisposed();
        string rawPath = _continuousRawPath
            ?? throw new InvalidOperationException("Continuous video recording is not active.");
        var export = NativeExportResult.Create();
        ThrowIfFailed(NativeMethods.StopRecording(_handle, ref export), _handle);
        _continuousRawPath = null;
        Directory.CreateDirectory(outputFolder);
        string outputPath = Path.Combine(
            outputFolder,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..8]}_video.mp4");
        try
        {
            await RemuxH264Async(rawPath, outputPath, _fps);
        }
        finally
        {
            TryDelete(rawPath);
        }

        return new ContinuousVideoResult(
            outputPath,
            _startTimestamp + export.StartTimestamp100ns / 10_000,
            _startTimestamp + export.EndTimestamp100ns / 10_000,
            export.FrameCount);
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

    private static async Task RemuxH264Async(string inputPath, string outputPath, int fps)
    {
        string ffmpegPath = FFMpegCore.GlobalFFOptions.GetFFMpegBinaryPath();
        string arguments =
            $"-y -hide_banner -loglevel error -fflags +genpts -r {fps} " +
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

    private static (bool IsWindow, IntPtr Handle) ResolveTarget(string captureTarget)
    {
        if (captureTarget.StartsWith("Window|", StringComparison.Ordinal))
        {
            string[] parts = captureTarget.Split('|', 3);
            if (parts.Length >= 2 &&
                long.TryParse(parts[1], NumberStyles.HexNumber, null, out long value) &&
                NativeMethods.IsWindow(new IntPtr(value)))
            {
                return (true, new IntPtr(value));
            }
            throw new InvalidOperationException("The selected window is no longer available.");
        }

        System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.PrimaryScreen
            ?? System.Windows.Forms.Screen.AllScreens.First();
        if (captureTarget.StartsWith("Monitor|", StringComparison.Ordinal))
        {
            string deviceName = captureTarget["Monitor|".Length..];
            screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                ?? screen;
        }

        IntPtr monitor = NativeMethods.MonitorFromPoint(
            new NativePoint(screen.Bounds.Left + 1, screen.Bounds.Top + 1),
            2);
        if (monitor == IntPtr.Zero)
            throw new InvalidOperationException("The selected monitor is no longer available.");
        return (false, monitor);
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
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
}
