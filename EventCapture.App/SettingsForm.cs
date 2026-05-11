namespace EventCapture.App;

public partial class SettingsForm : Form
{
    public event Action<int, int, string>? OnSettingsChanged;
    public event Action<bool>? OnOverlayToggled;

    public int BufferDurationSeconds { get; private set; }
    public int FrameRate { get; private set; }
    public string SaveFolder { get; private set; }

    private readonly MainForm _mainForm;
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

        // FPS
        layout.Controls.Add(MakeSubLabel("Frame Rate", Color.FromArgb(150, 150, 150)));
        layout.Controls.Add(MakeArrowSelector(
            new[] { "15 fps", "30 fps", "60 fps" },
            new[] { 15, 30, 60 },
            FrameRate,
            val => {
                FrameRate = val;
                OnSettingsChanged?.Invoke(FrameRate, BufferDurationSeconds, SaveFolder);
            }));
        layout.Controls.Add(MakeSeparator());

        // Duration
        layout.Controls.Add(MakeSubLabel("Buffer Duration", Color.FromArgb(150, 150, 150)));
        layout.Controls.Add(MakeArrowSelector(
            new[] { "15 sec", "30 sec", "45 sec", "60 sec", "90 sec", "120 sec" },
            new[] { 15, 30, 45, 60, 90, 120 },
            BufferDurationSeconds,
            val => {
                BufferDurationSeconds = val;
                OnSettingsChanged?.Invoke(FrameRate, BufferDurationSeconds, SaveFolder);
            }));
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

    private Control MakeArrowSelector(string[] labels, int[] values, int currentValue, Action<int> onChange)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Height = 32,
            AutoSize = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 4)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));

        int currentIndex = Array.IndexOf(values, currentValue);
        if (currentIndex < 0) currentIndex = 0;

        var valueLabel = new Label
        {
            Text = labels[currentIndex],
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(0, 196, 160),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        var btnLeft = new Button
        {
            Text = "◀",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(0, 196, 160),
            Font = new Font("Segoe UI", 8),
            Cursor = Cursors.Hand,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        btnLeft.FlatAppearance.BorderSize = 0;
        btnLeft.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 42, 46);
        btnLeft.FlatAppearance.MouseDownBackColor = Color.FromArgb(58, 58, 62);

        var btnRight = new Button
        {
            Text = "▶",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(0, 196, 160),
            Font = new Font("Segoe UI", 8),
            Cursor = Cursors.Hand,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        btnRight.FlatAppearance.BorderSize = 0;
        btnRight.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 42, 46);
        btnRight.FlatAppearance.MouseDownBackColor = Color.FromArgb(58, 58, 62);

        btnLeft.Click += (s, e) =>
        {
            if (currentIndex > 0)
            {
                currentIndex--;
                valueLabel.Text = labels[currentIndex];
                onChange(values[currentIndex]);
            }
        };

        btnRight.Click += (s, e) =>
        {
            if (currentIndex < labels.Length - 1)
            {
                currentIndex++;
                valueLabel.Text = labels[currentIndex];
                onChange(values[currentIndex]);
            }
        };

        panel.Controls.Add(btnLeft, 0, 0);
        panel.Controls.Add(valueLabel, 1, 0);
        panel.Controls.Add(btnRight, 2, 0);

        return panel;
    }

    private void SettingsForm_Load(object sender, EventArgs e)
    {

    }
}