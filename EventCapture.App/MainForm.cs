using EventCapture.Core.Capture;

namespace EventCapture.App;

public partial class MainForm : Form
{
    // ─── Компоненти трею ───────────────────────────────────────────────────
    private NotifyIcon _trayIcon = null!;
    private ContextMenuStrip _trayMenu = null!;

    // ─── Захоплення та кодування відео ────────────────────────────────────
    private VideoEncoder _encoder = null!;
    private ScreenCapturer _capturer = null!;
    private ScreenshotSaver _screenshotSaver = null!;
    private EventCapture.Core.Capture.AudioRecorder? _audioRecorder;

    // ─── UI компоненти ────────────────────────────────────────────────────
    private HotkeyManager _hotkeyManager;
    private OverlayForm _overlay;
    private SettingsForm? _settingsForm;

    // ─── Налаштування та стан ─────────────────────────────────────────────
    private AppSettings _appSettings;
    private CancellationTokenSource? _initCts;
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
    private EventCapture.Core.Monitoring.HardwareMonitor _hardwareMonitor;

    private const int WM_HOTKEY = 0x0312;
    private string? _lastDefaultSystemDeviceId;
    private long _lastDefaultSystemDeviceChangeMs = 0;
    private string _saveFolder;
    private int _currentFps = 60;
    private int _currentBufferSeconds = 60;
    private volatile bool _overlayVisible = false;

    private System.Threading.Timer? _hardwareTimer;
    private System.Threading.Timer? _memoryTimer;

    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "EventCapture",
        "full_debug.log");

    private static readonly object _logLock = new();

    public MainForm()
    {
        InitializeComponent();

        Opacity = 0;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Minimized;

        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);

        _appSettings = AppSettings.Load();
        _saveFolder = _appSettings.SaveFolder;

        File.WriteAllText(_logPath,
            $"[{DateTime.Now:HH:mm:ss.fff}] ═══ PROGRAM START ═══\n" +
            $"  Fps: {_appSettings.Fps}, Buffer: {_appSettings.BufferSeconds}s\n" +
            $"  Resolution: {_appSettings.Resolution}\n" +
            $"  RecordSystem: {_appSettings.RecordSystemAudio}\n" +
            $"  RecordMic: {_appSettings.RecordMicrophone}\n" +
            $"  SystemDeviceId: {_appSettings.SystemAudioDeviceId ?? "null"}\n" +
            $"  MicDeviceId: {_appSettings.MicDeviceId ?? "null"}\n\n");

        // Запускаємо захоплення у фоні, щоб не блокувати UI.
        Task.Run(async () =>
            await InitializeCapture(
                _appSettings.Fps,
                _appSettings.BufferSeconds,
                _appSettings.Resolution));

        InitializeTray();

        _hotkeyManager = new HotkeyManager(Handle);
        _hotkeyManager.RegisterAll(
            _appSettings.HotkeyScreenshot,
            _appSettings.HotkeySaveVideo,
            _appSettings.HotkeyToggleUI);

        _overlay = new OverlayForm();
        _hardwareMonitor = new EventCapture.Core.Monitoring.HardwareMonitor();

        StartHardwareMonitor();
        StartMemoryMonitor();

        Hide();
    }

    // Приховуємо форму з Alt+Tab через WS_EX_TOOLWINDOW.
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    // ─── Ініціалізація захоплення ──────────────────────────────────────────
    // Використовує debounce і семафор, щоб уникнути паралельних запусків.
    private async Task InitializeCapture(
        int fps,
        int bufferSeconds,
        string resolution = "Native",
        int targetWidth = 0,
        int targetHeight = 0)
    {
        _initCts?.Cancel();

        SafeLog(
            $"[{DateTime.Now:HH:mm:ss.fff}] InitializeCapture: fps={fps}, buffer={bufferSeconds}, res={resolution}\n");

        _initCts = new CancellationTokenSource();
        var ct = _initCts.Token;

        try
        {
            await Task.Delay(500, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        await _initSemaphore.WaitAsync();

        try
        {
            if (ct.IsCancellationRequested)
                return;

            _currentFps = fps;
            _currentBufferSeconds = bufferSeconds;

            StopCapturePipeline();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await Task.Delay(500, ct);

            if (ct.IsCancellationRequested)
                return;

            int nativeWidth = Screen.PrimaryScreen!.Bounds.Width;
            int nativeHeight = Screen.PrimaryScreen!.Bounds.Height;

            int encWidth = resolution switch
            {
                "720p" => 1280,
                "1080p" => 1920,
                "1440p" => 2560,
                _ => nativeWidth
            };

            int encHeight = resolution switch
            {
                "720p" => 720,
                "1080p" => 1080,
                "1440p" => 1440,
                _ => nativeHeight
            };

            if (targetWidth > 0)
                encWidth = targetWidth;

            if (targetHeight > 0)
                encHeight = targetHeight;

            _encoder = new VideoEncoder(fps, encWidth, encHeight);
            _screenshotSaver = new ScreenshotSaver(_saveFolder, encWidth, encHeight);
            _capturer = new ScreenCapturer(_encoder, fps, encWidth, encHeight);
            _audioRecorder = new EventCapture.Core.Capture.AudioRecorder();

            AttachAudioDeviceChangeHandler();

            StartRecordingPipeline();

            SafeLog(
                $"[{DateTime.Now:HH:mm:ss.fff}] Encoder started: {encWidth}x{encHeight} {fps}fps\n" +
                $"  AudioRecorder: RecordSystem={_appSettings.RecordSystemAudio}, RecordMic={_appSettings.RecordMicrophone}\n");
        }
        catch (TaskCanceledException)
        {
            // Ініціалізацію скасовано новими налаштуваннями.
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    private void AttachAudioDeviceChangeHandler()
    {
        if (_audioRecorder == null)
            return;

        _audioRecorder.DefaultDeviceChanged += newDeviceId =>
        {
            long now = Environment.TickCount64;

            // Windows інколи спамить однакові device-change events.
            // Ігноруємо дублікати протягом 3 секунд.
            if (newDeviceId == _lastDefaultSystemDeviceId &&
                now - _lastDefaultSystemDeviceChangeMs < 3000)
            {
                return;
            }

            _lastDefaultSystemDeviceId = newDeviceId;
            _lastDefaultSystemDeviceChangeMs = now;

            if (_audioRecorder != null &&
                _audioRecorder.UseDefaultSystemDevice &&
                _appSettings.RecordSystemAudio &&
                _audioRecorder.IsRecordingSystem)
            {
                _audioRecorder.RestartSystemCapture(null);

                if (_settingsForm != null)
                {
                    try
                    {
                        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();

                        var device = enumerator.GetDefaultAudioEndpoint(
                            NAudio.CoreAudioApi.DataFlow.Render,
                            NAudio.CoreAudioApi.Role.Multimedia);

                        _settingsForm.UpdateSystemDeviceName(device.FriendlyName);
                    }
                    catch
                    {
                    }
                }
            }
        };
    }

    private void StartRecordingPipeline()
    {
        if (_encoder == null || _capturer == null)
            return;

        _encoder.StartRecording();

        if (!_capturer.IsRunning)
            _capturer.Start();

        // ВАЖЛИВО: AudioRecorder отримує той самий StartTimestamp, що й VideoEncoder.
        // Це базова точка синхронізації audio/video.
        _audioRecorder?.StartRecording(
            _appSettings.RecordSystemAudio,
            _appSettings.SystemAudioDeviceId,
            _appSettings.RecordMicrophone,
            _appSettings.MicDeviceId,
            _encoder.StartTimestamp);
    }

    private void StopCapturePipeline()
    {
        try
        {
            _audioRecorder?.Dispose();
            _audioRecorder = null;
        }
        catch { }

        try
        {
            _capturer?.Stop();
            _capturer?.Dispose();
            _capturer = null!;
        }
        catch { }

        try
        {
            _encoder?.Stop();
            _encoder?.Dispose();
            _encoder = null!;
        }
        catch { }
    }

    // ─── Іконка в системному треї ─────────────────────────────────────────
    private void InitializeTray()
    {
        _trayMenu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(28, 28, 30),
            ForeColor = Color.FromArgb(240, 240, 240),
            RenderMode = ToolStripRenderMode.System
        };

        var itemOpen = new ToolStripMenuItem("Open Settings");
        var itemScreenshot = new ToolStripMenuItem("Save Screenshot\tAlt+F1");
        var itemSaveVideo = new ToolStripMenuItem("Save Video\tAlt+F2");

        bool autoStartEnabled = AppSettings.IsAutoStartEnabled();
        var itemAutostart = new ToolStripMenuItem(
            autoStartEnabled ? "✓ Launch at Startup" : "Launch at Startup");

        var itemExit = new ToolStripMenuItem("Exit");

        itemOpen.Click += (s, e) => ShowSettings();
        itemScreenshot.Click += (s, e) => TakeScreenshot();
        itemSaveVideo.Click += (s, e) => SaveVideo();

        itemAutostart.Click += (s, e) =>
        {
            autoStartEnabled = !autoStartEnabled;
            AppSettings.SetAutoStart(autoStartEnabled);
            itemAutostart.Text = autoStartEnabled ? "✓ Launch at Startup" : "Launch at Startup";
        };

        itemExit.Click += (s, e) => ExitApp();

        _trayMenu.Items.AddRange(new ToolStripItem[]
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

        _trayIcon = new NotifyIcon
        {
            Text = "EventCapture",
            Icon = new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EventCapture.ico")),
            ContextMenuStrip = _trayMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (s, e) => ShowSettings();
    }

    // ─── Панель налаштувань ───────────────────────────────────────────────
    private void ShowSettings()
    {
        if (_settingsForm == null)
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

            _settingsForm.OnSettingsChanged += async (
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
                micDeviceId) =>
            {
                if (fps != _appSettings.Fps)
                    _settingsForm.LogEvent($"FPS changed {_appSettings.Fps} → {fps}");
                if (seconds != _appSettings.BufferSeconds)
                    _settingsForm.LogEvent($"Duration changed {_appSettings.BufferSeconds}s → {seconds}s");
                if (resolution != _appSettings.Resolution)
                    _settingsForm.LogEvent($"Resolution changed to {resolution}");
                if (recordSystem != _appSettings.RecordSystemAudio)
                    _settingsForm.LogEvent($"System audio {(recordSystem ? "enabled" : "disabled")}");
                if (recordMic != _appSettings.RecordMicrophone)
                    _settingsForm.LogEvent($"Microphone {(recordMic ? "enabled" : "disabled")}");
                if (systemDeviceId != _appSettings.SystemAudioDeviceId)
                    _settingsForm.LogEvent("System audio device changed");
                if (micDeviceId != _appSettings.MicDeviceId)
                    _settingsForm.LogEvent("Mic device changed");
                if (folder != _appSettings.SaveFolder)
                    _settingsForm.LogEvent($"Save folder changed to ...\\{Path.GetFileName(folder)}");
                if (hotkeyScreenshot != _appSettings.HotkeyScreenshot)
                    _settingsForm.LogEvent($"Screenshot hotkey changed to {hotkeyScreenshot}");
                if (hotkeySaveVideo != _appSettings.HotkeySaveVideo)
                    _settingsForm.LogEvent($"Save video hotkey changed to {hotkeySaveVideo}");
                if (hotkeyToggleUI != _appSettings.HotkeyToggleUI)
                    _settingsForm.LogEvent($"Toggle UI hotkey changed to {hotkeyToggleUI}");

                // ВАЖЛИВО:
                // Усі flags рахуємо ДО оновлення _appSettings.
                bool needsVideoRestart =
                    fps != _appSettings.Fps ||
                    resolution != _appSettings.Resolution;

                bool systemAudioChanged =
                    recordSystem != _appSettings.RecordSystemAudio ||
                    systemDeviceId != _appSettings.SystemAudioDeviceId;

                bool micAudioChanged =
                    recordMic != _appSettings.RecordMicrophone ||
                    micDeviceId != _appSettings.MicDeviceId;

                bool needsAudioOnlyRestart =
                    systemAudioChanged && !needsVideoRestart && !micAudioChanged;

                bool needsFullRestart = needsVideoRestart;

                _saveFolder = folder;
                _currentBufferSeconds = seconds;

                _appSettings.Fps = fps;
                SafeLog(
    $"[{DateTime.Now:HH:mm:ss.fff}] UI Settings Mic Selection\n" +
    $"  recordMic: {recordMic}\n" +
    $"  micDeviceId from UI: {micDeviceId ?? "null"}\n" +
    $"  previous MicDeviceId: {_appSettings.MicDeviceId ?? "null"}\n");
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

                _hotkeyManager.RegisterAll(
                    hotkeyScreenshot,
                    hotkeySaveVideo,
                    hotkeyToggleUI);

                if (needsFullRestart)
                {
                    await InitializeCapture(fps, seconds, resolution);
                }
                else if (systemAudioChanged || micAudioChanged)
                {
                    if (_audioRecorder != null)
                    {
                        if (systemAudioChanged)
                        {
                            _audioRecorder.UseDefaultSystemDevice = systemDeviceId == null;

                            if (recordSystem)
                                _audioRecorder.RestartSystemCapture(systemDeviceId);
                        }

                        if (micAudioChanged)
                        {
                            _audioRecorder.UseDefaultMicDevice = micDeviceId == null;

                            if (recordMic)
                                _audioRecorder.RestartMicCapture(micDeviceId);
                        }
                    }
                }
            };

            _settingsForm.OnHotkeyInputStarted += () => _hotkeyManager.UnregisterAll();

            _settingsForm.OnHotkeyInputFinished += () => _hotkeyManager.RegisterAll(
                _appSettings.HotkeyScreenshot,
                _appSettings.HotkeySaveVideo,
                _appSettings.HotkeyToggleUI);

            _settingsForm.OnOverlayToggled += visible =>
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

                _settingsForm.LogEvent($"System info {(visible ? "enabled" : "disabled")}");
            };
        }

        if (_settingsForm.Visible)
            _ = SlideOut(_settingsForm);
        else
            _ = SlideIn(_settingsForm);
    }

    // ─── Анімація панелі налаштувань ──────────────────────────────────────
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

    public void TakeScreenshot()
    {
        try
        {
            _screenshotSaver.SaveScreenshot();
            ShowCustomNotification("Screenshot saved");
        }
        catch (Exception ex)
        {
            ShowCustomNotification("Screenshot failed");
        }
    }

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
            SafeLog(
                $"\n[{DateTime.Now:HH:mm:ss.fff}] ═══ SaveVideo START ═══\n" +
                $"  RecordSystem={_appSettings.RecordSystemAudio}, RecordMic={_appSettings.RecordMicrophone}\n" +
                $"  EncoderRunning={_encoder?.IsRunning}\n" +
                $"  CapturerRunning={_capturer?.IsRunning}\n" +
                $"  IsRecordingSystem={_audioRecorder?.IsRecordingSystem}, IsRecordingMic={_audioRecorder?.IsRecordingMic}\n" +
                $"  LoopbackPaths={_audioRecorder?.LoopbackTempPaths.Count ?? 0}\n");

            if (_encoder == null || !_encoder.IsRunning)
                throw new InvalidOperationException("Video encoder is not running.");

            var (videoPath, videoStartTimestamp, videoElapsedMs) =
                await _encoder.SaveLastSecondsAsync(_saveFolder, _currentBufferSeconds);

            rawVideoPath = videoPath;
            finalVideoPath = videoPath;

            SafeLog(
                $"[{DateTime.Now:HH:mm:ss.fff}] VideoEncoder\n" +
                $"  videoPath: {videoPath}\n" +
                $"  videoStartTimestamp: {videoStartTimestamp}\n" +
                $"  videoElapsedMs: {videoElapsedMs}\n");

            if (_audioRecorder != null &&
                (_appSettings.RecordSystemAudio || _appSettings.RecordMicrophone))
            {
                var mergedPath = await _audioRecorder.SaveLastSecondsAsync(
                    _saveFolder,
                    _currentBufferSeconds,
                    videoPath,
                    videoElapsedMs,
                    videoStartTimestamp);

                if (mergedPath != null && File.Exists(mergedPath))
                {
                    finalVideoPath = mergedPath;

                    try
                    {
                        File.Delete(videoPath);
                    }
                    catch { }
                }
            }

            ShowCustomNotification("Video saved");

            SafeLog(
                $"[{DateTime.Now:HH:mm:ss.fff}] SaveVideo DONE\n" +
                $"  rawVideoPath: {rawVideoPath}\n" +
                $"  finalVideoPath: {finalVideoPath}\n");
        }
        catch (Exception ex)
        {
            SafeLog(
                $"[{DateTime.Now:HH:mm:ss.fff}] SaveVideo ERROR: {ex}\n");

            ShowCustomNotification("EventCapture Error");
        }
        finally
        {
            try
            {
                // Критично: після VideoEncoder.SaveLastSecondsAsync encoder зупинений.
                // Перезапуск робимо тут, централізовано, після завершення audio trim/merge.
                // Так video й audio отримують один актуальний StartTimestamp.
                if (_encoder != null)
                {
                    if (!_capturer.IsRunning)
                        _capturer.Start();

                    _encoder.StartRecording();

                    _audioRecorder?.StartRecording(
                        _appSettings.RecordSystemAudio,
                        _appSettings.SystemAudioDeviceId,
                        _appSettings.RecordMicrophone,
                        _appSettings.MicDeviceId,
                        _encoder.StartTimestamp);

                    SafeLog(
                        $"[{DateTime.Now:HH:mm:ss.fff}] Capture restarted after save\n" +
                        $"  newEncoderStartTimestamp: {_encoder.StartTimestamp}\n" +
                        $"  RecordSystem={_appSettings.RecordSystemAudio}, RecordMic={_appSettings.RecordMicrophone}\n");
                }
            }
            catch (Exception restartEx)
            {
                SafeLog(
                    $"[{DateTime.Now:HH:mm:ss.fff}] Restart after save ERROR: {restartEx}\n");
            }

            _saveSemaphore.Release();
        }
    }

    // ─── Моніторинг системи ───────────────────────────────────────────────
    private void StartHardwareMonitor()
    {
        _hardwareTimer = new System.Threading.Timer(_ =>
        {
            _hardwareMonitor.Update();

            if (_overlayVisible)
            {
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
            }
        }, null, 0, 1000);
    }

    private void StartMemoryMonitor()
    {
        _memoryTimer = new System.Threading.Timer(_ =>
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            proc.Refresh();

            SafeLog(
                $"[{DateTime.Now:HH:mm:ss}] MEMORY REPORT\n" +
                $"  WorkingSet:     {proc.WorkingSet64 / 1024 / 1024} MB\n" +
                $"  PrivateMemory:  {proc.PrivateMemorySize64 / 1024 / 1024} MB\n" +
                $"  GC Total:       {GC.GetTotalMemory(false) / 1024 / 1024} MB\n" +
                $"  Encoder running: {_encoder?.IsRunning}\n" +
                $"  Capturer running: {_capturer?.IsRunning}\n" +
                $"  AudioRecorder: system={_audioRecorder?.IsRecordingSystem}, mic={_audioRecorder?.IsRecordingMic}\n\n");
        }, null, 0, 60_000);
    }

    public void SetUserSelectedSystemDevice(string deviceId)
    {
        if (_audioRecorder != null)
        {
            _audioRecorder.UseDefaultSystemDevice = false;
            _audioRecorder.RestartSystemCapture(deviceId);
        }
    }

    private void ExitApp()
    {
        SafeLog(
            $"\n[{DateTime.Now:HH:mm:ss.fff}] ═══ PROGRAM EXIT ═══\n" +
            $"  IsRecordingSystem={_audioRecorder?.IsRecordingSystem}\n" +
            $"  LoopbackPaths={_audioRecorder?.LoopbackTempPaths.Count ?? 0}\n");

        try { _audioRecorder?.Dispose(); } catch { }
        try { _hotkeyManager?.Dispose(); } catch { }
        try { _hardwareMonitor?.Dispose(); } catch { }
        try { _hardwareTimer?.Dispose(); } catch { }
        try { _memoryTimer?.Dispose(); } catch { }
        try { _capturer?.Stop(); } catch { }
        try { _capturer?.Dispose(); } catch { }
        try { _encoder?.Dispose(); } catch { }

        _trayIcon.Visible = false;

        Thread.Sleep(500);
        Application.Exit();
    }

    // ─── Обробка системних повідомлень Windows ────────────────────────────
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
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

        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }
    private static void SafeLog(string text)
    {
        lock (_logLock)
        {
            try
            {
                File.AppendAllText(_logPath, text);
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }

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
            SafeLog(
                $"[{DateTime.Now:HH:mm:ss.fff}] Notification error: {ex}\n");
        }
    }

    private void MainForm_Load(object sender, EventArgs e) { }
}
