using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;
using System.IO;

namespace EventCapture.App.Services;

public sealed class CaptureCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim _pipelineLock = new(1, 1);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _continuousLock = new(1, 1);
    private GpuCapturePipeline? _videoPipeline;
    private ScreenshotSaver? _screenshotSaver;
    private AudioRecorder? _audioRecorder;
    private AppSettings? _settings;

    public long CapturedFrames => (long)(_videoPipeline?.FramesCaptured ?? 0);
    public bool IsCapturingWindow =>
        _settings?.CaptureTarget.StartsWith("Window|", StringComparison.Ordinal) == true;
    public bool IsContinuousRecording { get; private set; }

    public async Task ApplySettingsAsync(AppSettings settings, bool restartPipeline)
    {
        _settings = settings;
        _screenshotSaver = CreateScreenshotSaver(settings);
        _audioRecorder?.SetSystemVolume(settings.SystemAudioVolume);
        _audioRecorder?.SetMicrophoneVolume(settings.MicVolume);

        if (restartPipeline) await RestartPipelineAsync();
    }

    public Task<string> SaveScreenshotAsync()
    {
        if (_settings is null)
            throw new InvalidOperationException("Capture is not initialized.");
        _screenshotSaver ??= CreateScreenshotSaver(_settings);
        return Task.Run(() => _screenshotSaver.SaveScreenshot());
    }

    public async Task<string> SaveRecordAsync()
    {
        if (_settings is null || !_settings.BufferEnabled)
            throw new InvalidOperationException("Replay buffer is disabled.");
        if (!await _saveLock.WaitAsync(0))
            throw new InvalidOperationException("Replay export is already in progress.");

        try
        {
            string? result;
            if (_settings.CaptureMode == "Audio")
            {
                if (_audioRecorder is null ||
                    (!_settings.RecordSystemAudio && !_settings.RecordMicrophone))
                    throw new InvalidOperationException("No audio source is enabled.");

                result = await _audioRecorder.SaveAudioLastSecondsAsMp3Async(
                    _settings.SaveFolder,
                    _settings.BufferSeconds);
            }
            else
            {
                if (_videoPipeline is null || !_videoPipeline.IsRunning)
                    throw new InvalidOperationException("GPU capture pipeline is not running.");

                var saved = await _videoPipeline.SaveLastSecondsAsync(
                    _settings.SaveFolder,
                    _settings.BufferSeconds);
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
                throw new InvalidOperationException("The replay could not be exported.");
            return result;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task StartContinuousRecordingAsync()
    {
        if (_settings is null)
            throw new InvalidOperationException("Capture is not initialized.");
        if (!await _continuousLock.WaitAsync(0))
            throw new InvalidOperationException("Recording state is already changing.");

        try
        {
            if (IsContinuousRecording)
                throw new InvalidOperationException("Continuous recording is already active.");

            await _pipelineLock.WaitAsync();
            try
            {
                if (_videoPipeline is null && _audioRecorder is null)
                {
                    await Task.Run(
                        () => StartPipelineCore(forceStart: true));
                }

                bool wantsVideo = _settings.CaptureMode != "Audio";
                bool wantsAudio = _settings.CaptureMode != "Video" &&
                    (_settings.RecordSystemAudio || _settings.RecordMicrophone);

                if (wantsAudio)
                    (_audioRecorder ?? throw new InvalidOperationException("Audio pipeline is unavailable."))
                        .StartContinuousRecording();
                if (wantsVideo)
                    (_videoPipeline ?? throw new InvalidOperationException("Video pipeline is unavailable."))
                        .StartContinuousRecording();

                IsContinuousRecording = true;
                AppLogger.Info($"Continuous recording started | Mode={_settings.CaptureMode}");
            }
            catch
            {
                if (!_settings.BufferEnabled) StopPipelineCore();
                throw;
            }
            finally
            {
                _pipelineLock.Release();
            }
        }
        finally
        {
            _continuousLock.Release();
        }
    }

    public async Task<string> StopContinuousRecordingAsync()
    {
        if (_settings is null)
            throw new InvalidOperationException("Capture is not initialized.");
        if (!await _continuousLock.WaitAsync(0))
            throw new InvalidOperationException("Recording state is already changing.");

        try
        {
            if (!IsContinuousRecording)
                throw new InvalidOperationException("Continuous recording is not active.");

            ContinuousVideoResult? video = null;
            (string? AudioPath, long StartTimestamp, long EndTimestamp)? audio = null;

            if (_settings.CaptureMode != "Audio" && _videoPipeline is not null)
                video = await _videoPipeline.StopContinuousRecordingAsync(_settings.SaveFolder);
            if (_settings.CaptureMode != "Video" && _audioRecorder is not null &&
                (_settings.RecordSystemAudio || _settings.RecordMicrophone))
                audio = await _audioRecorder.StopContinuousRecordingAsync();

            string result;
            if (video is not null && audio?.AudioPath is not null)
            {
                result = await AudioRecorder.MergeContinuousWithVideoAsync(
                    video.VideoPath,
                    audio.Value.AudioPath,
                    _settings.SaveFolder,
                    video.StartTimestamp,
                    video.EndTimestamp,
                    audio.Value.StartTimestamp);
                TryDelete(video.VideoPath);
                TryDelete(audio.Value.AudioPath);
            }
            else if (video is not null)
            {
                result = video.VideoPath;
            }
            else if (audio?.AudioPath is not null)
            {
                result = await AudioRecorder.EncodeContinuousAudioAsMp3Async(
                    audio.Value.AudioPath,
                    _settings.SaveFolder);
                TryDelete(audio.Value.AudioPath);
            }
            else
            {
                throw new InvalidOperationException("Continuous recording produced no media.");
            }

            IsContinuousRecording = false;
            AppLogger.Info($"Continuous recording saved | Path={result}");

            if (!_settings.BufferEnabled)
            {
                await _pipelineLock.WaitAsync();
                try { StopPipelineCore(); }
                finally { _pipelineLock.Release(); }
            }

            return result;
        }
        finally
        {
            _continuousLock.Release();
        }
    }

    public async Task RestartPipelineAsync()
    {
        if (_settings is null) return;
        await _pipelineLock.WaitAsync();
        try
        {
            if (IsContinuousRecording)
                throw new InvalidOperationException("Stop recording before changing capture settings.");
            StopPipelineCore();
            if (_settings.BufferEnabled)
            {
                await Task.Run(
                    () => StartPipelineCore(forceStart: false));
            }
        }
        catch (Exception ex)
        {
            StopPipelineCore();
            AppLogger.Error(nameof(CaptureCoordinator), $"Pipeline restart failed: {ex}");
            throw;
        }
        finally
        {
            _pipelineLock.Release();
        }
    }

    private void StartPipelineCore(bool forceStart)
    {
        if (_settings is null || (!forceStart && !_settings.BufferEnabled)) return;
        bool wantsVideo = _settings.CaptureMode != "Audio";
        bool wantsAudio = _settings.CaptureMode != "Video";
        long sharedTimestamp = Environment.TickCount64;
        int effectiveFps = _settings.Fps;
        int videoBitrate = 0;

        if (wantsVideo)
        {
            var (width, height) = ResolveResolution(_settings);
            effectiveFps = ResolveEffectiveFrameRate(_settings);
            videoBitrate = CalculateVideoBitrateKbps(
                width,
                height,
                effectiveFps,
                _settings.VideoQuality);
            _videoPipeline = new GpuCapturePipeline(
                effectiveFps,
                width,
                height,
                videoBitrate,
                _settings.BufferSeconds,
                _settings.CaptureTarget);
            _videoPipeline.Start();
            sharedTimestamp = _videoPipeline.StartTimestamp;
        }

        if (wantsAudio)
        {
            _audioRecorder = new AudioRecorder(_settings.BufferSeconds);
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
            $"Native GPU pipeline started | Mode={_settings.CaptureMode} | " +
            $"Target={_settings.CaptureTarget} | FPS={effectiveFps} | Bitrate={videoBitrate}kbps");
    }

    private ScreenshotSaver CreateScreenshotSaver(AppSettings settings)
    {
        if (settings.Resolution == "Native")
            return new ScreenshotSaver(settings.SaveFolder, 0, 0, settings.CaptureTarget);
        var (width, height) = ResolveResolution(settings);
        return new ScreenshotSaver(settings.SaveFolder, width, height, settings.CaptureTarget);
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

    private static int ResolveEffectiveFrameRate(AppSettings settings)
    {
        int requestedFps = Math.Clamp(settings.Fps, 1, 60);
        int refreshRate = Math.Clamp(
            ScreenCapturer.GetTargetRefreshRate(settings.CaptureTarget),
            1,
            240);

        int effectiveFps = Math.Min(requestedFps, refreshRate);

        if (effectiveFps != requestedFps)
        {
            AppLogger.Info(
                $"Frame rate clamped | Requested={requestedFps} | " +
                $"RefreshRate={refreshRate} | Effective={effectiveFps}");
        }

        return effectiveFps;
    }

    private static int CalculateVideoBitrateKbps(
        int width,
        int height,
        int fps,
        int quality)
    {
        const double referencePixels = 1920.0 * 1080.0;
        double resolutionScale = Math.Max(0.5, (width * height) / referencePixels);
        double fpsScale = Math.Sqrt(Math.Clamp(fps, 1, 240) / 60.0);

        int referenceKbps = quality switch
        {
            <= 50 => 8_000,
            <= 70 => 14_000,
            _ => 22_000
        };

        int bitrate = (int)Math.Round(referenceKbps * resolutionScale * fpsScale);
        int cap = quality switch
        {
            <= 50 => 45_000,
            <= 70 => 70_000,
            _ => 95_000
        };

        return Math.Clamp(bitrate, 4_000, cap);
    }

    private void StopPipelineCore()
    {
        try { _audioRecorder?.Dispose(); } catch { }
        try { _videoPipeline?.Stop(); } catch { }
        try { _videoPipeline?.Dispose(); } catch { }
        IsContinuousRecording = false;
        _audioRecorder = null;
        _videoPipeline = null;
    }

    public async Task StopAllAsync()
    {
        bool lockTaken = false;

        try
        {
            lockTaken = await _pipelineLock.WaitAsync(TimeSpan.FromSeconds(3));
            StopPipelineCore();
        }
        finally
        {
            if (lockTaken)
                _pipelineLock.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
        _pipelineLock.Dispose();
        _saveLock.Dispose();
        _continuousLock.Dispose();
    }
}
