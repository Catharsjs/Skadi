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
    private DateTime _videoStartTime;
    private double _audioVideoStartOffset = 0;

    // ─── UI компоненти ────────────────────────────────────────────────────
    private HotkeyManager _hotkeyManager;
    private OverlayForm _overlay;
    private SettingsForm? _settingsForm;

    // ─── Налаштування та стан ─────────────────────────────────────────────
    private AppSettings _appSettings;
    private CancellationTokenSource? _initCts;
    private readonly SemaphoreSlim _initSemaphore = new SemaphoreSlim(1, 1);
    private EventCapture.Core.Monitoring.HardwareMonitor _hardwareMonitor;
    private const int WM_HOTKEY = 0x0312;
    private string _saveFolder;
    private int _currentFps = 60;
    private int _currentBufferSeconds = 60;
    private volatile bool _overlayVisible = false;
    private System.Threading.Timer? _hardwareTimer;
    private static readonly string _logPath = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    "EventCapture", "full_debug.log");
    private EventCapture.Core.Capture.AudioRecorder? _audioRecorder;

    public MainForm()
    {
        InitializeComponent();
        Opacity = 0;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Minimized;

        // Завантажуємо збережені налаштування
        _appSettings = AppSettings.Load();
       System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    "EventCapture", "full_debug.log");
        System.IO.File.WriteAllText(_logPath,
            $"[{DateTime.Now:HH:mm:ss.fff}] ═══ PROGRAM START ═══\n" +
            $"  Fps: {_appSettings.Fps}, Buffer: {_appSettings.BufferSeconds}s\n" +
            $"  Resolution: {_appSettings.Resolution}\n" +
            $"  RecordSystem: {_appSettings.RecordSystemAudio}\n" +
            $"  RecordMic: {_appSettings.RecordMicrophone}\n" +
            $"  SystemDeviceId: {_appSettings.SystemAudioDeviceId ?? "null"}\n" +
            $"  MicDeviceId: {_appSettings.MicDeviceId ?? "null"}\n\n");
        _saveFolder = _appSettings.SaveFolder;

        // Запускаємо захоплення в фоні щоб не блокувати UI
        Task.Run(async () => await InitializeCapture(_appSettings.Fps, _appSettings.BufferSeconds, _appSettings.Resolution));

        InitializeTray();

        _hotkeyManager = new HotkeyManager(Handle);
        _hotkeyManager.RegisterAll(
            _appSettings.HotkeyScreenshot,
            _appSettings.HotkeySaveVideo,
            _appSettings.HotkeyToggleUI);

        _overlay = new OverlayForm();
        _hardwareMonitor = new EventCapture.Core.Monitoring.HardwareMonitor();
        StartHardwareMonitor();
        Hide();
    }

    // Приховуємо форму з Alt+Tab через WS_EX_TOOLWINDOW
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
    // Використовує debounce (200ms) і семафор щоб уникнути паралельних запусків
    private async Task InitializeCapture(int fps, int bufferSeconds,
        string resolution = "Native", int targetWidth = 0, int targetHeight = 0)
    {
        _initCts?.Cancel();
        System.IO.File.AppendAllText(_logPath,
     $"[{DateTime.Now:HH:mm:ss.fff}] InitializeCapture: fps={fps}, buffer={bufferSeconds}, res={resolution}\n");
        _initCts = new CancellationTokenSource();
        var ct = _initCts.Token;

        try { await Task.Delay(200, ct); }
        catch (TaskCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        await _initSemaphore.WaitAsync();
        try
        {
            if (ct.IsCancellationRequested) return;

            _currentFps = fps;
            _currentBufferSeconds = bufferSeconds;

            _capturer?.Stop();
            _capturer?.Dispose();
            _capturer = null;

            _encoder?.Stop();
            _encoder?.Dispose();
            _encoder = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await Task.Delay(200);

            if (ct.IsCancellationRequested) return;

            // Визначаємо роздільну здатність запису
            int nativeWidth = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width;
            int nativeHeight = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Height;

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

            if (targetWidth > 0) encWidth = targetWidth;
            if (targetHeight > 0) encHeight = targetHeight;

            _encoder = new VideoEncoder(fps, encWidth, encHeight);
            _screenshotSaver = new ScreenshotSaver(_saveFolder, encWidth, encHeight);
            _capturer = new ScreenCapturer(_encoder, fps, encWidth, encHeight);

            _audioRecorder?.Dispose();
            _audioRecorder = new EventCapture.Core.Capture.AudioRecorder();

            // Моніторимо зміну дефолтного пристрою Windows
            _audioRecorder.DefaultDeviceChanged += (newDeviceId) =>
            {
                if (_audioRecorder != null && _audioRecorder.UseDefaultSystemDevice &&
                    _appSettings.RecordSystemAudio && _audioRecorder.IsRecordingSystem)
                {
                    _audioRecorder.RestartSystemCapture(null);

                    if (_settingsForm != null)
                    {
                        try
                        {
                            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                            var device = enumerator.GetDefaultAudioEndpoint(
                                NAudio.CoreAudioApi.DataFlow.Render,
                                NAudio.CoreAudioApi.Role.Multimedia);
                            _settingsForm.UpdateSystemDeviceName(device.FriendlyName);
                        }
                        catch { }
                    }
                }
            };

            // Запускаємо відео і аудіо максимально близько
            _encoder.StartRecording();

            System.IO.File.AppendAllText(_logPath,
    $"[{DateTime.Now:HH:mm:ss.fff}] Encoder started: {encWidth}x{encHeight} {fps}fps\n" +
    $"  AudioRecorder: RecordSystem={_appSettings.RecordSystemAudio}, RecordMic={_appSettings.RecordMicrophone}\n");
            _audioRecorder.StartRecording(
                _appSettings.RecordSystemAudio, _appSettings.SystemAudioDeviceId,
                _appSettings.RecordMicrophone, _appSettings.MicDeviceId);
            _capturer.Start();
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    // ─── Іконка в системному треї ─────────────────────────────────────────
    private void InitializeTray()
    {
        _trayMenu = new ContextMenuStrip();
        _trayMenu.BackColor = Color.FromArgb(28, 28, 30);
        _trayMenu.ForeColor = Color.FromArgb(240, 240, 240);
        _trayMenu.RenderMode = ToolStripRenderMode.System;

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
            itemOpen, new ToolStripSeparator(),
            itemScreenshot, itemSaveVideo,
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
    // Створюється один раз і кешується — повторні відкриття анімовані
    private void ShowSettings()
    {
        if (_settingsForm == null)
        {
            _settingsForm = new SettingsForm(this, _saveFolder, _currentFps, _currentBufferSeconds,
                _appSettings.Resolution, _appSettings.HotkeyScreenshot,
                _appSettings.HotkeySaveVideo, _appSettings.HotkeyToggleUI,
                _appSettings.RecordSystemAudio, _appSettings.SystemAudioDeviceId,
                _appSettings.RecordMicrophone, _appSettings.MicDeviceId);

            _settingsForm.OnSettingsChanged += async (fps, seconds, folder, resolution, hotkeyScreenshot, hotkeySaveVideo, hotkeyToggleUI, recordSystem, systemDeviceId, recordMic, micDeviceId) =>
            {
                // Логуємо зміни ДО оновлення _appSettings
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
                    _settingsForm.LogEvent($"System audio device changed");
                if (micDeviceId != _appSettings.MicDeviceId)
                    _settingsForm.LogEvent($"Mic device changed");
                if (folder != _appSettings.SaveFolder)
                    _settingsForm.LogEvent($"Save folder changed to ...\\{System.IO.Path.GetFileName(folder)}");
                if (hotkeyScreenshot != _appSettings.HotkeyScreenshot)
                    _settingsForm.LogEvent($"Screenshot hotkey changed to {hotkeyScreenshot}");
                if (hotkeySaveVideo != _appSettings.HotkeySaveVideo)
                    _settingsForm.LogEvent($"Save video hotkey changed to {hotkeySaveVideo}");
                if (hotkeyToggleUI != _appSettings.HotkeyToggleUI)
                    _settingsForm.LogEvent($"Toggle UI hotkey changed to {hotkeyToggleUI}");

                // Визначаємо чи потрібен перезапуск
                bool needsRestart = fps != _appSettings.Fps ||
                                    seconds != _appSettings.BufferSeconds ||
                                    resolution != _appSettings.Resolution ||
                                    recordSystem != _appSettings.RecordSystemAudio ||
                                    recordMic != _appSettings.RecordMicrophone ||
                                    systemDeviceId != _appSettings.SystemAudioDeviceId ||
                                    micDeviceId != _appSettings.MicDeviceId;

                // Оновлюємо налаштування
                _saveFolder = folder;
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

                _hotkeyManager.RegisterAll(hotkeyScreenshot, hotkeySaveVideo, hotkeyToggleUI);

                if (needsRestart)
                    await InitializeCapture(fps, seconds, resolution);
            };

            // Тимчасово знімаємо хоткеї під час введення нової комбінації
            _settingsForm.OnHotkeyInputStarted += () => _hotkeyManager.UnregisterAll();
            _settingsForm.OnHotkeyInputFinished += () => _hotkeyManager.RegisterAll(
                _appSettings.HotkeyScreenshot,
                _appSettings.HotkeySaveVideo,
                _appSettings.HotkeyToggleUI);

            _settingsForm.OnOverlayToggled += (visible) =>
            {
                _overlayVisible = visible;
                _overlay.SetSystemInfoVisible(visible);
                if (visible) { _overlay.Show(); _overlay.BringToFront(); }
                else _overlay.Hide();
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
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
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
            _trayIcon.ShowBalloonTip(2000, "EventCapture", "Screenshot saved", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _trayIcon.ShowBalloonTip(2000, "EventCapture", $"Error: {ex.Message}", ToolTipIcon.Error);
        }
    }

    public async void SaveVideo()
    {
        string logPath = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    "EventCapture", "full_debug.log");
        System.IO.File.AppendAllText(_logPath,
     $"\n[{DateTime.Now:HH:mm:ss.fff}] ═══ SaveVideo START ═══\n" +
     $"  RecordSystem={_appSettings.RecordSystemAudio}, RecordMic={_appSettings.RecordMicrophone}\n" +
     $"  IsRecordingSystem={_audioRecorder?.IsRecordingSystem}, IsRecordingMic={_audioRecorder?.IsRecordingMic}\n" +
     $"  LoopbackPaths={_audioRecorder?.LoopbackTempPaths.Count ?? 0}\n");
        try
        {
            double videoElapsed = _encoder.RecordingStopwatch.Elapsed.TotalSeconds;
            var videoPath = await _encoder.SaveLastSecondsAsync(_saveFolder, _currentBufferSeconds);

            if (_audioRecorder != null &&
                (_appSettings.RecordSystemAudio || _appSettings.RecordMicrophone))
            {
                var finalPath = await _audioRecorder.SaveLastSecondsAsync(_saveFolder, _currentBufferSeconds, videoPath, videoElapsed, _encoder.StartTimestamp);

                if (finalPath != null && File.Exists(finalPath))
                    try { File.Delete(videoPath); } catch { }

                _audioRecorder.StartRecording(
                    _appSettings.RecordSystemAudio, _appSettings.SystemAudioDeviceId,
                    _appSettings.RecordMicrophone, _appSettings.MicDeviceId);
            }

            _trayIcon.ShowBalloonTip(2000, "EventCapture", "Video saved", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _trayIcon.ShowBalloonTip(2000, "EventCapture", $"Error: {ex.Message}", ToolTipIcon.Error);
        }
    }

    // ─── Моніторинг системи (оновлення раз на секунду) ───────────────────
    // _overlayVisible — volatile флаг для безпечного читання з іншого потоку
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
        _audioRecorder?.Dispose();
        System.IO.File.AppendAllText(_logPath,
    $"\n[{DateTime.Now:HH:mm:ss.fff}] ═══ PROGRAM EXIT ═══\n" +
    $"  IsRecordingSystem={_audioRecorder?.IsRecordingSystem}\n" +
    $"  LoopbackPaths={_audioRecorder?.LoopbackTempPaths.Count ?? 0}\n");
        _hotkeyManager?.Dispose();
        _hardwareMonitor?.Dispose();
        _capturer?.Stop();
        _capturer?.Dispose();
        _encoder?.Dispose();
        _trayIcon.Visible = false;
        System.Threading.Thread.Sleep(500);
        Application.Exit();
    }

    // ─── Обробка системних повідомлень Windows ────────────────────────────
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case HotkeyManager.HOTKEY_SCREENSHOT: TakeScreenshot(); break;
                case HotkeyManager.HOTKEY_SAVE_VIDEO: SaveVideo(); break;
                case HotkeyManager.HOTKEY_TOGGLE_OVERLAY: ShowSettings(); break;
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

    private void MainForm_Load(object sender, EventArgs e) { }
}