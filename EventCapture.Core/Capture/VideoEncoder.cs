using EventCapture.Core.Diagnostics;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.MediaFoundation;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EventCapture.Core.Capture;

public class VideoEncoder : IDisposable
{
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();
    private readonly int _fps;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bitrate;

    private SinkWriter? _sinkWriter;
    private int _videoStreamIndex;
    private long _frameDuration;
    private long _currentTime;
    private bool _isRunning;

    private readonly object _writeLock = new();

    public bool IsRunning => _isRunning;

    public VideoEncoder(int fps, int width, int height, int bitrate = 8000)
    {
        _fps = fps;
        _width = width;
        _height = height;
        _bitrate = bitrate * 1000;
        _frameDuration = 10_000_000 / fps; // 100-nanosecond units
    }

    public void Start(string outputPath)
    {
        if (_isRunning) return;

        MediaFactory.Startup(MediaFactory.Version, 0);

        // Створюємо атрибути для SinkWriter
        using var attributes = new MediaAttributes(2);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1);

        _sinkWriter = MediaFactory.CreateSinkWriterFromURL(outputPath, null, attributes);

        // Налаштовуємо вихідний формат H.264
        using var outputMediaType = new MediaType();
        outputMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outputMediaType.Set(MediaTypeAttributeKeys.AvgBitrate, _bitrate);
        outputMediaType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
        outputMediaType.Set(MediaTypeAttributeKeys.FrameSize,
            ((long)_width << 32) | (uint)_height);
        outputMediaType.Set(MediaTypeAttributeKeys.FrameRate,
            ((long)_fps << 32) | 1);
        outputMediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio,
            (1L << 32) | 1);

        _sinkWriter.AddStream(outputMediaType, out _videoStreamIndex);

        // Налаштовуємо вхідний формат BGRA
        using var inputMediaType = new MediaType();
        inputMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        inputMediaType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
        inputMediaType.Set(MediaTypeAttributeKeys.FrameSize,
            ((long)_width << 32) | (uint)_height);
        inputMediaType.Set(MediaTypeAttributeKeys.FrameRate,
            ((long)_fps << 32) | 1);
        inputMediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio,
            (1L << 32) | 1);

        _sinkWriter.SetInputMediaType(_videoStreamIndex, inputMediaType, null);
        _sinkWriter.BeginWriting();

        _currentTime = 0;
        _stopwatch.Restart();
        _isRunning = true;

        AppLogger.Log($"VideoEncoder started (MediaFoundation): {_width}x{_height} {_fps}fps");
    }

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
                sample.SampleTime = _stopwatch.ElapsedTicks *
                    10_000_000 / System.Diagnostics.Stopwatch.Frequency;
                sample.SampleDuration = _frameDuration;

                _sinkWriter.WriteSample(_videoStreamIndex, sample);

                sample.Dispose();
                buffer.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("WriteFrame", ex.Message);
            }
        }
    }

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
        {
            AppLogger.LogError("SaveVideo", error);
            throw new Exception($"FFmpeg trim failed: {error}");
        }

        try { File.Delete(tempPath); } catch { }
        AppLogger.LogResult($"Video saved: {outputPath}");

        StartRecording();
        return outputPath;
    }

    private string _currentTempPath = string.Empty;

    public void StartRecording()
    {
        _currentTempPath = Path.Combine(Path.GetTempPath(),
            $"eventcapture_{Guid.NewGuid()}.mp4");
        Start(_currentTempPath);
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        lock (_writeLock)
        {
            try
            {
                _sinkWriter?.Finalize();
                _sinkWriter?.Dispose();
                _sinkWriter = null;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Stop", ex.Message);
            }
        }

        AppLogger.Log("VideoEncoder stopped");
    }

    public void Dispose()
    {
        Stop();
        try
        {
            if (File.Exists(_currentTempPath))
                File.Delete(_currentTempPath);
        }
        catch { }
        MediaFactory.Shutdown();
    }
}