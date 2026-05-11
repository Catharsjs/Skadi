using EventCapture.Core.Buffer;
using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;

namespace EventCapture.App;

public partial class MainForm : Form
{
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;
    private VideoEncoder _encoder;
    private ScreenCapturer _capturer;
    private ScreenshotSaver _screenshotSaver;
    private HotkeyManager _hotkeyManager;
    private OverlayForm _overlay;
    private SettingsForm? _settingsForm;
    private AppSettings _appSettings;
    private EventCapture.Core.Monitoring.HardwareMonitor _hardwareMonitor;
    private const int WM_HOTKEY = 0x0312;
    private string _saveFolder;
    private int _currentFps = 15;
    private int _currentBufferSeconds = 60;

    public MainForm()
    {
        InitializeComponent();
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Minimized;
        _appSettings = AppSettings.Load();
        _saveFolder = _appSettings.SaveFolder;
        Task.Run(async () => await InitializeCapture(_appSettings.Fps, _appSettings.BufferSeconds));
        InitializeTray();
        _hotkeyManager = new HotkeyManager(Handle);
        _overlay = new OverlayForm();
        _hardwareMonitor = new EventCapture.Core.Monitoring.HardwareMonitor();
        StartHardwareMonitor();
        Hide();
    }

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

    private async Task InitializeCapture(int fps, int bufferSeconds,
    int targetWidth = 0, int targetHeight = 0)
    {
        _currentFps = fps;
        _currentBufferSeconds = bufferSeconds;

        _capturer?.Stop();
        _capturer?.Dispose();
        _encoder?.Dispose();

        int encWidth = targetWidth > 0 ? targetWidth
            : System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width;
        int encHeight = targetHeight > 0 ? targetHeight
            : System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Height;

        AppLogger.LogSettings(fps, bufferSeconds, _saveFolder, encWidth, encHeight);

        _encoder = new VideoEncoder(fps, encWidth, encHeight);
        _screenshotSaver = new ScreenshotSaver(_saveFolder);
        _capturer = new ScreenCapturer(_encoder, fps, encWidth, encHeight);

        _encoder.StartRecording();
        _capturer.Start();
    }

    private void InitializeTray()
    {
        _trayMenu = new ContextMenuStrip();
        _trayMenu.BackColor = Color.FromArgb(28, 28, 30);
        _trayMenu.ForeColor = Color.FromArgb(240, 240, 240);
        _trayMenu.RenderMode = ToolStripRenderMode.System;

        var itemOpen = new ToolStripMenuItem("Open Settings");
        var itemScreenshot = new ToolStripMenuItem("Save Screenshot\tAlt+F1");
        var itemSaveVideo = new ToolStripMenuItem("Save Video\tAlt+F2");
        var itemExit = new ToolStripMenuItem("Exit");

        itemOpen.Click += (s, e) => ShowSettings();
        itemScreenshot.Click += (s, e) => TakeScreenshot();
        itemSaveVideo.Click += (s, e) => SaveVideo();
        itemExit.Click += (s, e) => ExitApp();

        _trayMenu.Items.AddRange(new ToolStripItem[]
        {
            itemOpen, new ToolStripSeparator(),
            itemScreenshot, itemSaveVideo,
            new ToolStripSeparator(), itemExit
        });

        _trayIcon = new NotifyIcon
        {
            Text = "EventCapture",
            Icon = SystemIcons.Application,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (s, e) => ShowSettings();
    }

    private void ShowSettings()
    {
        if (_settingsForm == null)
        {
            _settingsForm = new SettingsForm(this, _saveFolder, _currentFps, _currentBufferSeconds);
            _settingsForm.OnSettingsChanged += async (fps, seconds, folder) =>
            {
                _saveFolder = folder;
                _appSettings.Fps = fps;
                _appSettings.BufferSeconds = seconds;
                _appSettings.SaveFolder = folder;
                _appSettings.Save();
            };
            _settingsForm.OnOverlayToggled += (visible) =>
            {
                _overlay.SetSystemInfoVisible(visible);
                if (visible) _overlay.Show();
                else _overlay.Hide();
            };
        }

        if (_settingsForm.Visible)
            _ = SlideOut(_settingsForm);
        else
            _ = SlideIn(_settingsForm);
    }

    private async Task SlideIn(Form form)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        form.Location = new Point(screen.Right - form.Width, 0);
        form.Opacity = 0;
        form.Show();

        int steps = 15;
        for (int i = 1; i <= steps; i++)
        {
            form.Opacity = i / (double)steps;
            await Task.Delay(10);
        }
        form.Opacity = 1;
    }

    private async Task SlideOut(Form form)
    {
        int steps = 15;
        for (int i = steps - 1; i >= 0; i--)
        {
            form.Opacity = i / (double)steps;
            await Task.Delay(10);
        }
        form.Hide();
        form.Opacity = 1;
    }

    public void TakeScreenshot()
    {
        AppLogger.LogAction("Screenshot requested");
        try
        {
            var path = _screenshotSaver.SaveScreenshot();
            AppLogger.LogResult($"Screenshot saved: {path}");
            _trayIcon.ShowBalloonTip(2000, "EventCapture", "Screenshot saved", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("TakeScreenshot", ex.Message);
            _trayIcon.ShowBalloonTip(2000, "EventCapture", $"Error: {ex.Message}", ToolTipIcon.Error);
        }
    }

    public async void SaveVideo()
    {
        AppLogger.LogAction("SaveVideo requested");
        try
        {
            var path = await _encoder.SaveLastSecondsAsync(_saveFolder, _currentBufferSeconds);
            AppLogger.LogResult($"Video saved: {path}");
            _trayIcon.ShowBalloonTip(2000, "EventCapture", "Video saved", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("SaveVideo", ex.Message);
            _trayIcon.ShowBalloonTip(2000, "EventCapture", $"Error: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void StartHardwareMonitor()
    {
        var timer = new System.Threading.Timer(_ =>
        {
            _hardwareMonitor.Update();
            if (_overlay.Visible)
            {
                _overlay.UpdateBuffer(_currentBufferSeconds);
                _overlay.UpdateFps(_capturer.IsRunning ? _currentFps : 0);
                _overlay.UpdateSystemInfo(
                    _hardwareMonitor.CpuLoad,
                    _hardwareMonitor.CpuFrequency,
                    _hardwareMonitor.GpuLoad,
                    _hardwareMonitor.GpuFrequency,
                    _hardwareMonitor.GpuVram,
                    _hardwareMonitor.RamUsed);
            }
        }, null, 0, 1000);
    }

    private void ExitApp()
    {
        _hotkeyManager?.Dispose();
        _hardwareMonitor?.Dispose();
        _capturer?.Stop();
        _capturer?.Dispose();
        _encoder?.Dispose();
        _trayIcon.Visible = false;
        System.Threading.Thread.Sleep(500);
        Application.Exit();
    }

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

    private void MainForm_Load(object sender, EventArgs e) { }
}