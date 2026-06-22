using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;
using System.IO;

namespace EventCapture.App.Services;

public sealed class CaptureCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim _pipelineLock = new(1, 1);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private VideoEncoder? _encoder;
    private ScreenCapturer? _capturer;
    private ScreenshotSaver? _screenshotSaver;
    private AudioRecorder? _audioRecorder;
    private AppSettings? _settings;

    public long CapturedFrames => _capturer?.FramesCaptured ?? 0;
    public bool IsCapturingWindow => _settings?.CaptureTarget.StartsWith("Window|", StringComparison.Ordinal) == true;

    public async Task ApplySettingsAsync(AppSettings settings, bool restartPipeline)
    {
        _settings = settings;
        _screenshotSaver = CreateScreenshotSaver(settings);
        _audioRecorder?.SetSystemVolume(settings.SystemAudioVolume);
        _audioRecorder?.SetMicrophoneVolume(settings.MicVolume);

        if (restartPipeline)
            await RestartPipelineAsync();
    }

    public Task<string> SaveScreenshotAsync()
    {
        if (_settings is null) throw new InvalidOperationException("Capture is not initialized.");
        _screenshotSaver ??= CreateScreenshotSaver(_settings);
        return Task.Run(() => _screenshotSaver.SaveScreenshot());
    }

    public async Task<string> SaveRecordAsync()
    {
        if (_settings is null || !_settings.BufferEnabled)
            throw new InvalidOperationException("Replay buffer is disabled.");
        if (!await _saveLock.WaitAsync(0))
            throw new InvalidOperationException("Record save is already in progress.");

        try
        {
            string? result;
            if (_settings.CaptureMode == "Audio")
            {
                if (_audioRecorder is null ||
                    (!_settings.RecordSystemAudio && !_settings.RecordMicrophone))
                    throw new InvalidOperationException("No audio source is enabled.");

                result = await _audioRecorder.SaveAudioLastSecondsAsMp3Async(
                    _settings.SaveFolder, _settings.BufferSeconds);
            }
            else
            {
                if (_encoder is null || !_encoder.IsRunning)
                    throw new InvalidOperationException("Video encoder is not running.");

                var saved = await _encoder.SaveLastSecondsAsync(
                    _settings.SaveFolder, _settings.BufferSeconds);
                result = saved.videoPath;

                if (_settings.CaptureMode != "Video" &&
                    _audioRecorder is not null &&
                    (_settings.RecordSystemAudio || _settings.RecordMicrophone))
                {
                    string? merged = await _audioRecorder.SaveLastSecondsAsync(
                        _settings.SaveFolder,
                        _settings.BufferSeconds,
                        saved.videoPath,
                        saved.videoElapsedMs,
                        saved.videoStartTimestamp);
                    if (!string.IsNullOrWhiteSpace(merged) && File.Exists(merged))
                    {
                        TryDelete(saved.videoPath);
                        result = merged;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(result) || !File.Exists(result))
                throw new InvalidOperationException("The capture could not be exported.");
            return result!;
        }
        finally
        {
            try { await RestartPipelineAsync(); }
            finally { _saveLock.Release(); }
        }
    }

    public async Task RestartPipelineAsync()
    {
        if (_settings is null) return;
        await _pipelineLock.WaitAsync();
        try
        {
            StopPipeline();
            if (!_settings.BufferEnabled) return;

            bool wantsVideo = _settings.CaptureMode != "Audio";
            bool wantsAudio = _settings.CaptureMode != "Video";
            long sharedTimestamp = Environment.TickCount64;

            if (wantsVideo)
            {
                var (width, height) = ResolveResolution(_settings);
                int bitrate = CalculateVideoBitrateKbps(
                    width, height, _settings.Fps, _settings.VideoQuality);
                _encoder = new VideoEncoder(_settings.Fps, width, height, bitrate);
                _capturer = new ScreenCapturer(
                    _encoder, _settings.Fps, _settings.CaptureTarget);
                _encoder.StartRecording();
                _capturer.Start();
                sharedTimestamp = _encoder.StartTimestamp;
            }

            if (wantsAudio)
            {
                _audioRecorder = new AudioRecorder();
                _audioRecorder.SetSystemVolume(_settings.SystemAudioVolume);
                _audioRecorder.SetMicrophoneVolume(_settings.MicVolume);
                _audioRecorder.StartRecording(
                    _settings.RecordSystemAudio,
                    _settings.SystemAudioDeviceId,
                    _settings.RecordMicrophone,
                    _settings.MicDeviceId,
                    sharedTimestamp);
            }

            AppLogger.Info(
                $"Pipeline started | Mode={_settings.CaptureMode} | " +
                $"Target={_settings.CaptureTarget} | FPS={_settings.Fps}");
        }
        catch (Exception ex)
        {
            StopPipeline();
            AppLogger.Error(nameof(CaptureCoordinator), $"Pipeline restart failed: {ex}");
            throw;
        }
        finally
        {
            _pipelineLock.Release();
        }
    }

    private ScreenshotSaver CreateScreenshotSaver(
    AppSettings settings)
    {
        if (settings.Resolution == "Native")
        {
            return new ScreenshotSaver(
                settings.SaveFolder,
                width: 0,
                height: 0,
                settings.CaptureTarget);
        }

        var (width, height) =
            ResolveResolution(settings);

        return new ScreenshotSaver(
            settings.SaveFolder,
            width,
            height,
            settings.CaptureTarget);
    }

    private static (int Width, int Height) ResolveResolution(AppSettings settings)
    {
        var native = ScreenCapturer.GetTargetSize(settings.CaptureTarget);
        return settings.Resolution switch
        {
            "720p" => (1280, 720),
            "1080p" => (1920, 1080),
            "1440p" => (2560, 1440),
            _ => native
        };
    }

    private static int CalculateVideoBitrateKbps(
        int width, int height, int fps, int quality)
    {
        long pixelRate = (long)width * height * fps;
        int baseRate = (int)Math.Clamp(pixelRate * 0.10 / 1000.0, 4_000, 50_000);
        double multiplier = quality switch { <= 50 => .50, <= 70 => .70, _ => .90 };
        return Math.Max(1_500, (int)Math.Round(baseRate * multiplier));
    }

    private void StopPipeline()
    {
        try { _audioRecorder?.Dispose(); } catch { }
        try { _capturer?.Stop(); } catch { }
        try { _capturer?.Dispose(); } catch { }
        try { _encoder?.Stop(); } catch { }
        try { _encoder?.Dispose(); } catch { }
        _audioRecorder = null;
        _capturer = null;
        _encoder = null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await _pipelineLock.WaitAsync();
        try { StopPipeline(); }
        finally
        {
            _pipelineLock.Release();
            _pipelineLock.Dispose();
            _saveLock.Dispose();
        }
    }
}
