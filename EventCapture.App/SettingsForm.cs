namespace EventCapture.App;

public partial class SettingsForm : Form
{
    public event Action<int, int, string>? OnSettingsChanged;
    public event Action<bool>? OnOverlayToggled;

    public int BufferDurationSeconds { get; private set; }
    public int FrameRate { get; private set; }
    public string SaveFolder { get; private set; }

    private readonly MainForm _mainForm;
    private TrackBar _fpsSlider;
    private TrackBar _durationSlider;
    private Label _fpsValueLabel;
    private Label _durationValueLabel;
    private Label _folderValueLabel;
    private ToggleSwitch _toggleOverlay;

    public SettingsForm(MainForm mainForm, string saveFolder, int fps, int bufferSeconds)
    {
        _mainForm = mainForm;
        SaveFolder = saveFolder;
        FrameRate = fps;
        BufferDurationSeconds = bufferSeconds;
        InitializeComponent();
        BuildUI();
    }

    private void BuildUI()
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        int panelWidth = (int)(screen.Width * 0.22);

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(panelWidth, screen.Height);
        Location = new Point(screen.Right - panelWidth, 0);
        BackColor = Color.FromArgb(28, 28, 30);
        ForeColor = Color.FromArgb(240, 240, 240);
        Font = new Font("Segoe UI", 9);
        Padding = new Padding(16, 0, 16, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 16, 0, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(MakeTitle("EventCapture"));
        layout.Controls.Add(MakeSubLabel("● Buffer Active", Color.FromArgb(0, 196, 160)));
        layout.Controls.Add(MakeSeparator());

        layout.Controls.Add(MakePrimaryButton("Save Screenshot", () => _mainForm.TakeScreenshot()));
        layout.Controls.Add(MakeSubLabel("Alt + F1", Color.FromArgb(80, 80, 80)));
        layout.Controls.Add(MakePrimaryButton("Save Video", () => _mainForm.SaveVideo()));
        layout.Controls.Add(MakeSubLabel("Alt + F2", Color.FromArgb(80, 80, 80)));
        layout.Controls.Add(MakeSeparator());

        // FPS slider
        _fpsValueLabel = MakeSubLabel($"Frame Rate: {FrameRate} fps", Color.FromArgb(0, 196, 160));
        layout.Controls.Add(_fpsValueLabel);
        _fpsSlider = new TrackBar
        {
            Minimum = 0,
            Maximum = 2,
            Value = new[] { 15, 30, 60 }.ToList().IndexOf(FrameRate),
            TickFrequency = 1,
            TickStyle = TickStyle.Both,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(28, 28, 30),
            Height = 36,
            Margin = new Padding(0)
        };
        _fpsSlider.ValueChanged += (s, e) =>
        {
            int[] allowed = { 15, 30, 60 };
            FrameRate = allowed[_fpsSlider.Value];
            _fpsValueLabel.Text = $"Frame Rate: {FrameRate} fps";
        };
        layout.Controls.Add(_fpsSlider);
        layout.Controls.Add(MakeSeparator());

        // Duration slider
        _durationValueLabel = MakeSubLabel($"Buffer Duration: {BufferDurationSeconds} sec", Color.FromArgb(0, 196, 160));
        layout.Controls.Add(_durationValueLabel);
        _durationSlider = new TrackBar
        {
            Minimum = 0,
            Maximum = 4,
            Value = new[] { 30, 60, 120, 180, 300 }.ToList().IndexOf(BufferDurationSeconds),
            TickFrequency = 1,
            TickStyle = TickStyle.Both,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(28, 28, 30),
            Height = 36,
            Margin = new Padding(0)
        };
        _durationSlider.ValueChanged += (s, e) =>
        {
            int[] allowed = { 30, 60, 120, 180, 300 };
            BufferDurationSeconds = allowed[_durationSlider.Value];
            _durationValueLabel.Text = $"Buffer Duration: {BufferDurationSeconds} sec";
        };
        layout.Controls.Add(_durationSlider);
        layout.Controls.Add(MakeSeparator());

        // Save folder
        layout.Controls.Add(MakeSubLabel("Save Folder", Color.FromArgb(150, 150, 150)));
        _folderValueLabel = new Label
        {
            Text = SaveFolder,
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 36,
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(80, 80, 80),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 0, 2)
        };
        layout.Controls.Add(_folderValueLabel);
        layout.Controls.Add(MakeSecondaryButton("Browse...", () =>
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                SaveFolder = dlg.SelectedPath;
                _folderValueLabel.Text = SaveFolder;
            }
        }));
        layout.Controls.Add(MakeSeparator());

        // Toggle
        var toggleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 4)
        };
        toggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        toggleRow.Controls.Add(MakeSubLabel("Show System Info", Color.FromArgb(240, 240, 240)), 0, 0);
        _toggleOverlay = new ToggleSwitch { Anchor = AnchorStyles.Right };
        _toggleOverlay.CheckedChanged += (s, e) => OnOverlayToggled?.Invoke(_toggleOverlay.Checked);
        toggleRow.Controls.Add(_toggleOverlay, 1, 0);
        layout.Controls.Add(toggleRow);
        layout.Controls.Add(MakeSeparator());

        // Buttons
        layout.Controls.Add(MakePrimaryButton("Save Settings", () =>
        {
            OnSettingsChanged?.Invoke(FrameRate, BufferDurationSeconds, SaveFolder);
        }));
        layout.Controls.Add(MakeExitButton());

        Controls.Add(layout);
    }

    private Label MakeTitle(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 196, 160),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 4)
        };
    }

    private Label MakeSubLabel(string text, Color color)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 9),
            ForeColor = color,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 2)
        };
    }

    private Panel MakeSeparator()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            Height = 1,
            BackColor = Color.FromArgb(42, 42, 46),
            Margin = new Padding(0, 8, 0, 8)
        };
    }

    private Button MakePrimaryButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Height = 36,
            BackColor = Color.FromArgb(0, 196, 160),
            ForeColor = Color.FromArgb(10, 46, 40),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 4, 0, 4)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += (s, e) => onClick();
        return btn;
    }

    private Button MakeSecondaryButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Height = 36,
            BackColor = Color.FromArgb(42, 42, 46),
            ForeColor = Color.FromArgb(240, 240, 240),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 4, 0, 4)
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(58, 58, 62);
        btn.Click += (s, e) => onClick();
        return btn;
    }

    private Button MakeExitButton()
    {
        var btn = new Button
        {
            Text = "Exit",
            Dock = DockStyle.Fill,
            Height = 36,
            BackColor = Color.FromArgb(42, 42, 46),
            ForeColor = Color.FromArgb(220, 80, 80),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 4, 0, 4)
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(58, 58, 62);
        btn.Click += (s, e) => Hide();
        return btn;
    }
}