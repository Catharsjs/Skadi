namespace EventCapture.App;

public partial class MainForm : Form
{
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;

    public MainForm()
    {
        InitializeComponent();
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        InitializeTray();
        Hide();
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
        var itemSeparator = new ToolStripSeparator();
        var itemExit = new ToolStripMenuItem("Exit");

        itemOpen.Click += (s, e) => ShowSettings();
        itemScreenshot.Click += (s, e) => TakeScreenshot();
        itemSaveVideo.Click += (s, e) => SaveVideo();
        itemExit.Click += (s, e) => ExitApp();

        _trayMenu.Items.AddRange(new ToolStripItem[]
        {
            itemOpen, itemSeparator, itemScreenshot, itemSaveVideo,
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
        using var settings = new SettingsForm();
        settings.ShowDialog();
    }

    private void TakeScreenshot() { }
    private void SaveVideo() { }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        Application.Exit();
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

    private void MainForm_Load(object sender, EventArgs e)
    {

    }
}