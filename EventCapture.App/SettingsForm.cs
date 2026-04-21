namespace EventCapture.App;

public partial class SettingsForm : Form
{
    public event Action<int, int, string>? OnSettingsChanged;
    private readonly MainForm _mainForm;
    public int BufferDurationSeconds { get; private set; }
    public int FrameRate { get; private set; }
    public string SaveFolder { get; private set; }

    private TrackBar _fpsSlider;
    private TrackBar _durationSlider;
    private Label _fpsValue;
    private Label _durationValue;
    private Label _folderValue;

    public SettingsForm(MainForm mainForm, string saveFolder, int fps, int bufferSeconds)
    {
        _mainForm = mainForm;
        BufferDurationSeconds = bufferSeconds;
        FrameRate = fps;
        SaveFolder = saveFolder;
        InitializeComponent();
        BuildUI();
    }

    private void BuildUI()
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        int panelWidth = (int)(screen.Width * 0.18);

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

        int pad = 20;
        int y = 24;
        int w = panelWidth - pad * 2;

        // Header
        var lblTitle = MakeLabel("EventCapture", pad, y, bold: true, size: 13);
        lblTitle.ForeColor = Color.FromArgb(0, 196, 160);
        y += 28;

        var lblStatus = MakeLabel("● Buffer Active", pad, y, size: 9);
        lblStatus.ForeColor = Color.FromArgb(0, 196, 160);
        y += 32;

        AddSeparator(pad, y, w); y += 20;

        // Buttons
        var btnScreenshot = MakeButton("Screenshot", pad, y, w, primary: true);
        var lblHk1 = MakeLabel("Alt + F1", pad, y + 36, size: 8);
        lblHk1.ForeColor = Color.FromArgb(80, 80, 80);
        y += 60;

        var btnSaveVideo = MakeButton("Save Video", pad, y, w);
        var lblHk2 = MakeLabel("Alt + F2", pad, y + 36, size: 8);
        lblHk2.ForeColor = Color.FromArgb(80, 80, 80);
        y += 60;

        AddSeparator(pad, y, w); y += 20;

        // FPS
        var lblFps = MakeLabel("Frame Rate", pad, y, size: 9);
        _fpsValue = MakeLabel($"{FrameRate} fps", pad + w - 45, y, size: 9);
        _fpsValue.ForeColor = Color.FromArgb(0, 196, 160);
        y += 22;
        _fpsSlider = new TrackBar
        {
            Minimum = 15,
            Maximum = 60,
            Value = FrameRate,
            TickFrequency = 5,
            Location = new Point(pad - 4, y),
            Width = w + 8,
            BackColor = Color.FromArgb(28, 28, 30),
            Height = 32
        };
        _fpsSlider.ValueChanged += (s, e) =>
        {
            FrameRate = _fpsSlider.Value;
            _fpsValue.Text = $"{FrameRate} fps";
        };
        y += 38;

        AddSeparator(pad, y, w); y += 20;

        // Duration
        var lblDuration = MakeLabel("Buffer Duration", pad, y, size: 9);
        _durationValue = MakeLabel($"{BufferDurationSeconds} sec", pad + w - 45, y, size: 9);
        _durationValue.ForeColor = Color.FromArgb(0, 196, 160);
        y += 22;
        _durationSlider = new TrackBar
        {
            Minimum = 30,
            Maximum = 300,
            Value = BufferDurationSeconds,
            TickFrequency = 30,
            Location = new Point(pad - 4, y),
            Width = w + 8,
            BackColor = Color.FromArgb(28, 28, 30),
            Height = 32
        };
        _durationSlider.ValueChanged += (s, e) =>
        {
            BufferDurationSeconds = _durationSlider.Value;
            _durationValue.Text = $"{BufferDurationSeconds} sec";
        };
        y += 38;

        AddSeparator(pad, y, w); y += 20;

        // Save folder
        var lblFolder = MakeLabel("Save Folder", pad, y, size: 9);
        y += 22;
        _folderValue = new Label
        {
            Text = SaveFolder,
            Location = new Point(pad, y),
            Size = new Size(w, 32),
            ForeColor = Color.FromArgb(80, 80, 80),
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 8)
        };
        y += 36;
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
        y += 48;

        AddSeparator(pad, y, w); y += 20;

        // Save Settings
        var btnSave = MakeButton("Save Settings", pad, y, w, primary: true);
        btnSave.Click += (s, e) =>
        {
            OnSettingsChanged?.Invoke(FrameRate, BufferDurationSeconds, SaveFolder);
            Hide();
        };
        y += 48;

        // Exit
        var btnExit = MakeButton("Exit", pad, y, w);
        btnExit.ForeColor = Color.FromArgb(220, 80, 80);
        btnExit.Click += (s, e) => Application.Exit();

        // Wire up main buttons
        btnScreenshot.Click += (s, e) => TakeScreenshot();
        btnSaveVideo.Click += (s, e) => SaveVideo();

        Controls.AddRange(new Control[]
{
    lblTitle, lblStatus,
    btnScreenshot, lblHk1,
    btnSaveVideo, lblHk2,
    lblFps, _fpsValue, _fpsSlider,
    lblDuration, _durationValue, _durationSlider,
    lblFolder, _folderValue, btnBrowse,
    btnSave, btnExit
});

        Deactivate += (s, e) => Hide();
    }

    private void AddSeparator(int x, int y, int w)
    {
        Controls.Add(new Panel
        {
            Location = new Point(x, y),
            Size = new Size(w, 1),
            BackColor = Color.FromArgb(42, 42, 46)
        });
    }

    private void TakeScreenshot() => _mainForm.TakeScreenshot();
    private void SaveVideo() => _mainForm.SaveVideo();

    private Label MakeLabel(string text, int x, int y, bool bold = false, float size = 9) => new Label
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    private Button MakeButton(string text, int x, int y, int w, bool primary = false) => new Button
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(w, 34),
        BackColor = primary ? Color.FromArgb(0, 196, 160) : Color.FromArgb(42, 42, 46),
        ForeColor = primary ? Color.FromArgb(10, 46, 40) : Color.FromArgb(240, 240, 240),
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9),
        Cursor = Cursors.Hand
    };

    private void SettingsForm_Load(object sender, EventArgs e)
    {

    }
}