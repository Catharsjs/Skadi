using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace EventCapture.App.Services;

public sealed class CaptureCoordinator : IAsyncDisposable
{
    private const long MinimumRecordingFreeDiskBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan RecordingDiskMonitorInterval = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _pipelineLock = new(1, 1);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _continuousLock = new(1, 1);
    private IVideoCapturePipeline? _videoPipeline;
    private AudioRecorder? _audioRecorder;
    private AppSettings? _settings;
    private CancellationTokenSource? _recordingDiskMonitorCts;
    private Task? _recordingDiskMonitorTask;
    private CancellationTokenSource? _recordingPerformanceCts;
    private Task? _recordingPerformanceTask;
    private bool _continuousNativeCombined;
    private StreamingMp3Writer? _continuousMp3Writer;

    public event EventHandler? ContinuousRecordingStopping;
    public event EventHandler<ContinuousRecordingStoppedEventArgs>? ContinuousRecordingStopped;
    public long CapturedFrames => (long)(_videoPipeline?.FramesCaptured ?? 0);
    public bool IsCapturingWindow => false;
    public bool IsContinuousRecording { get; private set; }

    public async Task ApplySettingsAsync(AppSettings settings, bool restartPipeline)
    {
        if (settings.CaptureTarget.StartsWith("Window|", StringComparison.Ordinal))
        {
            settings.CaptureTarget = "PrimaryMonitor";
        }
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

            GpuCapturePipeline.RecoverInterruptedRecordings(_settings.SaveFolder);
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
                    if (!wantsVideo || _videoPipeline is not GpuCapturePipeline gpuPipeline)
                        throw new InvalidOperationException("Native combined recording is unavailable.");

                    if (wantsAudio)
                    {
                        if (_audioRecorder is null)
                            throw new InvalidOperationException("Native combined recording is unavailable.");

                        gpuPipeline.StartContinuousRecording(
                            _settings.SaveFolder,
                            AudioRecorder.NativeContinuousMixFormat);
                        _audioRecorder.StartContinuousNativeStreaming(gpuPipeline);
                        _continuousNativeCombined = true;
                    }
                    else
                    {
                        AppLogger.Info("Coordinator state | Action=StartRecording combined-video-only | AudioSources=disabled");
                        gpuPipeline.StartContinuousRecording(_settings.SaveFolder);
                    }
                }
                else
                {
                    if (wantsAudio)
                    {
                        if (_settings.CaptureMode != "Audio")
                            throw new InvalidOperationException("Continuous audio mode is unavailable.");

                        var writer = StreamingMp3Writer.Start(
                            _settings.SaveFolder,
                            AudioRecorder.NativeContinuousMixFormat);
                        try
                        {
                            (_audioRecorder ?? throw new InvalidOperationException("Audio pipeline is unavailable."))
                                .StartContinuousNativeStreaming(writer);
                            _continuousMp3Writer = writer;
                        }
                        catch
                        {
                            writer.Dispose();
                            throw;
                        }
                    }
                    if (wantsVideo)
                        (_videoPipeline ?? throw new InvalidOperationException("Video pipeline is unavailable."))
                            .StartContinuousRecording(_settings.SaveFolder);
                }

                IsContinuousRecording = true;
                StartRecordingDiskSpaceMonitor();
                StartRecordingPerformanceMonitor();
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

    public async Task<string?> HandleDisplayTopologyChangedAsync()
    {
        AppLogger.Info($"Reload (Targets updated) | Action=DisplayTopologyChanged | Current={DescribeState()}");

        string? finalizedRecordingPath = null;
        Exception? finalizeError = null;
        if (IsContinuousRecording)
        {
            try
            {
                finalizedRecordingPath = await StopContinuousRecordingCoreAsync(stopDiskMonitor: true);
                AppLogger.Info(
                    $"Reload (Targets updated) | Recording finalized | " +
                    $"Path={Path.GetFileName(finalizedRecordingPath)}");
            }
            catch (Exception ex)
            {
                finalizeError = ex;
                AppLogger.Error(
                    nameof(CaptureCoordinator),
                    $"Reload (Targets updated) | Recording finalization failed: {ex}");
            }
        }

        await _pipelineLock.WaitAsync();
        try
        {
            StopPipelineCore();
            AppLogger.Info(
                $"Reload (Targets updated) | Previous capture pipeline stopped | " +
                $"Finalized={finalizedRecordingPath is not null} | Current={DescribeState()}");
        }
        finally
        {
            _pipelineLock.Release();
        }

        if (finalizeError is not null)
        {
            throw new InvalidOperationException(
                "Recording could not be safely finalized after the display topology changed.",
                finalizeError);
        }

        return finalizedRecordingPath;
    }

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
            string? audioPath = null;

            if (_settings.CaptureMode == "VideoAudio")
            {
                if (_continuousNativeCombined)
                {
                    if (_audioRecorder is null)
                        throw new InvalidOperationException("Native combined recording is not active.");

                    _audioRecorder.StopContinuousNativeStreaming();
                }
            }

            if (_settings.CaptureMode != "Audio" && _videoPipeline is not null)
                video = await _videoPipeline.StopContinuousRecordingAsync(_settings.SaveFolder);
            if (_settings.CaptureMode == "Audio")
            {
                StreamingMp3Writer writer = _continuousMp3Writer ??
                    throw new InvalidOperationException("Streaming MP3 recording is not active.");
                _continuousMp3Writer = null;
                try
                {
                    (_audioRecorder ?? throw new InvalidOperationException("Audio pipeline is unavailable."))
                        .StopContinuousNativeStreaming();
                    audioPath = await writer.CompleteAsync();
                }
                finally
                {
                    writer.Dispose();
                }
            }

            string result;
            if (video is not null)
            {
                result = video.VideoPath;
            }
            else if (audioPath is not null)
            {
                result = audioPath;
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
            StopRecordingPerformanceMonitor();
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
            int requestedVideoBitrate = videoBitrate;
            videoBitrate = ApplyDesktopDuplicationBitrateLimit(
                _settings,
                width,
                height,
                effectiveFps,
                videoBitrate);
            if (videoBitrate != requestedVideoBitrate)
            {
                AppLogger.Info(
                    $"Desktop Duplication bitrate limited | Requested={requestedVideoBitrate}kbps | " +
                    $"Effective={videoBitrate}kbps | Output={width}x{height} | Fps={effectiveFps} | Quality={_settings.VideoQuality}");
            }
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
                ContinuousRecordingStopping?.Invoke(this, EventArgs.Empty);
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
                    StopRecordingPerformanceMonitor();
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
    }

    private static void CleanupRecordingTempFiles(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (string path in Directory.EnumerateFiles(folder, ".record-merge-*.tmp.mp4"))
                TryDelete(path);
            foreach (string path in Directory.EnumerateFiles(folder, ".audio-recording-*.tmp.mp3"))
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

    private static int ApplyDesktopDuplicationBitrateLimit(
        AppSettings settings,
        int width,
        int height,
        int fps,
        int bitrateKbps)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return bitrateKbps;
        if (settings.CaptureTarget != "PrimaryMonitor" && settings.CaptureTarget != "AllMonitors")
            return bitrateKbps;

        int pixels = Math.Max(1, width * height);
        double megapixels = pixels / 1_000_000.0;
        double fpsScale = Math.Clamp(fps, 1, 60) / 60.0;
        double qualityScale = settings.VideoQuality switch
        {
            <= 50 => 0.75,
            <= 70 => 1.0,
            _ => 1.25
        };

        int ddaLimit = (int)Math.Round(6_000 * megapixels * fpsScale * qualityScale);
        ddaLimit = Math.Clamp(ddaLimit, 6_000, 18_000);
        return Math.Min(bitrateKbps, ddaLimit);
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


    private void StartRecordingPerformanceMonitor()
    {
        StopRecordingPerformanceMonitor();
        _recordingPerformanceCts = new CancellationTokenSource();
        CancellationToken token = _recordingPerformanceCts.Token;
        _recordingPerformanceTask = Task.Run(() => MonitorRecordingPerformanceAsync(token), token);
    }

    private void StopRecordingPerformanceMonitor()
    {
        try { _recordingPerformanceCts?.Cancel(); } catch { }
        _recordingPerformanceCts?.Dispose();
        _recordingPerformanceCts = null;
        _recordingPerformanceTask = null;
    }

    private async Task MonitorRecordingPerformanceAsync(CancellationToken token)
    {
        ulong lastCaptured = 0;
        ulong lastEncoded = 0;
        ulong lastDropped = 0;
        long lastTicks = Stopwatch.GetTimestamp();
        Process process = Process.GetCurrentProcess();
        TimeSpan lastCpu = process.TotalProcessorTime;

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                if (token.IsCancellationRequested || !IsContinuousRecording)
                    continue;

                long nowTicks = Stopwatch.GetTimestamp();
                double elapsedSeconds = Math.Max(0.001, (nowTicks - lastTicks) / (double)Stopwatch.Frequency);
                lastTicks = nowTicks;

                process.Refresh();
                TimeSpan cpu = process.TotalProcessorTime;
                double cpuPercent = Math.Max(0, (cpu - lastCpu).TotalMilliseconds / (elapsedSeconds * Environment.ProcessorCount * 1000.0) * 100.0);
                lastCpu = cpu;

                string videoDetail;
                if (_videoPipeline is GpuCapturePipeline gpu)
                {
                    GpuCaptureStats stats = gpu.GetStats();
                    ulong capturedDelta = stats.CapturedFrames >= lastCaptured ? stats.CapturedFrames - lastCaptured : 0;
                    ulong encodedDelta = stats.EncodedFrames >= lastEncoded ? stats.EncodedFrames - lastEncoded : 0;
                    ulong droppedDelta = stats.DroppedFrames >= lastDropped ? stats.DroppedFrames - lastDropped : 0;
                    double capturedFps = capturedDelta / elapsedSeconds;
                    double encodedFps = encodedDelta / elapsedSeconds;
                    lastCaptured = stats.CapturedFrames;
                    lastEncoded = stats.EncodedFrames;
                    lastDropped = stats.DroppedFrames;

                    videoDetail =
                        $"VideoType=Gpu | Running={stats.IsRunning} | Recording={stats.IsRecording} | " +
                        $"Captured={stats.CapturedFrames} | Encoded={stats.EncodedFrames} | Dropped={stats.DroppedFrames} | " +
                        $"CapturedDelta={capturedDelta} | EncodedDelta={encodedDelta} | DroppedDelta={droppedDelta} | " +
                        $"CapturedFps={capturedFps.ToString("0.##", CultureInfo.InvariantCulture)} | " +
                        $"EncodedFps={encodedFps.ToString("0.##", CultureInfo.InvariantCulture)} | " +
                        $"BufferedFrames={stats.BufferedFrames} | BufferedBytes={stats.BufferedBytes}";

                    if (!stats.IsRunning && stats.IsRecording)
                    {
                        AppLogger.Info($"Recording stopped unexpectedly | Active recording pipeline failed | {videoDetail}");
                        await ForceStopContinuousRecordingAsync(
                            "Recording stopped unexpectedly",
                            "The active video encoder or file writer stopped.");
                        break;
                    }
                }
                else if (_videoPipeline is not null)
                {
                    long frames = _videoPipeline.FramesCaptured;
                    videoDetail = $"VideoType={_videoPipeline.GetType().Name} | Frames={frames} | Running={_videoPipeline.IsRunning} | Recording={_videoPipeline.IsContinuousRecording}";
                }
                else
                {
                    videoDetail = "Video=null";
                }

                long managedBytes = GC.GetTotalMemory(forceFullCollection: false);
                long workingSet = Environment.WorkingSet;
                long privateBytes = process.PrivateMemorySize64;
                int threadCount = process.Threads.Count;
                ThreadPool.GetAvailableThreads(out int workerAvailable, out int completionAvailable);
                ThreadPool.GetMaxThreads(out int workerMax, out int completionMax);
                long diskFreeBytes = GetCurrentRecordingFreeDiskBytes();

                AppLogger.Info(
                    $"Recording performance | IntervalSec={elapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture)} | " +
                    $"CpuPercent={cpuPercent.ToString("0.##", CultureInfo.InvariantCulture)} | Threads={threadCount} | " +
                    $"WorkerThreads={workerMax - workerAvailable}/{workerMax} | IoThreads={completionMax - completionAvailable}/{completionMax} | " +
                    $"WorkingSetBytes={workingSet} | PrivateBytes={privateBytes} | ManagedBytes={managedBytes} | DiskFreeBytes={diskFreeBytes} | " +
                    videoDetail);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(CaptureCoordinator), $"Recording performance monitor failed: {ex}");
        }
    }

    private async Task ForceStopContinuousRecordingAsync(string message, string detail)
    {
        if (!IsContinuousRecording)
            return;

        AppLogger.Info($"{message} | Forced recording stop requested | {detail} | Current={DescribeState()}");
        ContinuousRecordingStopping?.Invoke(this, EventArgs.Empty);

        string? path = null;
        Exception? failure = null;

        try
        {
            path = await StopContinuousRecordingCoreAsync(stopDiskMonitor: false);
            AppLogger.Info($"{message} | Forced recording stop saved | Path={Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            failure = ex;
            AppLogger.Error(nameof(CaptureCoordinator), $"{message} | Forced recording stop failed: {ex}");
            IsContinuousRecording = false;
            _continuousNativeCombined = false;

            if (_settings is not null && !_settings.BufferEnabled)
            {
                await _pipelineLock.WaitAsync();
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
                    Message: message,
                    Path: path,
                    Error: failure));

            StopRecordingDiskSpaceMonitor();
            StopRecordingPerformanceMonitor();
        }
    }
    private long GetCurrentRecordingFreeDiskBytes()
    {
        if (_settings is null) return -1;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(_settings.SaveFolder));
            if (string.IsNullOrWhiteSpace(root)) return -1;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }
    private void StopPipelineCore()
    {
        AppLogger.Info($"Coordinator state | Action=StopPipelineCore enter | Current={DescribeState()}");
        try { _continuousMp3Writer?.Dispose(); } catch { }
        try { _audioRecorder?.Dispose(); } catch { }
        try { _videoPipeline?.Stop(); } catch { }
        try { _videoPipeline?.Dispose(); } catch { }
        StopRecordingDiskSpaceMonitor();
        StopRecordingPerformanceMonitor();
        IsContinuousRecording = false;
        _continuousNativeCombined = false;
        _continuousMp3Writer = null;
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
        string streamingMp3 = _continuousMp3Writer is null ? "StreamingMp3=null" : "StreamingMp3=active";
        return $"Continuous={IsContinuousRecording}, {settings}, {video}, {audio}, {streamingMp3}";
    }
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
        StopRecordingDiskSpaceMonitor();
        StopRecordingPerformanceMonitor();
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
