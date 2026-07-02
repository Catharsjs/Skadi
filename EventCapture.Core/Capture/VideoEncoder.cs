using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using EventCapture.Core.Diagnostics;

namespace EventCapture.Core.Capture;

public sealed class VideoEncoder : IDisposable
{
    private const int SegmentSeconds = 2;

    private readonly object _writeLock = new();
    private readonly int _fps;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bitrateKbps;
    private readonly int _segmentListSize;
    private readonly ReplaySegmentBuffer _segments;
    private readonly string _sessionDirectory;
    private readonly string _segmentListPath;
    private readonly string _segmentPattern;
    private readonly HashSet<string> _committedSegments = new(StringComparer.OrdinalIgnoreCase);

    private Process? _ffmpeg;
    private Stream? _videoInput;
    private Task<string>? _stderrTask;
    private byte[]? _scaledFrameBuffer;
    private bool _isRunning;
    private bool _disposed;
    private long _latestSegmentEndTimestamp;

    public VideoEncoder(
        int fps,
        int width,
        int height,
        int bitrate = 8_000,
        int bufferSeconds = 60,
        int segmentMilliseconds = SegmentSeconds * 1000)
    {
        _fps = Math.Clamp(fps, 1, 240);
        _width = Math.Max(2, width);
        _height = Math.Max(2, height);
        _bitrateKbps = Math.Max(1_000, bitrate);
        int retentionSeconds = Math.Max(5, bufferSeconds) + SegmentSeconds * 3;
        _segmentListSize = Math.Max(4, (int)Math.Ceiling(retentionSeconds / (double)SegmentSeconds) + 2);
        _segments = new ReplaySegmentBuffer(TimeSpan.FromSeconds(retentionSeconds));
        _sessionDirectory = ReplaySessionStorage.CreateSessionDirectory("video");
        _segmentListPath = Path.Combine(_sessionDirectory, "segments.csv");
        _segmentPattern = Path.Combine(_sessionDirectory, "segment_%08d.mp4");
    }

    public bool IsRunning => _isRunning;
    public long StartTimestamp { get; private set; }
    public DateTime RecordingStartTime { get; private set; }
    public Stopwatch RecordingStopwatch { get; } = new();

    public void StartRecording()
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            if (_isRunning) return;

            string ffmpegPath = FfmpegLocator.GetFfmpegPath();
            string forceKeyFrames = $"expr:gte(t,n_forced*{SegmentSeconds})";
            string encoderOptions = HardwareEncoderSelector.GetEncoderOptions(
                ffmpegPath,
                _fps,
                _bitrateKbps,
                SegmentSeconds);
            string arguments =
                $"-hide_banner -loglevel warning -y " +
                $"-use_wallclock_as_timestamps 1 " +
                $"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} " +
                $"-framerate {_fps} -i pipe:0 -an " +
                $"-vf format=nv12 {encoderOptions} " +
                $"-force_key_frames \"{forceKeyFrames}\" " +
                $"-f segment -segment_time {SegmentSeconds} -segment_time_delta 0.05 " +
                $"-reset_timestamps 1 -segment_format mp4 " +
                $"-segment_list \"{_segmentListPath}\" -segment_list_type csv " +
                $"-segment_list_size {_segmentListSize} \"{_segmentPattern}\"";

            _ffmpeg = new Process
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

            _ffmpeg.Start();
            _videoInput = _ffmpeg.StandardInput.BaseStream;
            _stderrTask = _ffmpeg.StandardError.ReadToEndAsync();
            StartTimestamp = Environment.TickCount64;
            RecordingStartTime = DateTime.Now;
            RecordingStopwatch.Restart();
            _latestSegmentEndTimestamp = StartTimestamp;
            _isRunning = true;

            AppLogger.Info(
                $"Persistent segmented encoder started | {_width}x{_height} | " +
                $"FPS={_fps} | Bitrate={_bitrateKbps}kbps | Segment={SegmentSeconds}s");
        }
    }

    public void WriteFrame(byte[] bgraData, int sourceWidth, int sourceHeight)
    {
        if (!_isRunning) return;

        lock (_writeLock)
        {
            if (!_isRunning || _videoInput is null || _ffmpeg is null) return;

            try
            {
                if (_ffmpeg.HasExited)
                {
                    string error = ReadEncoderError();
                    throw new InvalidOperationException($"Video encoder exited unexpectedly: {error}");
                }

                byte[] frame = EnsureFrameMatchesEncoderSize(bgraData, sourceWidth, sourceHeight);
                _videoInput.Write(frame, 0, frame.Length);
                RefreshSegmentsCore();
            }
            catch (Exception ex)
            {
                AppLogger.Error(nameof(VideoEncoder), $"WriteFrame failed: {ex}");
                StopCore();
            }
        }
    }

    public async Task<(string videoPath, long videoStartTimestamp, long videoElapsedMs)>
        SaveLastSecondsAsync(string outputFolder, int seconds)
    {
        if (!_isRunning) throw new InvalidOperationException("Video encoder is not running.");

        long requestedEnd = Environment.TickCount64;
        await WaitForSegmentCoverageAsync(requestedEnd, TimeSpan.FromSeconds(SegmentSeconds + 2));

        ReplaySegmentBuffer.Lease lease;
        lock (_writeLock)
        {
            RefreshSegmentsCore();
            long effectiveEnd = Math.Min(requestedEnd, _latestSegmentEndTimestamp);
            long from = Math.Max(StartTimestamp, effectiveEnd - Math.Max(1, seconds) * 1000L);
            lease = _segments.Acquire(from, effectiveEnd);
        }

        using (lease)
        {
            if (lease.Segments.Count == 0)
                throw new InvalidOperationException("Replay buffer does not contain finalized video segments yet.");

            string outputPath = OutputFileName.Create(outputFolder, "Replay", ".mp4");
            await ConcatenateSegmentsAsync(lease.Segments, outputPath);

            long actualStart = lease.Segments[0].StartTimestamp;
            long actualEnd = lease.Segments[^1].EndTimestamp;
            return (outputPath, actualStart, Math.Max(1, actualEnd - actualStart));
        }
    }

    private async Task WaitForSegmentCoverageAsync(long requestedEnd, TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            lock (_writeLock)
            {
                RefreshSegmentsCore();
                if (_latestSegmentEndTimestamp >= requestedEnd - 100) return;
                if (!_isRunning) break;
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
        string outputPath)
    {
        string listPath = Path.Combine(_sessionDirectory, $"export_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(
                listPath,
                segments.Select(segment => $"file '{segment.Path.Replace('\\', '/').Replace("'", "'\\''")}'"));

            string ffmpegPath = FfmpegLocator.GetFfmpegPath();
            string arguments =
                $"-hide_banner -loglevel warning -y -f concat -safe 0 -i \"{listPath}\" " +
                $"-map 0:v:0 -an -c:v copy -movflags +faststart \"{outputPath}\"";

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
            if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException($"Video export failed: {error}");
        }
        finally
        {
            TryDelete(listPath);
        }
    }

    private byte[] EnsureFrameMatchesEncoderSize(byte[] source, int sourceWidth, int sourceHeight)
    {
        int expectedSize = _width * _height * 4;
        if (sourceWidth == _width && sourceHeight == _height && source.Length == expectedSize) return source;

        int sourceSize = sourceWidth * sourceHeight * 4;
        if (source.Length != sourceSize)
            throw new ArgumentException($"Unexpected frame size. Expected {sourceSize}, got {source.Length}.", nameof(source));

        using var sourceBitmap = new Bitmap(sourceWidth, sourceHeight, PixelFormat.Format32bppArgb);
        BitmapData sourceData = sourceBitmap.LockBits(
            new Rectangle(0, 0, sourceWidth, sourceHeight),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try { Marshal.Copy(source, 0, sourceData.Scan0, source.Length); }
        finally { sourceBitmap.UnlockBits(sourceData); }

        using var scaledBitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(scaledBitmap))
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(
                sourceBitmap,
                new Rectangle(0, 0, _width, _height),
                new Rectangle(0, 0, sourceWidth, sourceHeight),
                GraphicsUnit.Pixel);
        }

        BitmapData scaledData = scaledBitmap.LockBits(
            new Rectangle(0, 0, _width, _height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            if (_scaledFrameBuffer is null || _scaledFrameBuffer.Length != expectedSize)
                _scaledFrameBuffer = new byte[expectedSize];
            Marshal.Copy(scaledData.Scan0, _scaledFrameBuffer, 0, expectedSize);
            return _scaledFrameBuffer;
        }
        finally
        {
            scaledBitmap.UnlockBits(scaledData);
        }
    }

    public void Stop()
    {
        lock (_writeLock)
        {
            StopCore();
        }
    }

    private void StopCore()
    {
        if (!_isRunning && _ffmpeg is null) return;
        _isRunning = false;
        RecordingStopwatch.Stop();

        try { _videoInput?.Flush(); } catch { }
        try { _videoInput?.Dispose(); } catch { }
        _videoInput = null;

        Process? process = _ffmpeg;
        _ffmpeg = null;
        if (process is not null)
        {
            try
            {
                if (!process.WaitForExit(5_000)) process.Kill(entireProcessTree: true);
            }
            catch { }

            string error = ReadEncoderError();
            if (!string.IsNullOrWhiteSpace(error))
                AppLogger.Debug($"Video encoder output: {error}");
            process.Dispose();
        }

        RefreshSegmentsCore();
        AppLogger.Info("Persistent segmented encoder stopped");
    }

    private string ReadEncoderError()
    {
        try
        {
            return _stderrTask?.IsCompleted == true ? _stderrTask.GetAwaiter().GetResult() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _segments.Dispose();
        TryDeleteDirectory(_sessionDirectory);
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
