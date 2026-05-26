using SharpDX.MediaFoundation;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EventCapture.Core.Diagnostics;
namespace EventCapture.Core.Capture;

// Кодує відео через Windows Media Foundation.
public class VideoEncoder : IDisposable
{
    private readonly Stopwatch _frameStopwatch = new();
    public readonly Stopwatch RecordingStopwatch = new();
    private readonly object _writeLock = new();
    private SinkWriter? _sinkWriter;
    private int _videoStreamIndex;
    private bool _isRunning;
    private string _currentTempPath = string.Empty;
    private readonly int _fps;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bitrate;
    private readonly long _frameDuration;

    public bool IsRunning => _isRunning;
    public long StartTimestamp { get; private set; }
    public DateTime RecordingStartTime { get; private set; }

    public VideoEncoder(int fps, int width, int height, int bitrate = 8000)
    {
        _fps = fps;
        _width = width;
        _height = height;
        _bitrate = bitrate * 1000;
        _frameDuration = 10_000_000 / fps;
    }

    // Ініціалізація Media Foundation encoder (...    
    public void StartRecording()
    {
        CleanupOldTempFiles();
        _currentTempPath = Path.Combine(Path.GetTempPath(), $"eventcapture_{Guid.NewGuid()}.mp4");
        Start(_currentTempPath);
    }

    private void Start(string outputPath)
    {
        if (_isRunning)
            return;

        using var attributes = new MediaAttributes(2);

        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1);

        _sinkWriter = MediaFactory.CreateSinkWriterFromURL(outputPath, null, attributes);

        ConfigureOutputMediaType();
        ConfigureInputMediaType();

        _sinkWriter.BeginWriting();
        _frameStopwatch.Restart();
        RecordingStopwatch.Restart();

        StartTimestamp = Environment.TickCount64;
        RecordingStartTime = DateTime.Now;
        _isRunning = true;
        AppLogger.Info($"Video encoder started {_width}x{_height} {_fps}fps");
    }

    private void ConfigureOutputMediaType()
    {
        using var mediaType = new MediaType();

        mediaType.Set(
            MediaTypeAttributeKeys.MajorType,
            MediaTypeGuids.Video);

        mediaType.Set(
            MediaTypeAttributeKeys.Subtype,
            VideoFormatGuids.H264);

        mediaType.Set(
            MediaTypeAttributeKeys.AvgBitrate,
            _bitrate);

        mediaType.Set(
            MediaTypeAttributeKeys.InterlaceMode,
            (int)VideoInterlaceMode.Progressive);

        mediaType.Set(
            MediaTypeAttributeKeys.FrameSize,
            ((long)_width << 32) | (uint)_height);

        mediaType.Set(
            MediaTypeAttributeKeys.FrameRate,
            ((long)_fps << 32) | 1);

        mediaType.Set(
            MediaTypeAttributeKeys.PixelAspectRatio,
            (1L << 32) | 1);

        _sinkWriter!.AddStream(mediaType, out _videoStreamIndex);
    }

    private void ConfigureInputMediaType()
    {
        using var mediaType = new MediaType();

        mediaType.Set(
            MediaTypeAttributeKeys.MajorType,
            MediaTypeGuids.Video);

        mediaType.Set(
            MediaTypeAttributeKeys.Subtype,
            VideoFormatGuids.Rgb32);

        mediaType.Set(
            MediaTypeAttributeKeys.InterlaceMode,
            (int)VideoInterlaceMode.Progressive);

        mediaType.Set(
            MediaTypeAttributeKeys.FrameSize,
            ((long)_width << 32) | (uint)_height);

        mediaType.Set(
            MediaTypeAttributeKeys.FrameRate,
            ((long)_fps << 32) | 1);

        mediaType.Set(
            MediaTypeAttributeKeys.PixelAspectRatio,
            (1L << 32) | 1);

        _sinkWriter!.SetInputMediaType(_videoStreamIndex, mediaType, null);
    }
    // ...) Ініціалізація Media Foundation encoder

    // Запис кадрів (...    
    public void WriteFrame(byte[] bgraData)
    {
        if (!_isRunning || _sinkWriter == null)
            return;

        lock (_writeLock)
        {
            try
            {
                using var buffer = MediaFactory.CreateMemoryBuffer(bgraData.Length);
                var dataPointer = buffer.Lock(out _, out _);

                CopyFrameData(bgraData, dataPointer);

                buffer.Unlock();
                buffer.CurrentLength = bgraData.Length;

                using var sample = MediaFactory.CreateSample();

                sample.AddBuffer(buffer);
                sample.SampleTime = GetCurrentSampleTime();
                sample.SampleDuration = _frameDuration;

                _sinkWriter.WriteSample(_videoStreamIndex, sample);
            }
            catch (Exception ex)
            {
                AppLogger.Error(nameof(VideoEncoder), $"WriteFrame error: {ex}");
            }
        }
    }

    private void CopyFrameData(byte[] source, IntPtr destination)
    {
        int stride = _width * 4;

        for (int y = 0; y < _height; y++)
        {
            int sourceRow = (_height - 1 - y) * stride;

            Marshal.Copy(
                source,
                sourceRow,
                destination + y * stride,
                stride);
        }
    }

    private long GetCurrentSampleTime()
    {
        return _frameStopwatch.ElapsedTicks * 10_000_000 / Stopwatch.Frequency;
    }
    // ...) Запис кадрів


    // Збереження replay-buffer (...    
    public async Task<(string videoPath, long videoStartTimestamp, long videoElapsedMs)>
          SaveLastSecondsAsync(
            string outputFolder,
            int seconds)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Video encoder is not running.");

        string tempPath = _currentTempPath;
        long capturedStartTimestamp = StartTimestamp;
        long capturedElapsedMs = RecordingStopwatch.ElapsedMilliseconds;

        Stop();
        await Task.Delay(500);

        if (!File.Exists(tempPath))
        {
            throw new FileNotFoundException($"Replay buffer file not found: {tempPath}");
        }

        Directory.CreateDirectory(outputFolder);

        string outputPath = Path.Combine(outputFolder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4");

        await TrimReplayBuffer(tempPath, outputPath, seconds);

        TryDeleteFile(tempPath);

        return (
            outputPath,
            capturedStartTimestamp,
            capturedElapsedMs);
    }

    private async Task TrimReplayBuffer(string inputPath, string outputPath, int seconds)
    {
        var ffmpegPath = FFMpegCore.GlobalFFOptions.GetFFMpegBinaryPath();

        string arguments =
            $"-y -sseof -{seconds} " +
            $"-i \"{inputPath}\" " +
            $"-c:v copy " +
            $"-avoid_negative_ts make_zero " +
            $"\"{outputPath}\"";

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

        string ffmpegOutput = await process.StandardError.ReadToEndAsync();

        await Task.Run(() => process.WaitForExit(30000));

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new Exception($"FFmpeg trim failed: {ffmpegOutput}");
        }
    }
    // ...) Збереження replay-buffer

    // Тимчасові файли (...    
    private static void CleanupOldTempFiles()
    {
        try
        {
            string tempDirectory = Path.GetTempPath();

            foreach (var directory in Directory.GetDirectories(
                         tempDirectory,
                         "eventcapture_*"))
            {
                TryDeleteDirectory(directory);
            }

            foreach (var file in Directory.GetFiles(
                         tempDirectory,
                         "eventcapture_*.mp4"))
            {
                TryDeleteFile(file);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(VideoEncoder), $"Temp cleanup error: {ex}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch {}
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch {}
    }
    // ...) Тимчасові файли

    // Завершення recording (...    
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;

        lock (_writeLock)
        {
            if (_sinkWriter != null)
            {
                try
                {
                    _sinkWriter.Finalize();
                }
                catch {}

                try
                {
                    _sinkWriter.Dispose();
                }
                catch {}

                _sinkWriter = null;
            }
        }

        _frameStopwatch.Stop();
        RecordingStopwatch.Stop();
        AppLogger.Info("Video encoder stopped");
    }

    public void Dispose()
    {
        Stop();
    }
    // ...) Завершення recording
}