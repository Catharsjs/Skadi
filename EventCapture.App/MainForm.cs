using EventCapture.Core.Buffer;
using EventCapture.Core.Capture;

namespace EventCapture.App;

public partial class MainForm : Form
{
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;
    private RingBuffer<FrameEntry> _buffer;
    private ScreenCapturer _capturer;
    private ScreenshotSaver _screenshotSaver;
    private VideoSaver _videoSaver;
    private HotkeyManager _hotkeyManager;
    private OverlayForm _overlay;
    private SettingsForm? _settingsForm;
    private const int WM_HOTKEY = 0x0312;
    private string _saveFolder;

    public MainForm()
    {
        InitializeComponent();
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        _saveFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EventCapture");
        InitializeCapture(fps: 15, bufferSeconds: 60);
        InitializeTray();
        _hotkeyManager = new HotkeyManager(Handle);
        _overlay = new OverlayForm();
        Hide();
    }

    private void InitializeCapture(int fps, int bufferSeconds)
    {
        _capturer?.Stop();
        _buffer = new RingBuffer<FrameEntry>(fps * bufferSeconds);
        _screenshotSaver = new ScreenshotSaver(_saveFolder);
        _videoSaver = new VideoSaver(_saveFolder);
        _capturer = new ScreenCapturer(_buffer, fps);
        _capturer.Start();
    }

    private void InitializeTray()
    {
        _trayMenu = new ContextMenuStrip();
        _trayMenu.BackColor = Color.FromArgb(28, 28, 30);
        _trayMenu.ForeColor = Color.FromArgb(240, 240, 240);
        _trayMenu.RenderMode = ToolStripRenderMode.System;

        var itemOpen = new ToolStripMenuItem("Open Settings");
        var itemScreenshot = new ToolStripMenuItem("Screenshot\tAlt+F1");
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
        if (_settingsForm != null && _settingsForm.Visible)
        {
            _settingsForm.Hide();
            return;
        }

        _settingsForm = new SettingsForm(this, _saveFolder, 15, 60);
        _settingsForm.OnSettingsChanged += (fps, seconds, folder) =>
        {
            _saveFolder = folder;
            InitializeCapture(fps, seconds);
        };
        _settingsForm.OnOverlayToggled += (visible) =>
        {
            _overlay.SetSystemInfoVisible(visible);
            if (visible) _overlay.Show();
        };
        _settingsForm.Show();
    }

    public void TakeScreenshot()
    {
        try
        {
            var path = _screenshotSaver.SaveScreenshot(_buffer);
            _trayIcon.ShowBalloonTip(2000, "EventCapture",
                $"Screenshot saved", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _trayIcon.ShowBalloonTip(2000, "EventCapture",
                $"Error: {ex.Message}", ToolTipIcon.Error);
        }
    }

    public async void SaveVideo()
    {
        try
        {
            var path = await _videoSaver.SaveVideoAsync(_buffer);
            _trayIcon.ShowBalloonTip(2000, "EventCapture",
                $"Video saved", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _trayIcon.ShowBalloonTip(2000, "EventCapture",
                $"Error: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void ToggleOverlay()
    {
        if (_overlay.Visible)
            _overlay.Hide();
        else
            _overlay.Show();
    }
    private void ExitApp()
    {
        _hotkeyManager?.Dispose();
        _capturer?.Stop();
        _trayIcon.Visible = false;
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

    private void MainForm_Load_1(object sender, EventArgs e)
    {

    }
}