using SharpDX.MediaFoundation;
using System.Runtime.InteropServices;

namespace EventCapture.Core.Capture;

// Кодує відео через Windows Media Foundation (H.264)
// Записує в тимчасовий файл, при збереженні обрізає через FFmpeg
public class VideoEncoder : IDisposable
{
    // Stopwatch для реальних timestamps кадрів
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    public readonly System.Diagnostics.Stopwatch RecordingStopwatch = new();

    public long StartTimestamp { get; private set; }

    private readonly int _fps;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bitrate;

    private string _currentTempPath = string.Empty;

    private SinkWriter? _sinkWriter;
    private int _videoStreamIndex;
    private readonly long _frameDuration;
    private bool _isRunning;

    private readonly object _writeLock = new();

    public bool IsRunning => _isRunning;
    public DateTime RecordingStartTime { get; private set; }

    public VideoEncoder(int fps, int width, int height, int bitrate = 8000)
    {
        _fps = fps;
        _width = width;
        _height = height;
        _bitrate = bitrate * 1000;
        _frameDuration = 10_000_000 / fps; // одиниці 100 наносекунд, формат Media Foundation
    }

    // ─── Ініціалізація SinkWriter з вихідним H.264 і вхідним RGB32 форматом ───
    public void Start(string outputPath)
    {
        if (_isRunning)
            return;

        using var attributes = new MediaAttributes(2);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1);

        _sinkWriter = MediaFactory.CreateSinkWriterFromURL(outputPath, null, attributes);

        using var outputMediaType = new MediaType();
        outputMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outputMediaType.Set(MediaTypeAttributeKeys.AvgBitrate, _bitrate);
        outputMediaType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
        outputMediaType.Set(MediaTypeAttributeKeys.FrameSize, ((long)_width << 32) | (uint)_height);
        outputMediaType.Set(MediaTypeAttributeKeys.FrameRate, ((long)_fps << 32) | 1);
        outputMediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1L << 32) | 1);

        _sinkWriter.AddStream(outputMediaType, out _videoStreamIndex);

        using var inputMediaType = new MediaType();
        inputMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        inputMediaType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
        inputMediaType.Set(MediaTypeAttributeKeys.FrameSize, ((long)_width << 32) | (uint)_height);
        inputMediaType.Set(MediaTypeAttributeKeys.FrameRate, ((long)_fps << 32) | 1);
        inputMediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1L << 32) | 1);

        _sinkWriter.SetInputMediaType(_videoStreamIndex, inputMediaType, null);
        _sinkWriter.BeginWriting();

        _stopwatch.Restart();
        RecordingStopwatch.Restart();

        StartTimestamp = Environment.TickCount64;
        RecordingStartTime = DateTime.Now;

        _isRunning = true;
    }

    // ─── Запис кадру ──────────────────────────────────────────────────────
    // Перевертає рядки bottom-up → top-down і передає в SinkWriter
    public void WriteFrame(byte[] bgraData)
    {
        if (!_isRunning || _sinkWriter == null)
            return;

        lock (_writeLock)
        {
            try
            {
                using var buffer = MediaFactory.CreateMemoryBuffer(bgraData.Length);
                var dataPtr = buffer.Lock(out _, out _);

                int stride = _width * 4;

                for (int y = 0; y < _height; y++)
                {
                    int srcRow = (_height - 1 - y) * stride;
                    Marshal.Copy(bgraData, srcRow, dataPtr + y * stride, stride);
                }

                buffer.Unlock();
                buffer.CurrentLength = bgraData.Length;

                using var sample = MediaFactory.CreateSample();
                sample.AddBuffer(buffer);

                // Реальний timestamp на основі Stopwatch
                sample.SampleTime =
                    _stopwatch.ElapsedTicks * 10_000_000 /
                    System.Diagnostics.Stopwatch.Frequency;

                sample.SampleDuration = _frameDuration;

                _sinkWriter.WriteSample(_videoStreamIndex, sample);
            }
            catch
            {
                // Не валимо програму через одиничний проблемний кадр.
            }
        }
    }

    // ─── Збереження останніх N секунд через FFmpeg ────────────────────────
    // ВАЖЛИВО:
    // Метод тільки зупиняє поточний буфер і створює video-файл.
    // Він НЕ запускає новий recording самостійно.
    // Новий старт encoder + audio має робити MainForm одночасно.
    public async Task<(string videoPath, long startTimestamp, long elapsedMs)> SaveLastSecondsAsync(
        string outputFolder,
        int seconds)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Encoder is not running.");

        string tempPath = _currentTempPath;

        long capturedStartTimestamp = StartTimestamp;
        long capturedElapsedMs = RecordingStopwatch.ElapsedMilliseconds;

        Stop();

        await Task.Delay(500);

        if (!File.Exists(tempPath))
            throw new FileNotFoundException($"Buffer file not found: {tempPath}");

        Directory.CreateDirectory(outputFolder);

        string outputPath = Path.Combine(
            outputFolder,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4");

        var ffmpegPath = FFMpegCore.GlobalFFOptions.GetFFMpegBinaryPath();

        // Залишаємо -c:v copy для швидкого збереження.
        // Через keyframe trim фактична duration може бути не рівно seconds,
        // тому AudioRecorder має синхронізуватися по реальній videoDuration.
        var args =
            $"-y -sseof -{seconds} -i \"{tempPath}\" " +
            $"-c:v copy -avoid_negative_ts make_zero \"{outputPath}\"";

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();

        string error = await process.StandardError.ReadToEndAsync();

        await Task.Run(() => process.WaitForExit(30000));

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new Exception($"FFmpeg trim failed: {error}");

        try
        {
            File.Delete(tempPath);
        }
        catch
        {
            // Якщо файл ще зайнятий системою, просто ігноруємо.
        }

        return (outputPath, capturedStartTimestamp, capturedElapsedMs);
    }

    // ─── Запуск нового циклу запису ───────────────────────────────────────
    public void StartRecording()
    {
        CleanupOldTempFiles();

        _currentTempPath = Path.Combine(
            Path.GetTempPath(),
            $"eventcapture_{Guid.NewGuid()}.mp4");

        Start(_currentTempPath);
    }

    // Видаляємо залишки попередніх сесій з %TEMP%
    private static void CleanupOldTempFiles()
    {
        try
        {
            var tempDir = Path.GetTempPath();

            foreach (var dir in Directory.GetDirectories(tempDir, "eventcapture_*"))
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch { }
            }

            foreach (var file in Directory.GetFiles(tempDir, "eventcapture_*.mp4"))
            {
                try
                {
                    File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

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
                catch { }

                try
                {
                    _sinkWriter.Dispose();
                }
                catch { }

                _sinkWriter = null;
            }
        }

        _stopwatch.Stop();
        RecordingStopwatch.Stop();
    }

    public void Dispose()
    {
        Stop();
    }
}