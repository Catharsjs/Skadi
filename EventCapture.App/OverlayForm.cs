using System.Runtime.InteropServices;

namespace EventCapture.App;

// HUD overlay у лівому верхньому куті екрану
// Прозорий фон, не перехоплює кліки миші (WS_EX_TRANSPARENT)
public partial class OverlayForm : Form
{
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    private Label _cpuNameLabel = null!;
    private Label _cpuInfoLabel = null!;
    private Label _gpuNameLabel = null!;
    private Label _gpuInfoLabel = null!;
    private Label _ramNameLabel = null!;
    private Label _ramInfoLabel = null!;

    public OverlayForm()
    {
        InitializeComponent();
        BuildUI();
    }

    // Приховуємо з Alt+Tab
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
        BackColor = Color.FromArgb(1, 1, 1);       // майже чорний — використовується як TransparencyKey
        TransparencyKey = Color.FromArgb(1, 1, 1);
        StartPosition = FormStartPosition.Manual;

        var screen = Screen.PrimaryScreen!.Bounds;
        Location = new Point(screen.Left + 16, screen.Top + 16);

        int lineHeight = 22;
        int groupGap = 6;
        int y = 8;

        _cpuNameLabel = MakeLabel("CPU: --", y, true); y += lineHeight;
        _cpuInfoLabel = MakeLabel("Load: --%  Freq: -- GHz", y, false); y += lineHeight + groupGap;
        _gpuNameLabel = MakeLabel("GPU: --", y, true); y += lineHeight;
        _gpuInfoLabel = MakeLabel("Load: --%  VRAM: -- GB", y, false); y += lineHeight + groupGap;
        _ramNameLabel = MakeLabel("RAM: --", y, true); y += lineHeight;
        _ramInfoLabel = MakeLabel("Used: -- / -- GB", y, false);

        Controls.AddRange(new Control[]
        {
            _cpuNameLabel, _cpuInfoLabel,
            _gpuNameLabel, _gpuInfoLabel,
            _ramNameLabel, _ramInfoLabel
        });
    }

    private Label MakeLabel(string text, int y, bool isHeader) => new Label
    {
        Text = text,
        Location = new Point(8, y),
        AutoSize = true,
        ForeColor = Color.FromArgb(0, 196, 160),
        BackColor = Color.FromArgb(1, 1, 1),
        Font = new Font("Segoe UI", 9, isHeader ? FontStyle.Bold : FontStyle.Regular)
    };

    private void UpdateOverlaySize()
    {
        var visibleLabels = Controls.OfType<Label>().Where(l => l.Visible).ToList();
        int maxWidth = visibleLabels.Any()
            ? visibleLabels.Max(l => l.PreferredWidth) + 24
            : 200;
        int height = visibleLabels.Sum(l => l.Height) + 24;
        Size = new Size(maxWidth, height);
    }

    // Робимо форму прозорою для кліків — події миші проходять крізь неї
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

    // Оновлюється раз на секунду з MainForm.StartHardwareMonitor
    public void UpdateSystemInfo(float cpuLoad, float cpuFreq, float gpuLoad,
        float gpuVram, float ramUsed, string cpuName, string gpuName,
        string ramType, int ramFreq, float totalRam)
    {
        if (InvokeRequired)
        {
            Invoke(() => UpdateSystemInfo(cpuLoad, cpuFreq, gpuLoad, gpuVram,
                ramUsed, cpuName, gpuName, ramType, ramFreq, totalRam));
            return;
        }
        _cpuNameLabel.Text = $"CPU: {cpuName}";
        _cpuInfoLabel.Text = $"Load: {cpuLoad:F0}%  Freq: {cpuFreq:F1} GHz";
        _gpuNameLabel.Text = $"GPU: {gpuName}";
        _gpuInfoLabel.Text = $"Load: {gpuLoad:F0}%  VRAM: {gpuVram:F1} GB";
        _ramNameLabel.Text = $"RAM: {ramType}-{ramFreq}";
        _ramInfoLabel.Text = $"Used: {ramUsed:F1} / {totalRam:F1} GB";
        UpdateOverlaySize();
    }

    public void SetSystemInfoVisible(bool visible)
    {
        if (InvokeRequired) { Invoke(() => SetSystemInfoVisible(visible)); return; }
        _cpuNameLabel.Visible = visible;
        _cpuInfoLabel.Visible = visible;
        _gpuNameLabel.Visible = visible;
        _gpuInfoLabel.Visible = visible;
        _ramNameLabel.Visible = visible;
        _ramInfoLabel.Visible = visible;
        UpdateOverlaySize();
    }

    private void OverlayForm_Load(object sender, EventArgs e) { }
}