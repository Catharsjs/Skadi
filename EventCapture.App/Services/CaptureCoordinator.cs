using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;
using System.IO;

namespace EventCapture.App.Services;

public sealed class CaptureCoordinator : IAsyncDisposable
{
    private const long MinimumRecordingFreeDiskBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan RecordingDiskMonitorInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _pipelineLock = new(1, 1);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _continuousLock = new(1, 1);
    private IVideoCapturePipeline? _videoPipeline;
    private AudioRecorder? _audioRecorder;
    private AppSettings? _settings;
    private CancellationTokenSource? _recordingDiskMonitorCts;
    private Task? _recordingDiskMonitorTask;
    private bool _continuousNativeCombined;

    public event EventHandler<ContinuousRecordingStoppedEventArgs>? ContinuousRecordingStopped;
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

            EnsureSufficientRecordingDiskSpace(_settings, "StartRecording");

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

                _continuousNativeCombined = false;
                if (_settings.CaptureMode == "VideoAudio")
                {
                    if (!wantsVideo || !wantsAudio || _videoPipeline is not GpuCapturePipeline gpuPipeline || _audioRecorder is null)
                        throw new InvalidOperationException("Native combined recording is unavailable.");

                    gpuPipeline.StartContinuousRecording(
                        _audioRecorder.SystemFormat,
                        _audioRecorder.MicrophoneFormat);
                    _audioRecorder.StartContinuousNativeStreaming(gpuPipeline);
                    _continuousNativeCombined = true;
                }
                else
                {
                    if (wantsAudio)
                        (_audioRecorder ?? throw new InvalidOperationException("Audio pipeline is unavailable."))
                            .StartContinuousRecording();
                    if (wantsVideo)
                        (_videoPipeline ?? throw new InvalidOperationException("Video pipeline is unavailable."))
                            .StartContinuousRecording();
                }

                IsContinuousRecording = true;
                StartRecordingDiskSpaceMonitor();
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

    public Task<string> StopContinuousRecordingAsync() =>
        StopContinuousRecordingCoreAsync(stopDiskMonitor: true);

    private async Task<string> StopContinuousRecordingCoreAsync(bool stopDiskMonitor)
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

            if (_settings.CaptureMode == "VideoAudio")
            {
                if (!_continuousNativeCombined || _audioRecorder is null)
                    throw new InvalidOperationException("Native combined recording is not active.");

                _audioRecorder.StopContinuousNativeStreaming();
                audio = null;
            }

            if (_settings.CaptureMode != "Audio" && _videoPipeline is not null)
                video = await _videoPipeline.StopContinuousRecordingAsync(_settings.SaveFolder);
            if (_settings.CaptureMode != "VideoAudio" && _settings.CaptureMode != "Video" && _audioRecorder is not null &&
                (_settings.RecordSystemAudio || _settings.RecordMicrophone))
                audio = await _audioRecorder.StopContinuousRecordingAsync();

            string result;
            if (video is not null)
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
            _continuousNativeCombined = false;
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
            _continuousNativeCombined = false;

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
            if (stopDiskMonitor)
                StopRecordingDiskSpaceMonitor();
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
            AppLogger.Info($"Coordinator state | Action=StartPipelineCore video-resolve-begin | Target={_settings.CaptureTarget} | Resolution={_settings.Resolution} | Current={DescribeState()}");
            var (width, height) = ResolveResolution(_settings);
            AppLogger.Info($"Coordinator state | Action=StartPipelineCore video-resolve-end | Width={width} | Height={height} | Target={_settings.CaptureTarget}");

            AppLogger.Info($"Coordinator state | Action=StartPipelineCore fps-resolve-begin | RequestedFps={_settings.Fps} | Target={_settings.CaptureTarget}");
            effectiveFps = ResolveEffectiveFrameRate(_settings);
            AppLogger.Info($"Coordinator state | Action=StartPipelineCore fps-resolve-end | EffectiveFps={effectiveFps}");

            videoBitrate = CalculateVideoBitrateKbps(
                width,
                height,
                effectiveFps,
                _settings.VideoQuality);
            AppLogger.Info($"Coordinator state | Action=StartPipelineCore video-create-begin | Output={width}x{height} | Fps={effectiveFps} | Bitrate={videoBitrate}kbps | BufferSec={_settings.BufferSeconds} | Buffer={_settings.BufferEnabled} | Mode={_settings.CaptureMode}");
            _videoPipeline = CreateVideoPipeline(
                _settings,
                effectiveFps,
                width,
                height,
                videoBitrate);
            AppLogger.Info($"Coordinator state | Action=StartPipelineCore video-create-end | Type={_videoPipeline.GetType().Name}");

            AppLogger.Info($"Coordinator state | Action=StartPipelineCore video-start-begin | Type={_videoPipeline.GetType().Name}");
            _videoPipeline.Start();
            AppLogger.Info($"Coordinator state | Action=StartPipelineCore video-start-end | StartTimestamp={_videoPipeline.StartTimestamp} | Frames={_videoPipeline.FramesCaptured}");
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

    private void StartRecordingDiskSpaceMonitor()
    {
        StopRecordingDiskSpaceMonitor();
        if (_settings is null) return;

        _recordingDiskMonitorCts = new CancellationTokenSource();
        CancellationToken token = _recordingDiskMonitorCts.Token;
        _recordingDiskMonitorTask = Task.Run(() => MonitorRecordingDiskSpaceAsync(token), token);
    }

    private void StopRecordingDiskSpaceMonitor()
    {
        try { _recordingDiskMonitorCts?.Cancel(); } catch { }
        _recordingDiskMonitorCts?.Dispose();
        _recordingDiskMonitorCts = null;
        _recordingDiskMonitorTask = null;
    }

    private async Task MonitorRecordingDiskSpaceAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(RecordingDiskMonitorInterval, token);
                if (token.IsCancellationRequested || !IsContinuousRecording || _settings is null)
                    continue;

                if (HasSufficientRecordingDiskSpace(_settings, out string detail))
                    continue;

                AppLogger.Info($"Recording stopped, disk is full | {detail}");
                string? path = null;
                Exception? failure = null;

                try
                {
                    path = await StopContinuousRecordingCoreAsync(stopDiskMonitor: false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    AppLogger.Error(nameof(CaptureCoordinator), $"Forced recording stop failed after low disk detection: {ex}");
                    IsContinuousRecording = false;
                    if (_settings is not null && !_settings.BufferEnabled)
                    {
                        await _pipelineLock.WaitAsync(token);
                        try { StopPipelineCore(); }
                        finally { _pipelineLock.Release(); }
                    }
                }
                finally
                {
                    if (_settings is not null)
                        CleanupRecordingTempFiles(_settings.SaveFolder);
                    ContinuousRecordingStopped?.Invoke(
                        this,
                        new ContinuousRecordingStoppedEventArgs(
                            Forced: true,
                            Message: "Recording stopped, disk is full",
                            Path: path,
                            Error: failure));
                    StopRecordingDiskSpaceMonitor();
                }

                break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(CaptureCoordinator), $"Disk space monitor failed: {ex}");
        }
    }

    private static void EnsureSufficientRecordingDiskSpace(AppSettings settings, string action)
    {
        if (HasSufficientRecordingDiskSpace(settings, out string detail))
            return;

        AppLogger.Info($"Disk is full | Action={action} | {detail}");
        throw new InvalidOperationException("Disk is full");
    }

    private static bool HasSufficientRecordingDiskSpace(AppSettings settings, out string detail)
    {
        List<string> low = [];
        foreach (string path in GetRecordingStoragePaths(settings))
        {
            try
            {
                Directory.CreateDirectory(path);
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrWhiteSpace(root))
                    continue;

                var drive = new DriveInfo(root);
                long available = drive.AvailableFreeSpace;
                if (available < MinimumRecordingFreeDiskBytes)
                {
                    low.Add($"Path={path} | Drive={drive.Name} | AvailableBytes={available} | RequiredBytes={MinimumRecordingFreeDiskBytes}");
                }
            }
            catch (Exception ex)
            {
                low.Add($"Path={path} | Error={ex.Message} | RequiredBytes={MinimumRecordingFreeDiskBytes}");
            }
        }

        detail = low.Count == 0
            ? "Disk space is sufficient"
            : string.Join("; ", low);
        return low.Count == 0;
    }

    private static IEnumerable<string> GetRecordingStoragePaths(AppSettings settings)
    {
        yield return settings.SaveFolder;
        yield return Path.GetTempPath();
    }

    private static void CleanupRecordingTempFiles(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (string path in Directory.EnumerateFiles(folder, ".record-merge-*.tmp.mp4"))
                TryDelete(path);
        }
        catch { }
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
        int normalizedFps = Math.Clamp(fps, 1, 60);

        int bitrate30;
        int bitrate60;

        if (height <= 720)
        {
            (bitrate30, bitrate60) = quality switch
            {
                <= 50 => (6_000, 8_000),
                <= 70 => (10_000, 14_000),
                _ => (16_000, 22_000)
            };
        }
        else if (height <= 1080)
        {
            (bitrate30, bitrate60) = quality switch
            {
                <= 50 => (8_000, 12_000),
                <= 70 => (14_000, 20_000),
                _ => (22_000, 32_000)
            };
        }
        else if (height <= 1440)
        {
            (bitrate30, bitrate60) = quality switch
            {
                <= 50 => (16_000, 24_000),
                <= 70 => (28_000, 40_000),
                _ => (44_000, 64_000)
            };
        }
        else
        {
            (bitrate30, bitrate60) = quality switch
            {
                <= 50 => (35_000, 50_000),
                <= 70 => (60_000, 85_000),
                _ => (95_000, 130_000)
            };
        }

        if (normalizedFps <= 30)
            return bitrate30;

        double fpsScale = (normalizedFps - 30) / 30.0;
        return (int)Math.Round(bitrate30 + ((bitrate60 - bitrate30) * fpsScale));
    }

    private void StopPipelineCore()
    {
        AppLogger.Info($"Coordinator state | Action=StopPipelineCore enter | Current={DescribeState()}");
        try { _audioRecorder?.Dispose(); } catch { }
        try { _videoPipeline?.Stop(); } catch { }
        try { _videoPipeline?.Dispose(); } catch { }
        StopRecordingDiskSpaceMonitor();
        IsContinuousRecording = false;
        _continuousNativeCombined = false;
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
        StopRecordingDiskSpaceMonitor();
        _pipelineLock.Dispose();
        _saveLock.Dispose();
        _continuousLock.Dispose();
    }
}

public sealed record ContinuousRecordingStoppedEventArgs(
    bool Forced,
    string Message,
    string? Path,
    Exception? Error);
