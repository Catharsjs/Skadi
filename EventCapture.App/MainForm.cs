using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;
using EventCapture.Core.Monitoring;
namespace EventCapture.App;

public partial class MainForm : Form
{
    private const int WM_HOTKEY = 0x0312;

    // Налаштування та стан
    private AppSettings _appSettings;
    private string _saveFolder;
    private int _currentFps = 60;
    private int _currentBufferSeconds = 60;
    private volatile bool _overlayVisible;

    // Capture pipeline
    private VideoEncoder _encoder = null!;
    private ScreenCapturer _capturer = null!;
    private ScreenshotSaver _screenshotSaver = null!;
    private AudioRecorder? _audioRecorder;

    // UI та tray
    private NotifyIcon _trayIcon = null!;
    private ContextMenuStrip _trayMenu = null!;
    private HotkeyManager _hotkeyManager = null!;
    private OverlayForm _overlay = null!;
    private SettingsForm? _settingsForm;

    // Моніторинг
    private HardwareMonitor _hardwareMonitor = null!;
    private System.Threading.Timer? _hardwareTimer;
    private System.Threading.Timer? _memoryTimer;

    // Синхронізація
    private CancellationTokenSource? _initCts;
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

    // Audio device events
    private string? _lastDefaultSystemDeviceId;
    private long _lastDefaultSystemDeviceChangeMs;

    public MainForm()
    {
        InitializeComponent();

        ConfigureHiddenWindow();

        _appSettings = AppSettings.Load();
        _saveFolder = _appSettings.SaveFolder;

        LogApplicationStart();

        InitializeTray();
        InitializeHotkeys();
        InitializeOverlay();

        Hide();

        BeginInvoke(new Action(StartDeferredInitialization));
    }

    private void StartDeferredInitialization()
    {
        Task.Run(() =>
        {
            try
            {
                InitializeHardwareMonitoring();
            }
            catch (Exception ex)
            {
                AppLogger.Error(nameof(MainForm), $"InitializeHardwareMonitoring failed: {ex}");
            }
        });

        StartCaptureInitializationInBackground();
    }

    // Початкова конфігурація форми (...
    private void ConfigureHiddenWindow()
    {
        Opacity = 0;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Minimized;
    }
    // ...) Початкова конфігурація форми

    // Ініціалізація компонентів (...
    private void InitializeHotkeys()
    {
        _hotkeyManager = new HotkeyManager(Handle);
        _hotkeyManager.RegisterAll(
            _appSettings.HotkeyScreenshot,
            _appSettings.HotkeySaveVideo,
            _appSettings.HotkeyToggleUI);
    }

    private void InitializeOverlay()
    {
        _overlay = new OverlayForm();
    }

    private void InitializeHardwareMonitoring()
    {
        _hardwareMonitor = new HardwareMonitor();
        StartHardwareMonitor();
        StartMemoryMonitor();
    }

    private void StartCaptureInitializationInBackground()
    {
        // Capture pipeline запускається у фоні, щоб не блокувати UI.
        Task.Run(async () =>
            await InitializeCapture(
                _appSettings.Fps,
                _appSettings.BufferSeconds,
                _appSettings.Resolution));
    }
    // ...) Ініціалізація компонентів

    // Логування запуску (...
    private void LogApplicationStart()
    {
        AppLogger.Info(
            "Program started | " +
            $"Fps={_appSettings.Fps} | " +
            $"Buffer={_appSettings.BufferSeconds}s | " +
            $"Resolution={_appSettings.Resolution} | " +
            $"RecordSystem={_appSettings.RecordSystemAudio} | " +
            $"RecordMic={_appSettings.RecordMicrophone} | " +
            $"SystemDeviceId={_appSettings.SystemAudioDeviceId ?? "null"} | " +
            $"MicDeviceId={_appSettings.MicDeviceId ?? "null"}");
    }
    // ...) Логування запуску

    // Приховування вікна з Alt+Tab (...
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var createParams = base.CreateParams;
            createParams.ExStyle |= WS_EX_TOOLWINDOW;
            return createParams;
        }
    }
    // ...) Приховування вікна з Alt+Tab

    // Ініціалізація capture pipeline (...
    private async Task InitializeCapture(
        int fps,
        int bufferSeconds,
        string resolution = "Native",
        int targetWidth = 0,
        int targetHeight = 0)
    {
        _initCts?.Cancel();
        _initCts = new CancellationTokenSource();

        var cancellationToken = _initCts.Token;

        AppLogger.Info(
            $"InitializeCapture | " +
            $"Fps={fps} | " +
            $"Buffer={bufferSeconds}s | " +
            $"Resolution={resolution}");

        try
        {
            await Task.Delay(500, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        await _initSemaphore.WaitAsync();

        try
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            _currentFps = fps;
            _currentBufferSeconds = bufferSeconds;

            StopCapturePipeline();
            ForceGarbageCollection();

            await Task.Delay(500, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            var (encoderWidth, encoderHeight) =
                ResolveEncoderResolution(
                    resolution,
                    targetWidth,
                    targetHeight);

            CreateCaptureComponents(
                fps,
                encoderWidth,
                encoderHeight);

            AttachAudioDeviceChangeHandler();
            StartRecordingPipeline();

            AppLogger.Info(
                $"Capture initialized | " +
                $"Resolution={encoderWidth}x{encoderHeight} | " +
                $"Fps={fps}");
        }
        catch (TaskCanceledException)
        {
            AppLogger.Debug("InitializeCapture canceled");
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainForm), $"InitializeCapture failed: {ex}");
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    private static void ForceGarbageCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static (int Width, int Height) ResolveEncoderResolution(
        string resolution,
        int targetWidth,
        int targetHeight)
    {
        int nativeWidth = Screen.PrimaryScreen!.Bounds.Width;
        int nativeHeight = Screen.PrimaryScreen!.Bounds.Height;

        int width = resolution switch
        {
            "720p" => 1280,
            "1080p" => 1920,
            "1440p" => 2560,
            _ => nativeWidth
        };

        int height = resolution switch
        {
            "720p" => 720,
            "1080p" => 1080,
            "1440p" => 1440,
            _ => nativeHeight
        };

        if (targetWidth > 0)
            width = targetWidth;

        if (targetHeight > 0)
            height = targetHeight;

        return (width, height);
    }

    private void CreateCaptureComponents(
        int fps,
        int width,
        int height)
    {
        _encoder = new VideoEncoder(
            fps,
            width,
            height);

        _screenshotSaver = new ScreenshotSaver(
            _saveFolder,
            width,
            height);

        _capturer = new ScreenCapturer(
            _encoder,
            fps);

        _audioRecorder = new AudioRecorder();
    }
    // ...) Ініціалізація capture pipeline

    private void RecreateScreenshotSaver()
    {
        _screenshotSaver = new ScreenshotSaver(_saveFolder);
    }

    // Audio device change events (...
    private void AttachAudioDeviceChangeHandler()
    {
        if (_audioRecorder == null)
            return;

        _audioRecorder.DefaultDeviceChanged += OnDefaultSystemDeviceChanged;
    }

    private void OnDefaultSystemDeviceChanged(
        string newDeviceId)
    {
        long now = Environment.TickCount64;

        // Windows інколи генерує однакові events декілька разів.
        // Ігноруємо дублікати протягом 3 секунд.
        bool isDuplicateEvent =
            newDeviceId == _lastDefaultSystemDeviceId &&
            now - _lastDefaultSystemDeviceChangeMs < 3000;

        if (isDuplicateEvent)
            return;

        _lastDefaultSystemDeviceId = newDeviceId;
        _lastDefaultSystemDeviceChangeMs = now;

        if (_audioRecorder == null ||
            !_audioRecorder.UseDefaultSystemDevice ||
            !_appSettings.RecordSystemAudio ||
            !_audioRecorder.IsRecordingSystem)
        {
            return;
        }

        AppLogger.Info($"Default system audio device changed | Id={newDeviceId}");
        _audioRecorder.RestartSystemCapture(null);
        UpdateSettingsSystemDeviceName();
    }

    private void UpdateSettingsSystemDeviceName()
    {
        if (_settingsForm == null)
            return;

        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();

            var device =
                enumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.Role.Multimedia);

            _settingsForm.UpdateSystemDeviceName(device.FriendlyName);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"UpdateSettingsSystemDeviceName warning: {ex.Message}");
        }
    }
    // ...) Audio device change events

    // Запуск та зупинка recording pipeline (...
    private void StartRecordingPipeline()
    {
        if (_encoder == null ||
            _capturer == null)
        {
            return;
        }

        _encoder.StartRecording();

        if (!_capturer.IsRunning)
        {
            _capturer.Start();
        }

        // Базова точка синхронізації audio/video (StartTimestamp)
        _audioRecorder?.StartRecording(
            _appSettings.RecordSystemAudio,
            _appSettings.SystemAudioDeviceId,
            _appSettings.RecordMicrophone,
            _appSettings.MicDeviceId,
            _encoder.StartTimestamp);
    }

    private void StopCapturePipeline()
    {
        DisposeAudioRecorder();
        DisposeScreenCapturer();
        DisposeVideoEncoder();
    }

    private void DisposeAudioRecorder()
    {
        try
        {
            _audioRecorder?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"AudioRecorder dispose warning: {ex.Message}");
        }

        _audioRecorder = null;
    }

    private void DisposeScreenCapturer()
    {
        try
        {
            _capturer?.Stop();
            _capturer?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"ScreenCapturer dispose warning: {ex.Message}");
        }
        _capturer = null!;
    }

    private void DisposeVideoEncoder()
    {
        try
        {
            _encoder?.Stop();
            _encoder?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"VideoEncoder dispose warning: {ex.Message}");
        }
        _encoder = null!;
    }
    // ...) Запуск та зупинка recording pipeline

    // Іконка системного трею (...
    private void InitializeTray()
    {
        _trayMenu = CreateTrayMenu();
        _trayIcon = new NotifyIcon
        {
            Text = "EventCapture",
            Icon = new Icon(
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "EventCapture.ico")),
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettings();
    }

    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(28, 28, 30),
            ForeColor = Color.FromArgb(240, 240, 240),
            RenderMode = ToolStripRenderMode.System
        };

        bool autoStartEnabled = AppSettings.IsAutoStartEnabled();
        var itemOpen = new ToolStripMenuItem("Open Settings");
        var itemScreenshot = new ToolStripMenuItem("Save Screenshot\tAlt+F1");
        var itemSaveVideo = new ToolStripMenuItem("Save Video\tAlt+F2");
        var itemAutostart = new ToolStripMenuItem(autoStartEnabled ? "✓ Launch at Startup" : "Launch at Startup");
        var itemExit = new ToolStripMenuItem("Exit");

        itemOpen.Click += (_, _) => ShowSettings();
        itemScreenshot.Click += (_, _) => TakeScreenshot();
        itemSaveVideo.Click += (_, _) => SaveVideo();
        itemAutostart.Click += (_, _) =>
        {
            autoStartEnabled = !autoStartEnabled;
            AppSettings.SetAutoStart(autoStartEnabled);
            itemAutostart.Text =
                autoStartEnabled
                    ? "✓ Launch at Startup"
                    : "Launch at Startup";
        };

        itemExit.Click += (_, _) => ExitApp();

        menu.Items.AddRange(
            new ToolStripItem[]
            {
            itemOpen,
            new ToolStripSeparator(),
            itemScreenshot,
            itemSaveVideo,
            new ToolStripSeparator(),
            itemAutostart,
            new ToolStripSeparator(),
            itemExit
            });
        return menu;
    }
    // ...) Іконка системного трею

    // Панель налаштувань (...
    private void ShowSettings()
    {
        if (_settingsForm == null)
        {
            CreateSettingsForm();
            AttachSettingsEvents();
        }
        ToggleSettingsWindow();
    }

    private void CreateSettingsForm()
    {
        _settingsForm = new SettingsForm(
            this,
            _saveFolder,
            _currentFps,
            _currentBufferSeconds,
            _appSettings.Resolution,
            _appSettings.HotkeyScreenshot,
            _appSettings.HotkeySaveVideo,
            _appSettings.HotkeyToggleUI,
            _appSettings.RecordSystemAudio,
            _appSettings.SystemAudioDeviceId,
            _appSettings.RecordMicrophone,
            _appSettings.MicDeviceId);
    }

    private void ToggleSettingsWindow()
    {
        if (_settingsForm == null)
            return;

        if (_settingsForm.Visible)
        {
            _ = SlideOut(_settingsForm);
        }
        else
        {
            _ = SlideIn(_settingsForm);
        }
    }
    // ...) Панель налаштувань


    // Events settings форми (...
    private void AttachSettingsEvents()
    {
        if (_settingsForm == null)
            return;
        _settingsForm.OnSettingsChanged += OnSettingsChanged;
        _settingsForm.OnHotkeyInputStarted += OnHotkeyInputStarted;
        _settingsForm.OnHotkeyInputFinished += OnHotkeyInputFinished;
        _settingsForm.OnOverlayToggled += OnOverlayToggled;
    }

    private void OnHotkeyInputStarted()
    {
        _hotkeyManager.UnregisterAll();
    }

    private void OnHotkeyInputFinished()
    {
        _hotkeyManager.RegisterAll(
            _appSettings.HotkeyScreenshot,
            _appSettings.HotkeySaveVideo,
            _appSettings.HotkeyToggleUI);
    }

    private void OnOverlayToggled(bool visible)
    {
        _overlayVisible = visible;
        _overlay.SetSystemInfoVisible(visible);

        if (visible)
        {
            _overlay.Show();
            _overlay.BringToFront();
        }
        else
        {
            _overlay.Hide();
        }
        _settingsForm?.LogEvent($"System info {(visible ? "enabled" : "disabled")}");
    }
    // ...) Events settings форми

    // Застосування налаштувань (...
    private async void OnSettingsChanged(
        int fps,
        int seconds,
        string folder,
        string resolution,
        string hotkeyScreenshot,
        string hotkeySaveVideo,
        string hotkeyToggleUI,
        bool recordSystem,
        string? systemDeviceId,
        bool recordMic,
        string? micDeviceId)
    {
        LogSettingsChanges(
            fps,
            seconds,
            folder,
            resolution,
            hotkeyScreenshot,
            hotkeySaveVideo,
            hotkeyToggleUI,
            recordSystem,
            systemDeviceId,
            recordMic,
            micDeviceId);

        bool needsVideoRestart =
            fps != _appSettings.Fps ||
            resolution != _appSettings.Resolution;

        bool systemAudioChanged =
            recordSystem != _appSettings.RecordSystemAudio ||
            systemDeviceId != _appSettings.SystemAudioDeviceId;

        bool micAudioChanged =
            recordMic != _appSettings.RecordMicrophone ||
            micDeviceId != _appSettings.MicDeviceId;

        ApplySettingsValues(
            fps,
            seconds,
            folder,
            resolution,
            hotkeyScreenshot,
            hotkeySaveVideo,
            hotkeyToggleUI,
            recordSystem,
            systemDeviceId,
            recordMic,
            micDeviceId);

        _hotkeyManager.RegisterAll(
            hotkeyScreenshot,
            hotkeySaveVideo,
            hotkeyToggleUI);

        if (needsVideoRestart)
        {
            await InitializeCapture(
                fps,
                seconds,
                resolution);
            return;
        }

        if (systemAudioChanged ||
            micAudioChanged)
        {
            RestartAudioDevices(
                recordSystem,
                systemDeviceId,
                systemAudioChanged,
                recordMic,
                micDeviceId,
                micAudioChanged);
        }
    }

    private void ApplySettingsValues(
        int fps,
        int seconds,
        string folder,
        string resolution,
        string hotkeyScreenshot,
        string hotkeySaveVideo,
        string hotkeyToggleUI,
        bool recordSystem,
        string? systemDeviceId,
        bool recordMic,
        string? micDeviceId)
    {
        _saveFolder = folder;
        RecreateScreenshotSaver();
        _currentBufferSeconds = seconds;
        _appSettings.Fps = fps;
        _appSettings.BufferSeconds = seconds;
        _appSettings.SaveFolder = folder;
        _appSettings.Resolution = resolution;
        _appSettings.HotkeyScreenshot = hotkeyScreenshot;
        _appSettings.HotkeySaveVideo = hotkeySaveVideo;
        _appSettings.HotkeyToggleUI = hotkeyToggleUI;
        _appSettings.RecordSystemAudio = recordSystem;
        _appSettings.SystemAudioDeviceId = systemDeviceId;
        _appSettings.RecordMicrophone = recordMic;
        _appSettings.MicDeviceId = micDeviceId;
        _appSettings.Save();
    }

    private void RestartAudioDevices(
        bool recordSystem,
        string? systemDeviceId,
        bool systemAudioChanged,
        bool recordMic,
        string? micDeviceId,
        bool micAudioChanged)
    {
        if (_audioRecorder == null)
            return;

        if (systemAudioChanged)
        {
            RestartSystemAudio(recordSystem, systemDeviceId);
        }

        if (micAudioChanged)
        {
            RestartMicrophone(recordMic, micDeviceId);
        }
    }

    private void RestartSystemAudio(bool recordSystem, string? systemDeviceId)
    {
        if (_audioRecorder == null)
            return;

        _audioRecorder.UseDefaultSystemDevice = systemDeviceId == null;

        if (recordSystem)
        {
            _audioRecorder.RestartSystemCapture(systemDeviceId);
        }
    }

    private void RestartMicrophone(bool recordMic, string? micDeviceId)
    {
        if (_audioRecorder == null)
            return;

        _audioRecorder.UseDefaultMicDevice = micDeviceId == null;

        if (recordMic)
        {
            _audioRecorder.RestartMicCapture(micDeviceId);
        }
    }
    // ...) Застосування налаштувань

    // Логування змін налаштувань (...

    private void LogSettingsChanges(
        int fps,
        int seconds,
        string folder,
        string resolution,
        string hotkeyScreenshot,
        string hotkeySaveVideo,
        string hotkeyToggleUI,
        bool recordSystem,
        string? systemDeviceId,
        bool recordMic,
        string? micDeviceId)
    {
        if (_settingsForm == null)
            return;

        if (fps != _appSettings.Fps)
        {
            _settingsForm.LogEvent($"FPS changed {_appSettings.Fps} → {fps}");
        }

        if (seconds != _appSettings.BufferSeconds)
        {
            _settingsForm.LogEvent($"Duration changed {_appSettings.BufferSeconds}s → {seconds}s");
        }

        if (resolution != _appSettings.Resolution)
        {
            _settingsForm.LogEvent($"Resolution changed to {resolution}");
        }

        if (recordSystem != _appSettings.RecordSystemAudio)
        {
            _settingsForm.LogEvent($"System audio {(recordSystem ? "enabled" : "disabled")}");
        }

        if (recordMic != _appSettings.RecordMicrophone)
        {
            _settingsForm.LogEvent($"Microphone {(recordMic ? "enabled" : "disabled")}");
        }

        if (systemDeviceId != _appSettings.SystemAudioDeviceId)
        {
            _settingsForm.LogEvent("System audio device changed");
        }

        if (micDeviceId != _appSettings.MicDeviceId)
        {
            _settingsForm.LogEvent("Mic device changed");
        }

        if (folder != _appSettings.SaveFolder)
        {
            _settingsForm.LogEvent($"Save folder changed to ...\\{Path.GetFileName(folder)}");
        }

        if (hotkeyScreenshot != _appSettings.HotkeyScreenshot)
        {
            _settingsForm.LogEvent($"Screenshot hotkey changed to {hotkeyScreenshot}");
        }

        if (hotkeySaveVideo != _appSettings.HotkeySaveVideo)
        {
            _settingsForm.LogEvent($"Save video hotkey changed to {hotkeySaveVideo}");
        }

        if (hotkeyToggleUI != _appSettings.HotkeyToggleUI)
        {
            _settingsForm.LogEvent($"Toggle UI hotkey changed to {hotkeyToggleUI}");
        }

        AppLogger.Info(
            $"Settings updated | " +
            $"MicEnabled={recordMic} | " +
            $"MicDevice={micDeviceId ?? "null"} | " +
            $"SystemEnabled={recordSystem} | " +
            $"SystemDevice={systemDeviceId ?? "null"}");
    }
    // ...) Логування змін налаштувань

    // Анімація settings panel (...
    private async Task SlideIn(Form form)
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        form.Location = new Point(screen.Right - form.Width, 0);
        form.Opacity = 0;
        form.Show();

        for (int i = 1; i <= 15; i++)
        {
            form.Opacity = i / 15.0;
            await Task.Delay(10);
        }
        form.Opacity = 1;
    }
    private async Task SlideOut(Form form)
    {
        for (int i = 14; i >= 0; i--)
        {
            form.Opacity = i / 15.0;
            await Task.Delay(10);
        }
        form.Hide();
        form.Opacity = 1;
    }
    // ...) Анімація settings panel

    // Збереження скріншота (...
    public void TakeScreenshot()
    {
        try
        {
            _screenshotSaver.SaveScreenshot();
            ShowCustomNotification("Screenshot saved");
            AppLogger.Info("Screenshot saved");
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainForm), $"TakeScreenshot failed: {ex}");

            ShowCustomNotification("Screenshot failed");
        }
    }
    // ...) Збереження скріншота

    // Збереження replay-buffer (...    
    public async void SaveVideo()
    {
        if (!await _saveSemaphore.WaitAsync(0))
        {
            ShowCustomNotification("Video save is already in progress");
            return;
        }
        string? rawVideoPath = null;
        string? finalVideoPath = null;

        try
        {
            AppLogger.Info(
                $"SaveVideo started | " +
                $"RecordSystem={_appSettings.RecordSystemAudio} | " +
                $"RecordMic={_appSettings.RecordMicrophone}");

            ValidateVideoEncoder();

            var saveResult =
                await _encoder.SaveLastSecondsAsync(
                    _saveFolder,
                    _currentBufferSeconds);

            rawVideoPath = saveResult.videoPath;
            finalVideoPath = rawVideoPath;

            AppLogger.Info($"Replay buffer saved | " + $"Path={rawVideoPath}");

            if (ShouldMergeAudio())
            {
                finalVideoPath =
                    await MergeAudioWithVideo(
                        rawVideoPath,
                        saveResult.videoElapsedMs,
                        saveResult.videoStartTimestamp)
                    ?? rawVideoPath;
            }

            ShowCustomNotification("Video saved");
            AppLogger.Info($"SaveVideo completed | " + $"FinalPath={finalVideoPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainForm), $"SaveVideo failed: {ex}");
            ShowCustomNotification("EventCapture Error");
        }
        finally
        {
            await RestartCaptureAfterSave();
            _saveSemaphore.Release();
        }
    }

    private void ValidateVideoEncoder()
    {
        if (_encoder == null || !_encoder.IsRunning)
        {
            throw new InvalidOperationException("Video encoder is not running.");
        }
    }

    private bool ShouldMergeAudio()
    {
        return _audioRecorder != null && (_appSettings.RecordSystemAudio || _appSettings.RecordMicrophone);
    }

    private async Task<string?> MergeAudioWithVideo(
        string rawVideoPath,
        long videoElapsedMs,
        long videoStartTimestamp)
    {
        if (_audioRecorder == null)
            return null;

        var mergedPath = await _audioRecorder.SaveLastSecondsAsync(
                _saveFolder,
                _currentBufferSeconds,
                rawVideoPath,
                videoElapsedMs,
                videoStartTimestamp);

        if (mergedPath != null && File.Exists(mergedPath))
        {
            TryDeleteFile(rawVideoPath);
            return mergedPath;
        }

        return rawVideoPath;
    }

    private async Task RestartCaptureAfterSave()
    {
        try
        {
            if (_encoder == null)
                return;

            if (!_capturer.IsRunning)
            {
                _capturer.Start();
            }

            _encoder.StartRecording();
            _audioRecorder?.StartRecording(
                _appSettings.RecordSystemAudio,
                _appSettings.SystemAudioDeviceId,
                _appSettings.RecordMicrophone,
                _appSettings.MicDeviceId,
                _encoder.StartTimestamp);

            AppLogger.Info($"Capture restarted | " + $"NewTimestamp={_encoder.StartTimestamp}");
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainForm), $"RestartCaptureAfterSave failed: {ex}");
        }
        await Task.CompletedTask;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
    // ...) Збереження replay-buffer

    // Моніторинг системи (...    

    private void StartHardwareMonitor()
    {
        _hardwareTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                _hardwareMonitor.Update();

                if (!_overlayVisible || _overlay.IsDisposed)
                    return;

                _overlay.BeginInvoke(new Action(() =>
                {
                    if (_overlay.IsDisposed)
                        return;

                    _overlay.UpdateSystemInfo(
                        _hardwareMonitor.CpuLoad,
                        _hardwareMonitor.CpuFrequency,
                        _hardwareMonitor.GpuLoad,
                        _hardwareMonitor.GpuVram,
                        _hardwareMonitor.RamUsed,
                        _hardwareMonitor.CpuName,
                        _hardwareMonitor.GpuName,
                        _hardwareMonitor.RamType,
                        _hardwareMonitor.RamFrequency,
                        _hardwareMonitor.TotalRamGB);
                }));
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"StartHardwareMonitor warning: {ex.Message}");
            }

        }, null, 0, 1000);
    }

    private void StartMemoryMonitor()
    {
        _memoryTimer = new System.Threading.Timer(_ =>
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();

            process.Refresh();

            AppLogger.Debug(
                $"Memory report | " +
                $"WorkingSet={process.WorkingSet64 / 1024 / 1024}MB | " +
                $"Private={process.PrivateMemorySize64 / 1024 / 1024}MB | " +
                $"GC={GC.GetTotalMemory(false) / 1024 / 1024}MB");

        }, null, 0, 60_000);
    }
    // ...) Моніторинг системи

    // Керування audio devices (...    
    public void SetUserSelectedSystemDevice(string deviceId)
    {
        if (_audioRecorder == null)
            return;

        _audioRecorder.UseDefaultSystemDevice = false;
        _audioRecorder.RestartSystemCapture(deviceId);
    }
    // ...) Керування audio devices

    // Завершення програми (...    
    private void ExitApp()
    {
        AppLogger.Info("Program exit");
        DisposeApplicationResources();
        _trayIcon.Visible = false;
        Thread.Sleep(300);
        Application.Exit();
    }

    private void DisposeApplicationResources()
    {
        try { _audioRecorder?.Dispose(); } catch { }
        try { _hotkeyManager?.Dispose(); } catch { }
        try { _hardwareMonitor?.Dispose(); } catch { }
        try { _hardwareTimer?.Dispose(); } catch { }
        try { _memoryTimer?.Dispose(); } catch { }
        try { _capturer?.Stop(); } catch { }
        try { _capturer?.Dispose(); } catch { }
        try { _encoder?.Dispose(); } catch { }
    }
    // ...) Завершення програми

    // Windows messages (...
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            HandleHotkey(m.WParam.ToInt32());
        }
        base.WndProc(ref m);
    }

    private void HandleHotkey(int hotkeyId)
    {
        switch (hotkeyId)
        {
            case HotkeyManager.HOTKEY_SCREENSHOT:
                TakeScreenshot();
                break;

            case HotkeyManager.HOTKEY_SAVE_VIDEO:
                SaveVideo();
                break;

            case HotkeyManager.HOTKEY_TOGGLE_OVERLAY:
                ShowSettings();
                break;
        }
    }

    protected override void OnFormClosing(
        FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
    // ...) Windows messages

    // Notifications (...    
    private void ShowCustomNotification(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowCustomNotification(message));
            return;
        }

        try
        {
            var notification = new CustomNotificationForm(message);
            notification.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Notification error: {ex.Message}");
        }
    }
    // ...) Notifications

    private void MainForm_Load(object sender, EventArgs e) { }
}