using System.Diagnostics;
using System.Globalization;
using EventCapture.Core.Diagnostics;

namespace EventCapture.Core.Capture;

public sealed class FfmpegMonitorCapturePipeline : IVideoCapturePipeline
{
    private const int SegmentSeconds = 2;

    private readonly object _sync = new();
    private readonly int _fps;
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private readonly int _bitrateKbps;
    private readonly int _bufferSeconds;
    private readonly bool _enableReplay;
    private readonly DisplayMonitor _monitor;
    private readonly string _sessionDirectory;
    private readonly ReplaySegmentBuffer _segments;
    private readonly HashSet<string> _committedSegments = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _segmentListPath;
    private readonly string _segmentPattern;
    private Process? _replayProcess;
    private Task<string>? _replayErrorTask;
    private Process? _continuousProcess;
    private Task<string>? _continuousErrorTask;
    private string? _continuousPath;
    private long _continuousStartTimestamp;
    private long _latestSegmentEndTimestamp;
    private bool _disposed;

    public FfmpegMonitorCapturePipeline(
        int fps,
        int outputWidth,
        int outputHeight,
        int bitrateKbps,
        int bufferSeconds,
        string captureTarget,
        bool enableReplay)
    {
        _fps = Math.Clamp(fps, 1, 60);
        _outputWidth = Math.Max(2, outputWidth & ~1);
        _outputHeight = Math.Max(2, outputHeight & ~1);
        _bitrateKbps = Math.Max(1_000, bitrateKbps);
        _bufferSeconds = Math.Max(5, bufferSeconds);
        _enableReplay = enableReplay;
        _monitor = DisplayMonitorService.Resolve(captureTarget);
        _sessionDirectory = ReplaySessionStorage.CreateSessionDirectory("ffmpeg-monitor");
        _segments = new ReplaySegmentBuffer(TimeSpan.FromSeconds(_bufferSeconds + SegmentSeconds * 4));
        _segmentListPath = Path.Combine(_sessionDirectory, "segments.csv");
        _segmentPattern = Path.Combine(_sessionDirectory, "segment_%08d.mp4");
    }

    public bool IsRunning { get; private set; }
    public bool IsContinuousRecording => _continuousProcess is not null;
    public long StartTimestamp { get; private set; }
    public long FramesCaptured => 0;

    public void Start()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (IsRunning) return;
            StartTimestamp = Environment.TickCount64;
            _latestSegmentEndTimestamp = StartTimestamp;
            IsRunning = true;

            if (_enableReplay)
                StartReplayProcessCore();

            AppLogger.Info(
                $"FFmpeg monitor pipeline started | Target={_monitor.DeviceName} | " +
                $"Bounds={_monitor.Bounds} | Output={_outputWidth}x{_outputHeight} | " +
                $"FPS={_fps} | Bitrate={_bitrateKbps}kbps | Replay={_enableReplay}");
        }
    }

    public async Task<(string videoPath, long videoElapsedMs, long videoStartTimestamp)>
        SaveLastSecondsAsync(string outputFolder, int seconds)
    {
        ThrowIfDisposed();
        if (!IsRunning || !_enableReplay)
            throw new InvalidOperationException("Replay buffer is not running.");

        long requestedEnd = Environment.TickCount64;
        await WaitForSegmentCoverageAsync(requestedEnd, TimeSpan.FromSeconds(SegmentSeconds + 2));

        ReplaySegmentBuffer.Lease lease;
        long requestedStart;
        long effectiveEnd;
        lock (_sync)
        {
            RefreshSegmentsCore();
            effectiveEnd = Math.Min(requestedEnd, _latestSegmentEndTimestamp);
            requestedStart = Math.Max(StartTimestamp, effectiveEnd - Math.Max(1, seconds) * 1000L);
            lease = _segments.Acquire(requestedStart, effectiveEnd);
        }

        using (lease)
        {
            if (lease.Segments.Count == 0)
                throw new InvalidOperationException("Replay buffer does not contain finalized video segments yet.");

            Directory.CreateDirectory(outputFolder);
            string outputPath = Path.Combine(
                outputFolder,
                $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..8]}.mp4");

            await ConcatenateSegmentsAsync(
                lease.Segments,
                outputPath,
                requestedStart,
                effectiveEnd);

            long elapsed = Math.Max(1, effectiveEnd - requestedStart);
            AppLogger.Info(
                $"FFmpeg replay diagnostics | RequestedSec={seconds} | " +
                $"DurationSec={(elapsed / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)} | " +
                $"Segments={lease.Segments.Count} | OutputBytes={new FileInfo(outputPath).Length}");

            return (outputPath, elapsed, requestedStart);
        }
    }

    public void StartContinuousRecording()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (!IsRunning) Start();
            if (_continuousProcess is not null)
                throw new InvalidOperationException("Continuous video recording is already active.");

            _continuousStartTimestamp = Environment.TickCount64;
            _continuousPath = Path.Combine(_sessionDirectory, $"recording-{Guid.NewGuid():N}.mp4");
            _continuousProcess = StartFfmpegProcess(CreateDirectRecordingArguments(_continuousPath));
            _continuousErrorTask = _continuousProcess.StandardError.ReadToEndAsync();

            AppLogger.Info(
                $"FFmpeg continuous recording started | Output={_outputWidth}x{_outputHeight} | " +
                $"FPS={_fps} | Bitrate={_bitrateKbps}kbps");
        }
    }

    public async Task<ContinuousVideoResult> StopContinuousRecordingAsync(string outputFolder)
    {
        ThrowIfDisposed();

        Process process;
        Task<string>? errorTask;
        string sourcePath;
        long startTimestamp;
        long endTimestamp = Environment.TickCount64;

        lock (_sync)
        {
            if (_continuousProcess is null || string.IsNullOrWhiteSpace(_continuousPath))
                throw new InvalidOperationException("Continuous video recording is not active.");

            process = _continuousProcess;
            errorTask = _continuousErrorTask;
            sourcePath = _continuousPath;
            startTimestamp = _continuousStartTimestamp;
            _continuousProcess = null;
            _continuousErrorTask = null;
            _continuousPath = null;
            _continuousStartTimestamp = 0;
        }

        await StopFfmpegProcessAsync(process, errorTask, "FFmpeg continuous recording");

        Directory.CreateDirectory(outputFolder);
        string outputPath = Path.Combine(
            outputFolder,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..8]}_video.mp4");

        File.Move(sourcePath, outputPath, overwrite: true);
        long elapsed = Math.Max(1, endTimestamp - startTimestamp);
        ulong frameCount = (ulong)Math.Max(1L, (long)Math.Round(elapsed / 1000.0 * _fps));

        AppLogger.Info(
            $"Video export diagnostics | Kind=ContinuousFFmpeg | ConfiguredFps={_fps} | " +
            $"RemuxFps={_fps} | FrameCount={frameCount} | " +
            $"DurationSec={(elapsed / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)} | " +
            $"ActualFps={_fps} | OutputBytes={new FileInfo(outputPath).Length}");

        return new ContinuousVideoResult(outputPath, startTimestamp, endTimestamp, frameCount);
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_continuousProcess is not null)
            {
                try { _continuousProcess.Kill(entireProcessTree: true); } catch { }
                _continuousProcess.Dispose();
                _continuousProcess = null;
            }

            if (_replayProcess is not null)
            {
                try { _replayProcess.Kill(entireProcessTree: true); } catch { }
                _replayProcess.Dispose();
                _replayProcess = null;
            }

            IsRunning = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _segments.Dispose();
        TryDeleteDirectory(_sessionDirectory);
    }

    private void StartReplayProcessCore()
    {
        _replayProcess = StartFfmpegProcess(CreateSegmentRecordingArguments());
        _replayErrorTask = _replayProcess.StandardError.ReadToEndAsync();
    }

    private Process StartFfmpegProcess(string arguments)
    {
        string ffmpegPath = FfmpegLocator.GetFfmpegPath();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.Start();
        return process;
    }

    private string CreateDirectRecordingArguments(string outputPath)
    {
        return $"{CreateInputArguments()} {CreateEncoderArguments()} -an -movflags +faststart \"{outputPath}\"";
    }

    private string CreateSegmentRecordingArguments()
    {
        string forceKeyFrames = $"expr:gte(t,n_forced*{SegmentSeconds})";
        int segmentListSize = Math.Max(4, (int)Math.Ceiling((_bufferSeconds + SegmentSeconds * 4) / (double)SegmentSeconds) + 2);
        return
            $"{CreateInputArguments()} {CreateEncoderArguments()} -an " +
            $"-force_key_frames \"{forceKeyFrames}\" " +
            $"-f segment -segment_time {SegmentSeconds} -segment_time_delta 0.05 " +
            $"-reset_timestamps 1 -segment_format mp4 " +
            $"-segment_list \"{_segmentListPath}\" -segment_list_type csv " +
            $"-segment_list_size {segmentListSize} \"{_segmentPattern}\"";
    }

    private string CreateInputArguments()
    {
        return
            "-hide_banner -loglevel warning -y " +
            "-thread_queue_size 1024 " +
            $"-f gdigrab -draw_mouse 1 -framerate {_fps} " +
            $"-offset_x {_monitor.Bounds.Left} -offset_y {_monitor.Bounds.Top} " +
            $"-video_size {_monitor.Bounds.Width}x{_monitor.Bounds.Height} -i desktop";
    }

    private string CreateEncoderArguments()
    {
        string scale =
            _monitor.Bounds.Width == _outputWidth && _monitor.Bounds.Height == _outputHeight
                ? "format=yuv420p"
                : $"scale={_outputWidth}:{_outputHeight}:flags=bilinear,format=yuv420p";

        return
            $"-vf \"{scale}\" " +
            $"-c:v libx264 -preset veryfast -tune zerolatency " +
            $"-b:v {_bitrateKbps}k -maxrate {_bitrateKbps}k -bufsize {_bitrateKbps * 2}k " +
            $"-r {_fps} -g {_fps * SegmentSeconds} -keyint_min {_fps * SegmentSeconds} " +
            "-sc_threshold 0 -bf 0";
    }

    private async Task StopFfmpegProcessAsync(Process process, Task<string>? errorTask, string operation)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q");
                await process.StandardInput.FlushAsync();
            }
        }
        catch
        {
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync();
        }

        string error = string.Empty;
        try
        {
            if (errorTask is not null) error = await errorTask;
        }
        catch
        {
        }

        int exitCode = process.ExitCode;
        process.Dispose();

        if (exitCode != 0)
            throw new InvalidOperationException($"{operation} failed: {error}");
    }

    private async Task WaitForSegmentCoverageAsync(long requestedEnd, TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            lock (_sync)
            {
                RefreshSegmentsCore();
                if (_latestSegmentEndTimestamp >= requestedEnd - 100) return;
                if (!IsRunning) break;
            }

            await Task.Delay(100);
        }
    }

    private void RefreshSegmentsCore()
    {
        if (!File.Exists(_segmentListPath)) return;

        string[] lines;
        try
        {
            using var stream = new FileStream(
                _segmentListPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var content = new List<string>();
            while (reader.ReadLine() is { } line) content.Add(line);
            lines = content.ToArray();
        }
        catch
        {
            return;
        }

        foreach (string line in lines)
        {
            string[] parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) continue;

            string relativePath = parts[0].Trim().Trim('"');
            string fullPath = Path.GetFullPath(Path.Combine(_sessionDirectory, relativePath));
            if (_committedSegments.Contains(fullPath) || !File.Exists(fullPath)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double startSeconds)) continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double endSeconds)) continue;

            long start = StartTimestamp + (long)Math.Round(startSeconds * 1000.0);
            long end = StartTimestamp + (long)Math.Round(endSeconds * 1000.0);
            if (end <= start || new FileInfo(fullPath).Length == 0) continue;

            _segments.Add(fullPath, start, end);
            _committedSegments.Add(fullPath);
            _latestSegmentEndTimestamp = Math.Max(_latestSegmentEndTimestamp, end);
        }

        _segments.Prune(Environment.TickCount64);
    }

    private async Task ConcatenateSegmentsAsync(
        IReadOnlyList<ReplaySegmentBuffer.Segment> segments,
        string outputPath,
        long requestedStartTimestamp,
        long requestedEndTimestamp)
    {
        string listPath = Path.Combine(_sessionDirectory, $"export_{Guid.NewGuid():N}.txt");
        string temporaryPath = Path.Combine(_sessionDirectory, $"concat_{Guid.NewGuid():N}.mp4");
        try
        {
            await File.WriteAllLinesAsync(
                listPath,
                segments.Select(segment => $"file '{segment.Path.Replace('\\', '/').Replace("'", "'\\''")}'"));

            string concatArguments =
                $"-hide_banner -loglevel warning -y -f concat -safe 0 -i \"{listPath}\" " +
                $"-map 0:v:0 -an -c:v copy -movflags +faststart \"{temporaryPath}\"";

            await FfmpegLocator.RunAsync(concatArguments, "FFmpeg replay concat");

            long firstSegmentStart = segments[0].StartTimestamp;
            double trimStartSeconds = Math.Max(0, (requestedStartTimestamp - firstSegmentStart) / 1000.0);
            double trimDurationSeconds = Math.Max(0.001, (requestedEndTimestamp - requestedStartTimestamp) / 1000.0);
            string trimArguments =
                $"-hide_banner -loglevel warning -y " +
                $"-ss {FormatSeconds(trimStartSeconds)} -i \"{temporaryPath}\" " +
                $"-t {FormatSeconds(trimDurationSeconds)} " +
                $"-map 0:v:0 -an -c:v copy -movflags +faststart \"{outputPath}\"";

            await FfmpegLocator.RunAsync(trimArguments, "FFmpeg replay trim");
        }
        finally
        {
            TryDelete(listPath);
            TryDelete(temporaryPath);
        }
    }

    private static string FormatSeconds(double seconds)
    {
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
