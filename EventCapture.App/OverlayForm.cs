using System.Runtime.InteropServices;

namespace EventCapture.App;

public partial class OverlayForm : Form
{
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    private Label _bufferLabel;
    private Label _fpsLabel;
    private Label _cpuLabel;
    private Label _gpuLabel;
    private Label _ramLabel;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowSystemInfo { get; set; } = false;

    public OverlayForm()
    {
        InitializeComponent();
        BuildUI();
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

    private void BuildUI()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(1, 1, 1);
        TransparencyKey = Color.FromArgb(1, 1, 1);
        StartPosition = FormStartPosition.Manual;

        var screen = Screen.PrimaryScreen!.Bounds;
        Location = new Point(screen.Left + 16, screen.Top + 16);

        int y = 8;
        _bufferLabel = MakeLabel("Buffer: --:--", y); y += 22;
        _fpsLabel = MakeLabel("FPS: --", y); y += 22;
        _cpuLabel = MakeLabel("CPU: -- % @ -- GHz", y); y += 22;
        _gpuLabel = MakeLabel("GPU: -- % @ -- GHz  VRAM: -- GB", y); y += 22;
        _ramLabel = MakeLabel("RAM: -- GB", y);

        _cpuLabel.Visible = false;
        _gpuLabel.Visible = false;
        _ramLabel.Visible = false;

        Controls.AddRange(new Control[]
        {
            _bufferLabel, _fpsLabel, _cpuLabel, _gpuLabel, _ramLabel
        });
    }

    private Label MakeLabel(string text, int y) => new Label
    {
        Text = text,
        Location = new Point(8, y),
        AutoSize = true,
        ForeColor = Color.FromArgb(0, 196, 160),
        BackColor = Color.FromArgb(1, 1, 1),
        Font = new Font("Segoe UI", 9, FontStyle.Bold)
    };

    private void UpdateOverlaySize()
    {
        var visibleLabels = Controls.OfType<Label>().Where(l => l.Visible).ToList();
        int maxWidth = visibleLabels.Any()
            ? visibleLabels.Max(l => l.PreferredWidth) + 24
            : 200;
        int height = visibleLabels.Count * 22 + 16;
        Size = new Size(maxWidth, height);
    }

    private void MakeClickThrough()
    {
        int style = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MakeClickThrough();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateOverlaySize();
    }

    public void UpdateBuffer(int seconds)
    {
        if (InvokeRequired) { Invoke(() => UpdateBuffer(seconds)); return; }
        _bufferLabel.Text = $"Buffer: {seconds / 60:D2}:{seconds % 60:D2}";
        UpdateOverlaySize();
    }

    public void UpdateFps(int fps)
    {
        if (InvokeRequired) { Invoke(() => UpdateFps(fps)); return; }
        _fpsLabel.Text = $"FPS: {fps}";
        UpdateOverlaySize();
    }

    public void UpdateSystemInfo(float cpuLoad, float cpuFreq, float gpuLoad, float gpuFreq, float gpuVram, float ramUsed)
    {
        if (InvokeRequired) { Invoke(() => UpdateSystemInfo(cpuLoad, cpuFreq, gpuLoad, gpuFreq, gpuVram, ramUsed)); return; }
        _cpuLabel.Text = $"CPU: {cpuLoad:F0}% @ {cpuFreq:F1} GHz";
        _gpuLabel.Text = $"GPU: {gpuLoad:F0}% @ {gpuFreq:F1} GHz  VRAM: {gpuVram:F1} GB";
        _ramLabel.Text = $"RAM: {ramUsed:F1} GB";
        UpdateOverlaySize();
    }

    public void SetSystemInfoVisible(bool visible)
    {
        if (InvokeRequired) { Invoke(() => SetSystemInfoVisible(visible)); return; }
        _cpuLabel.Visible = visible;
        _gpuLabel.Visible = visible;
        _ramLabel.Visible = visible;
        UpdateOverlaySize();
    }

    private void OverlayForm_Load(object sender, EventArgs e) { }
}