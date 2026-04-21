namespace EventCapture.App;

public partial class SettingsForm : Form
{
    public int BufferDurationSeconds { get; private set; } = 60;
    public int FrameRate { get; private set; } = 15;
    public string SaveFolder { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EventCapture");

    private TrackBar _fpsSlider;
    private TrackBar _durationSlider;
    private Label _fpsValue;
    private Label _durationValue;
    private Label _folderValue;

    public SettingsForm()
    {
        InitializeComponent();
        BuildUI();
    }

    private void BuildUI()
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        int panelWidth = (int)(screen.Width * 0.15);

        Text = "EventCapture";
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(panelWidth, screen.Height);
        Location = new Point(screen.Right - panelWidth, 0);

        BackColor = Color.FromArgb(28, 28, 30);
        ForeColor = Color.FromArgb(240, 240, 240);
        Font = new Font("Segoe UI", 9);

        int pad = 16;
        int y = 20;
        int w = panelWidth - pad * 2;

        // Header
        var lblTitle = MakeLabel("EventCapture", pad, y, bold: true, size: 12);
        lblTitle.ForeColor = Color.FromArgb(0, 196, 160);
        y += 30;

        var lblStatus = MakeLabel("Buffer Active", pad, y);
        lblStatus.ForeColor = Color.FromArgb(96, 96, 96);
        y += 30;

        // Separator
        y += 10;
        var sep1 = new Panel { Location = new Point(pad, y), Size = new Size(w, 1), BackColor = Color.FromArgb(42, 42, 46) };
        y += 16;

        // Screenshot button
        var btnScreenshot = MakeButton("Screenshot  Alt+F1", pad, y, w);
        btnScreenshot.BackColor = Color.FromArgb(0, 196, 160);
        btnScreenshot.ForeColor = Color.FromArgb(10, 46, 40);
        btnScreenshot.Click += (s, e) => TakeScreenshot();
        y += 42;

        // Save Video button
        var btnSaveVideo = MakeButton("Save Video  Alt+F2", pad, y, w);
        btnSaveVideo.Click += (s, e) => SaveVideo();
        y += 42;

        y += 10;
        var sep2 = new Panel { Location = new Point(pad, y), Size = new Size(w, 1), BackColor = Color.FromArgb(42, 42, 46) };
        y += 16;

        // FPS
        var lblFps = MakeLabel("Frame Rate", pad, y);
        _fpsValue = MakeLabel($"{FrameRate} fps", pad + w - 50, y);
        _fpsValue.ForeColor = Color.FromArgb(0, 196, 160);
        y += 22;
        _fpsSlider = new TrackBar
        {
            Minimum = 15,
            Maximum = 60,
            Value = FrameRate,
            TickFrequency = 5,
            Location = new Point(pad, y),
            Width = w,
            BackColor = Color.FromArgb(28, 28, 30)
        };
        _fpsSlider.ValueChanged += (s, e) =>
        {
            FrameRate = _fpsSlider.Value;
            _fpsValue.Text = $"{FrameRate} fps";
        };
        y += 45;

        // Duration
        var lblDuration = MakeLabel("Buffer Duration", pad, y);
        _durationValue = MakeLabel($"{BufferDurationSeconds} sec", pad + w - 50, y);
        _durationValue.ForeColor = Color.FromArgb(0, 196, 160);
        y += 22;
        _durationSlider = new TrackBar
        {
            Minimum = 30,
            Maximum = 300,
            Value = BufferDurationSeconds,
            TickFrequency = 30,
            Location = new Point(pad, y),
            Width = w,
            BackColor = Color.FromArgb(28, 28, 30)
        };
        _durationSlider.ValueChanged += (s, e) =>
        {
            BufferDurationSeconds = _durationSlider.Value;
            _durationValue.Text = $"{BufferDurationSeconds} sec";
        };
        y += 45;

        y += 10;
        var sep3 = new Panel { Location = new Point(pad, y), Size = new Size(w, 1), BackColor = Color.FromArgb(42, 42, 46) };
        y += 16;

        // Save folder
        var lblFolder = MakeLabel("Save Folder", pad, y);
        y += 22;
        _folderValue = MakeLabel(SaveFolder, pad, y);
        _folderValue.ForeColor = Color.FromArgb(96, 96, 96);
        _folderValue.Width = w;
        _folderValue.AutoEllipsis = true;
        y += 22;
        var btnBrowse = MakeButton("Browse...", pad, y, w);
        btnBrowse.Click += (s, e) =>
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                SaveFolder = dlg.SelectedPath;
                _folderValue.Text = SaveFolder;
            }
        };
        y += 42;

        y += 10;
        var sep4 = new Panel { Location = new Point(pad, y), Size = new Size(w, 1), BackColor = Color.FromArgb(42, 42, 46) };
        y += 16;

        // Exit
        var btnExit = MakeButton("Exit", pad, y, w);
        btnExit.ForeColor = Color.FromArgb(220, 80, 80);
        btnExit.Click += (s, e) =>
        {
            Application.Exit();
        };

        Controls.AddRange(new Control[]
        {
            lblTitle, lblStatus, sep1,
            btnScreenshot, btnSaveVideo, sep2,
            lblFps, _fpsValue, _fpsSlider,
            lblDuration, _durationValue, _durationSlider, sep3,
            lblFolder, _folderValue, btnBrowse, sep4,
            btnExit
        });

        // Close on click outside
        Deactivate += (s, e) => Hide();
    }

    private void TakeScreenshot() { }
    private void SaveVideo() { }

    private Label MakeLabel(string text, int x, int y, bool bold = false, float size = 9) => new Label
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    private Button MakeButton(string text, int x, int y, int w) => new Button
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(w, 32),
        BackColor = Color.FromArgb(42, 42, 46),
        ForeColor = Color.FromArgb(240, 240, 240),
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(4, 0, 0, 0),
        Cursor = Cursors.Hand
    };
}