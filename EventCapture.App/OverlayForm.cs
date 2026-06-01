using System.Runtime.InteropServices;
namespace EventCapture.App;

// Прозорий HUD-overlay для відображення системних показників.
public partial class OverlayForm : Form
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private static readonly Color TransparentColor = Color.FromArgb(1, 1, 1);
    private static readonly Color AccentColor = Color.FromArgb(0, 196, 160);

    private Label _cpuNameLabel = null!;
    private Label _cpuInfoLabel = null!;
    private Label _gpuNameLabel = null!;
    private Label _gpuInfoLabel = null!;
    private Label _ramNameLabel = null!;
    private Label _ramInfoLabel = null!;

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    public OverlayForm()
    {
        InitializeComponent();
        InitializeOverlayWindow();
        BuildSystemInfoLabels();
    }

    // Приховування overlay з Alt+Tab (...
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }
    // ...) Приховування overlay з Alt+Tab

    // Ініціалізація overlay (...
    private void InitializeOverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = TransparentColor;
        TransparencyKey = TransparentColor;

        var screen = Screen.PrimaryScreen!.Bounds;
        Location = new Point(screen.Left + 16, screen.Top + 16);
    }

    private void BuildSystemInfoLabels()
    {
        // Масштабування під DPI монітора
        float dpiScale = DeviceDpi / 96f;
        float fontSize = 9f;

        using var measureFont = new Font("Segoe UI", fontSize, FontStyle.Regular);
        int lineHeight = TextRenderer.MeasureText("Ag", measureFont).Height + 1;
        int groupGap = 3;

        int y = 4;

        _cpuNameLabel = MakeLabel("CPU: --", y, true, fontSize);
        y += lineHeight;
        _cpuInfoLabel = MakeLabel("Load: --%  Freq: -- GHz", y, false, fontSize);
        y += lineHeight + groupGap;

        _gpuNameLabel = MakeLabel("GPU: --", y, true, fontSize);
        y += lineHeight;
        _gpuInfoLabel = MakeLabel("Load: --%  VRAM: -- GB", y, false, fontSize);
        y += lineHeight + groupGap;

        _ramNameLabel = MakeLabel("RAM: --", y, true, fontSize);
        y += lineHeight;
        _ramInfoLabel = MakeLabel("Used: -- / -- GB", y, false, fontSize);

        Controls.AddRange(new Control[]
        {
            _cpuNameLabel, _cpuInfoLabel,
            _gpuNameLabel, _gpuInfoLabel,
            _ramNameLabel, _ramInfoLabel
        });

        UpdateOverlaySize();
    }

    private static Label MakeLabel(string text, int y, bool isHeader, float fontSize)
    {
        return new Label
        {
            Text = text,
            Location = new Point(8, y),
            AutoSize = true,
            ForeColor = AccentColor,
            BackColor = TransparentColor,
            Font = new Font(
                "Segoe UI",
                fontSize,
                isHeader ? FontStyle.Bold : FontStyle.Regular)
        };
    }
    // ...) Ініціалізація overlay

    // Розмір overlay (...
    private void UpdateOverlaySize()
    {
        var visible = Controls
            .OfType<Label>()
            .Where(l => l.Visible)
            .ToList();

        int width = visible.Count > 0
            ? visible.Max(l => l.PreferredWidth) + 24
            : 200;

        // Беремо нижній край останнього лейблу замість суми висот
        int height = visible.Count > 0
            ? visible.Max(l => l.Bottom) + 16
            : 1;

        Size = new Size(width, height);
    }
    // ...) Розмір overlay

    // Click-through режим (...
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
    // ...) Click-through режим

    // Оновлення системної інформації (...
    public void UpdateSystemInfo(
        float cpuLoad,
        float cpuFrequency,
        float gpuLoad,
        float gpuVram,
        float ramUsed,
        string cpuName,
        string gpuName,
        string ramType,
        int ramFrequency,
        float totalRam)
    {
        if (InvokeRequired)
        {
            Invoke(() => UpdateSystemInfo(
                cpuLoad, cpuFrequency, gpuLoad, gpuVram,
                ramUsed, cpuName, gpuName, ramType, ramFrequency, totalRam));
            return;
        }

        _cpuNameLabel.Text = $"CPU: {cpuName}";
        _cpuInfoLabel.Text = $"Load: {cpuLoad:F0}%  Freq: {cpuFrequency:F1} GHz";
        _gpuNameLabel.Text = $"GPU: {gpuName}";
        _gpuInfoLabel.Text = $"Load: {gpuLoad:F0}%  VRAM: {gpuVram:F1} GB";
        _ramNameLabel.Text = $"RAM: {ramType}-{ramFrequency}";
        _ramInfoLabel.Text = $"Used: {ramUsed:F1} / {totalRam:F1} GB";

        UpdateOverlaySize();
    }

    public void SetSystemInfoVisible(bool visible)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetSystemInfoVisible(visible));
            return;
        }

        _cpuNameLabel.Visible = visible;
        _cpuInfoLabel.Visible = visible;
        _gpuNameLabel.Visible = visible;
        _gpuInfoLabel.Visible = visible;
        _ramNameLabel.Visible = visible;
        _ramInfoLabel.Visible = visible;

        UpdateOverlaySize();
    }
    // ...) Оновлення системної інформації

    private void OverlayForm_Load(object sender, EventArgs e) { }
}