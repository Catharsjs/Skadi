using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;
using System.IO;

namespace EventCapture.App.Services;

public sealed class CaptureCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim _pipelineLock = new(1, 1);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _continuousLock = new(1, 1);
    private IVideoCapturePipeline? _videoPipeline;
    private AudioRecorder? _audioRecorder;
    private AppSettings? _settings;

    public long CapturedFrames => (long)(_videoPipeline?.FramesCaptured ?? 0);
    public bool IsCapturingWindow =>
        _settings?.CaptureTarget.StartsWith("Window|", StringComparison.Ordinal) == true;
    public bool IsContinuousRecording { get; private set; }

    public async Task ApplySettingsAsync(AppSettings settings, bool restartPipeline)
    {
        AppLogger.Info($"Coordinator state | Action=ApplySettings enter | Restart={restartPipeline} | NewBuffer={settings.BufferEnabled} | NewMode={settings.CaptureMode} | NewTarget={settings.CaptureTarget} | Current={DescribeState()}");
        _settings = settings;
        _audioRecorder?.SetSystemVolume(settings.SystemAudioVolume);
        _audioRecorder?.SetMicrophoneVolume(settings.MicVolume);

        if (restartPipeline) await RestartPipelineAsync();
        AppLogger.Info($"Coordinator state | Action=ApplySettings exit | Restart={restartPipeline} | Current={DescribeState()}");
    }

    public async Task<string> SaveRecordAsync()
    {
        AppLogger.Info($"Coordinator state | Action=SaveReplay enter | Current={DescribeState()}");
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
                    throw new InvalidOperationException("Video capture pipeline is not running.");

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
                        if (!string.Equals(merged, saved.videoPath, StringComparison.OrdinalIgnoreCase))
                            TryDelete(saved.videoPath);
                        result = merged;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(result) || !File.Exists(result))
                throw new InvalidOperationException("The replay could not be exported.");
            AppLogger.Info($"Coordinator state | Action=SaveReplay exit | Result={Path.GetFileName(result)} | Current={DescribeState()}");
            return result;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task StartContinuousRecordingAsync()
    {
        AppLogger.Info($"Coordinator state | Action=StartRecording enter | Current={DescribeState()}");
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
                bool wantsVideo = _settings.CaptureMode != "Audio";

                if (_settings.BufferEnabled && wantsVideo && _videoPipeline is not null)
                {
                    AppLogger.Info($"Coordinator state | Action=StartRecording restart-buffer-pipeline | Current={DescribeState()}");
                    StopPipelineCore();
                    await Task.Run(() => StartPipelineCore(forceStart: true));
                }
                else if (_videoPipeline is null && _audioRecorder is null)
                {
                    await Task.Run(
                        () => StartPipelineCore(forceStart: true));
                }

                bool wantsAudio = _settings.CaptureMode != "Video" && (_settings.RecordSystemAudio || _settings.RecordMicrophone);

                if (wantsAudio)
                    (_audioRecorder ?? throw new InvalidOperationException("Audio pipeline is unavailable."))
                        .StartContinuousRecording();
                if (wantsVideo)
                    (_videoPipeline ?? throw new InvalidOperationException("Video pipeline is unavailable."))
                        .StartContinuousRecording();

                IsContinuousRecording = true;
                AppLogger.Info($"Continuous recording started | Mode={_settings.CaptureMode}");
                AppLogger.Info($"Coordinator state | Action=StartRecording exit | Current={DescribeState()}");
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
        AppLogger.Info($"Coordinator state | Action=StopRecording enter | Current={DescribeState()}");
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
                if (!string.Equals(result, video.VideoPath, StringComparison.OrdinalIgnoreCase))
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
            AppLogger.Info($"Coordinator state | Action=StopRecording media-saved | Result={Path.GetFileName(result)} | Current={DescribeState()}");

            if (!_settings.BufferEnabled)
            {
                await _pipelineLock.WaitAsync();
                try { StopPipelineCore(); }
                finally { _pipelineLock.Release(); }
            }

            return result;
        }
        catch
        {
            IsContinuousRecording = false;

            if (_settings is not null && !_settings.BufferEnabled)
            {
                await _pipelineLock.WaitAsync();
                try { StopPipelineCore(); }
                finally { _pipelineLock.Release(); }
            }

            throw;
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
            {
                AppLogger.Info($"Coordinator state | Action=RestartPipeline skipped-recording-active | Current={DescribeState()}");
                return;
            }

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
        AppLogger.Info($"Coordinator state | Action=StartPipelineCore enter | ForceStart={forceStart} | Current={DescribeState()}");
        if (_settings is null || (!forceStart && !_settings.BufferEnabled))
        {
            AppLogger.Info($"Coordinator state | Action=StartPipelineCore skipped | ForceStart={forceStart} | Current={DescribeState()}");
            return;
        }
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
            _videoPipeline = CreateVideoPipeline(
                _settings,
                effectiveFps,
                width,
                height,
                videoBitrate);
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
            $"Video pipeline started | Type={_videoPipeline?.GetType().Name} | Mode={_settings.CaptureMode} | " +
            $"Target={_settings.CaptureTarget} | FPS={effectiveFps} | Bitrate={videoBitrate}kbps");
        AppLogger.Info($"Coordinator state | Action=StartPipelineCore exit | WantsVideo={wantsVideo} | WantsAudio={wantsAudio} | SharedTimestamp={sharedTimestamp} | Current={DescribeState()}");
    }

    private static IVideoCapturePipeline CreateVideoPipeline(
        AppSettings settings,
        int fps,
        int width,
        int height,
        int bitrateKbps)
    {
        return new GpuCapturePipeline(
            fps,
            width,
            height,
            bitrateKbps,
            settings.BufferSeconds,
            settings.CaptureTarget,
            enableReplay: true);
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
        AppLogger.Info($"Coordinator state | Action=StopPipelineCore enter | Current={DescribeState()}");
        try { _audioRecorder?.Dispose(); } catch { }
        try { _videoPipeline?.Stop(); } catch { }
        try { _videoPipeline?.Dispose(); } catch { }
        IsContinuousRecording = false;
        _audioRecorder = null;
        _videoPipeline = null;
        AppLogger.Info($"Coordinator state | Action=StopPipelineCore exit | Current={DescribeState()}");
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

    private string DescribeState()
    {
        string settings = _settings is null
            ? "Settings=null"
            : $"Buffer={_settings.BufferEnabled}, Mode={_settings.CaptureMode}, Target={_settings.CaptureTarget}, Fps={_settings.Fps}, BufferSec={_settings.BufferSeconds}";
        string video = _videoPipeline is null
            ? "Video=null"
            : $"Video={_videoPipeline.GetType().Name}, Running={_videoPipeline.IsRunning}, Continuous={_videoPipeline.IsContinuousRecording}, Frames={_videoPipeline.FramesCaptured}";
        string audio = _audioRecorder is null ? "Audio=null" : "Audio=active";
        return $"Continuous={IsContinuousRecording}, {settings}, {video}, {audio}";
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
