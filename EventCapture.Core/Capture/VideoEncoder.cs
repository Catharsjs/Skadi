using SharpDX.MediaFoundation;
using System.Runtime.InteropServices;

namespace EventCapture.Core.Capture;

// Кодує відео через Windows Media Foundation (H.264)
// Записує в тимчасовий файл, при збереженні обрізає через FFmpeg
public class VideoEncoder : IDisposable
{
    // Stopwatch для реальних timestamps кадрів (замість лічильника)
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();
    private readonly int _fps;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bitrate;
    private string _currentTempPath = string.Empty;

    private SinkWriter? _sinkWriter;
    private int _videoStreamIndex;
    private long _frameDuration;
    private bool _isRunning;

    private readonly object _writeLock = new();

    public bool IsRunning => _isRunning;

    public VideoEncoder(int fps, int width, int height, int bitrate = 8000)
    {
        _fps = fps;
        _width = width;
        _height = height;
        _bitrate = bitrate * 1000;
        _frameDuration = 10_000_000 / fps; // одиниці 100 наносекунд (формат MF)
    }

    // ─── Ініціалізація SinkWriter з вихідним H.264 і вхідним RGB32 форматом ───
    public void Start(string outputPath)
    {
        if (_isRunning) return;

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
        _isRunning = true;
    }

    // ─── Запис кадру ──────────────────────────────────────────────────────
    // Перевертає рядки (bottom-up → top-down) і передає в SinkWriter
    public void WriteFrame(byte[] bgraData)
    {
        if (!_isRunning || _sinkWriter == null) return;

        lock (_writeLock)
        {
            try
            {
                var buffer = MediaFactory.CreateMemoryBuffer(bgraData.Length);
                var dataPtr = buffer.Lock(out _, out _);

                int stride = _width * 4;
                for (int y = 0; y < _height; y++)
                {
                    int srcRow = (_height - 1 - y) * stride;
                    Marshal.Copy(bgraData, srcRow, dataPtr + y * stride, stride);
                }

                buffer.Unlock();
                buffer.CurrentLength = bgraData.Length;

                var sample = MediaFactory.CreateSample();
                sample.AddBuffer(buffer);
                // Реальний timestamp на основі Stopwatch
                sample.SampleTime = _stopwatch.ElapsedTicks * 10_000_000 / System.Diagnostics.Stopwatch.Frequency;
                sample.SampleDuration = _frameDuration;

                _sinkWriter.WriteSample(_videoStreamIndex, sample);

                sample.Dispose();
                buffer.Dispose();
            }
            catch { }
        }
    }

    // ─── Збереження останніх N секунд через FFmpeg ────────────────────────
    // Зупиняє запис → обрізає файл через -sseof → перезапускає запис
    public async Task<string> SaveLastSecondsAsync(string outputFolder, int seconds)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Encoder is not running.");

        string tempPath = _currentTempPath;
        Stop();

        await Task.Delay(500);

        if (!File.Exists(tempPath))
            throw new FileNotFoundException($"Buffer file not found: {tempPath}");

        string outputPath = Path.Combine(outputFolder,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4");
        Directory.CreateDirectory(outputFolder);

        var ffmpegPath = FFMpegCore.GlobalFFOptions.GetFFMpegBinaryPath();
        var args = $"-y -sseof -{seconds} -i \"{tempPath}\" " +
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

        try { File.Delete(tempPath); } catch { }

        StartRecording();
        return outputPath;
    }

    // ─── Запуск нового циклу запису ───────────────────────────────────────
    public void StartRecording()
    {
        CleanupOldTempFiles();
        _currentTempPath = Path.Combine(Path.GetTempPath(), $"eventcapture_{Guid.NewGuid()}.mp4");
        Start(_currentTempPath);
    }

    // Видаляємо залишки попередніх сесій з %TEMP%
    private static void CleanupOldTempFiles()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            foreach (var dir in Directory.GetDirectories(tempDir, "eventcapture_*"))
                try { Directory.Delete(dir, true); } catch { }
            foreach (var file in Directory.GetFiles(tempDir, "eventcapture_*.mp4"))
                try { File.Delete(file); } catch { }
        }
        catch { }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        lock (_writeLock)
        {
            if (_sinkWriter != null)
            {
                try { _sinkWriter.Finalize(); } catch { }
                try { _sinkWriter.Dispose(); } catch { }
                _sinkWriter = null;
            }
        }
    }

    public void Dispose() => Stop();
}