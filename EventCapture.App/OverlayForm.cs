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
    private static extern int SetWindowLong(
        IntPtr hWnd,
        int nIndex,
        int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(
        IntPtr hWnd,
        int nIndex);

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
            var createParams = base.CreateParams;
            createParams.ExStyle |= WS_EX_TOOLWINDOW;
            return createParams;
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

        Location = new Point(
            screen.Left + 16,
            screen.Top + 16);
    }

    private void BuildSystemInfoLabels()
    {
        int lineHeight = 22;
        int groupGap = 6;
        int y = 8;

        _cpuNameLabel = CreateLabel("CPU: --", y, true);
        y += lineHeight;
        _cpuInfoLabel = CreateLabel("Load: --%  Freq: -- GHz", y, false);
        y += lineHeight + groupGap;
        _gpuNameLabel = CreateLabel("GPU: --", y, true);
        y += lineHeight;
        _gpuInfoLabel = CreateLabel("Load: --%  VRAM: -- GB", y, false);
        y += lineHeight + groupGap;
        _ramNameLabel = CreateLabel("RAM: --", y, true);
        y += lineHeight;
        _ramInfoLabel = CreateLabel("Used: -- / -- GB", y, false);

        Controls.AddRange(
            new Control[]
            {
                _cpuNameLabel,
                _cpuInfoLabel,
                _gpuNameLabel,
                _gpuInfoLabel,
                _ramNameLabel,
                _ramInfoLabel
            });
        UpdateOverlaySize();
    }

    private static Label CreateLabel(
        string text,
        int y,
        bool isHeader)
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
                9,
                isHeader ? FontStyle.Bold : FontStyle.Regular)
        };
    }
    // ...) Ініціалізація overlay

    // Розмір overlay (...
    private void UpdateOverlaySize()
    {
        var visibleLabels =
            Controls
                .OfType<Label>()
                .Where(label => label.Visible)
                .ToList();

        int width = visibleLabels.Count > 0
            ? visibleLabels.Max(label => label.PreferredWidth) + 24
            : 200;

        int height = visibleLabels.Count > 0
            ? visibleLabels.Sum(label => label.Height) + 24
            : 1;

        Size = new Size(width, height);
    }
    // ...) Розмір overlay

    // Click-through режим (...
    private void MakeClickThrough()
    {
        int style = GetWindowLong(
            Handle,
            GWL_EXSTYLE);

        SetWindowLong(
            Handle,
            GWL_EXSTYLE,
            style | WS_EX_TRANSPARENT | WS_EX_LAYERED);
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
            Invoke(() =>
                UpdateSystemInfo(
                    cpuLoad,
                    cpuFrequency,
                    gpuLoad,
                    gpuVram,
                    ramUsed,
                    cpuName,
                    gpuName,
                    ramType,
                    ramFrequency,
                    totalRam));

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
            Invoke(() =>
                SetSystemInfoVisible(visible));
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

    private void OverlayForm_Load(
        object sender,
        EventArgs e)
    {
    }
}