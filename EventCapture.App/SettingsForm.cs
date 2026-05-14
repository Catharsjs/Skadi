using EventCapture.Core.Capture;
namespace EventCapture.App;

public partial class SettingsForm : Form
{
    // ─── Події ───────────────────────────────────────────────────────────
    // OnSettingsChanged — спрацьовує при будь-якій зміні налаштувань
    // OnHotkeyInputStarted/Finished — для тимчасового зняття хоткеїв
    public event Action<int, int, string, string, string, string, string, bool, string?, bool, string?>? OnSettingsChanged;
    public event Action<bool>? OnOverlayToggled;
    public event Action? OnHotkeyInputStarted;
    public event Action? OnHotkeyInputFinished;
    private Label _labelHotkeyScreenshot = null!;
    private Label _labelHotkeySaveVideo = null!;
    private bool _recordSystem = false;
    private bool _recordMic = false;
    private string? _systemDeviceId = null;
    private string? _micDeviceId = null;
    private Button _btnSystemDevice = null!;
    private Button _btnMicDevice = null!;
    public int BufferDurationSeconds { get; private set; }
    public int FrameRate { get; private set; }
    public string SaveFolder { get; private set; }

    private readonly MainForm _mainForm;
    private string _currentResolution = "Native";

    // Посилання на кнопки хоткеїв для скидання при конфлікті
    private Button? _btnHotkeyScreenshot;
    private Button? _btnHotkeySaveVideo;
    private Button? _btnHotkeyToggleUI;
    private string _hotkeyScreenshot;
    private string _hotkeySaveVideo;
    private string _hotkeyToggleUI;

    private Label _folderValueLabel = null!;
    private ToggleSwitch _toggleOverlay = null!;

    public SettingsForm(MainForm mainForm, string saveFolder, int fps, int bufferSeconds,
     string resolution = "Native", string hotkeyScreenshot = "Alt+F1",
     string hotkeySaveVideo = "Alt+F2", string hotkeyToggleUI = "Alt+F3",
     bool recordSystem = false, string? systemDeviceId = null,
     bool recordMic = false, string? micDeviceId = null)
    {
        _mainForm = mainForm;
        SaveFolder = saveFolder;
        FrameRate = fps;
        BufferDurationSeconds = bufferSeconds;
        _currentResolution = resolution;
        _hotkeyScreenshot = hotkeyScreenshot;
        _hotkeySaveVideo = hotkeySaveVideo;
        _hotkeyToggleUI = hotkeyToggleUI;
        _recordSystem = recordSystem;
        _systemDeviceId = systemDeviceId;
        _recordMic = recordMic;
        _micDeviceId = micDeviceId;
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

    // ─── Побудова UI ──────────────────────────────────────────────────────
    private void BuildUI()
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        int panelWidth = (int)(screen.Width * 0.22);

        FormBorderStyle = FormBorderStyle.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
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
        _labelHotkeyScreenshot = MakeSubLabel(_hotkeyScreenshot, Color.FromArgb(80, 80, 80));
        layout.Controls.Add(_labelHotkeyScreenshot);
        layout.Controls.Add(MakePrimaryButton("Save Video", () => _mainForm.SaveVideo()));
        _labelHotkeySaveVideo = MakeSubLabel(_hotkeySaveVideo, Color.FromArgb(80, 80, 80));
        layout.Controls.Add(_labelHotkeySaveVideo);
        layout.Controls.Add(MakeSeparator());

        // ─── Налаштування відео ───────────────────────────────────────────
        layout.Controls.Add(MakeSubLabel("Resolution", Color.FromArgb(150, 150, 150)));
        layout.Controls.Add(MakeResolutionSelector());
        layout.Controls.Add(MakeSeparator());

        layout.Controls.Add(MakeSubLabel("Frame Rate", Color.FromArgb(150, 150, 150)));
        layout.Controls.Add(MakeArrowSelector(
            new[] { "15 fps", "30 fps", "60 fps" },
            new[] { 15, 30, 60 },
            FrameRate,
            val => { FrameRate = val; InvokeSettingsChanged(); }));
        layout.Controls.Add(MakeSeparator());

        layout.Controls.Add(MakeSubLabel("Buffer Duration", Color.FromArgb(150, 150, 150)));
        layout.Controls.Add(MakeArrowSelector(
            new[] { "15 sec", "30 sec", "45 sec", "60 sec", "90 sec", "120 sec" },
            new[] { 15, 30, 45, 60, 90, 120 },
            BufferDurationSeconds,
            val => { BufferDurationSeconds = val; InvokeSettingsChanged(); }));
        layout.Controls.Add(MakeSeparator());
        // ─── Налаштування звуку ───────────────────────────────────────────────
        layout.Controls.Add(MakeSubLabel("Audio", Color.FromArgb(150, 150, 150)));
        layout.Controls.Add(MakeAudioRow("Record System Audio", isSystem: true));
        layout.Controls.Add(MakeAudioRow("Record Microphone", isSystem: false));
        layout.Controls.Add(MakeSeparator());

        // ─── Папка збереження ─────────────────────────────────────────────
        layout.Controls.Add(MakeSubLabel("Save Folder", Color.FromArgb(150, 150, 150)));
        _folderValueLabel = new Label
        {
            Text = SaveFolder,
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 36,
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(0, 196, 160),
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
                InvokeSettingsChanged();
            }
        }));
        layout.Controls.Add(MakeSeparator());

        // ─── HUD overlay тумблер ──────────────────────────────────────────
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

        // ─── Налаштування хоткеїв ─────────────────────────────────────────
        // При конфлікті — дублікат скидається в "Unassigned"
        layout.Controls.Add(MakeSubLabel("Hot Keys", Color.FromArgb(150, 150, 150)));
        layout.Controls.Add(MakeHotkeyRow("Save Screenshot", _hotkeyScreenshot, val => {
            _hotkeyScreenshot = val;
            if (_hotkeySaveVideo == val) { _hotkeySaveVideo = "Unassigned"; _btnHotkeySaveVideo!.Text = "Unassigned"; }
            if (_hotkeyToggleUI == val) { _hotkeyToggleUI = "Unassigned"; _btnHotkeyToggleUI!.Text = "Unassigned"; }
            InvokeSettingsChanged();
        }, btn => _btnHotkeyScreenshot = btn));

        layout.Controls.Add(MakeHotkeyRow("Save Video", _hotkeySaveVideo, val => {
            _hotkeySaveVideo = val;
            if (_hotkeyScreenshot == val) { _hotkeyScreenshot = "Unassigned"; _btnHotkeyScreenshot!.Text = "Unassigned"; }
            if (_hotkeyToggleUI == val) { _hotkeyToggleUI = "Unassigned"; _btnHotkeyToggleUI!.Text = "Unassigned"; }
            InvokeSettingsChanged();
        }, btn => _btnHotkeySaveVideo = btn));

        layout.Controls.Add(MakeHotkeyRow("Toggle UI", _hotkeyToggleUI, val => {
            _hotkeyToggleUI = val;
            if (_hotkeyScreenshot == val) { _hotkeyScreenshot = "Unassigned"; _btnHotkeyScreenshot!.Text = "Unassigned"; }
            if (_hotkeySaveVideo == val) { _hotkeySaveVideo = "Unassigned"; _btnHotkeySaveVideo!.Text = "Unassigned"; }
            InvokeSettingsChanged();
        }, btn => _btnHotkeyToggleUI = btn));

        layout.Controls.Add(MakeSeparator());
        layout.Controls.Add(MakeExitButton());

        Controls.Add(layout);
    }

    // Хелпер щоб не дублювати довгий виклик OnSettingsChanged
    private void InvokeSettingsChanged()
    {
        _labelHotkeyScreenshot.Text = _hotkeyScreenshot;
        _labelHotkeySaveVideo.Text = _hotkeySaveVideo;
        OnSettingsChanged?.Invoke(FrameRate, BufferDurationSeconds, SaveFolder,
            _currentResolution, _hotkeyScreenshot, _hotkeySaveVideo, _hotkeyToggleUI,
            _recordSystem, _systemDeviceId, _recordMic, _micDeviceId);
    }

    private Label MakeTitle(string text) => new Label
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 13, FontStyle.Bold),
        ForeColor = Color.FromArgb(0, 196, 160),
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 4)
    };

    private Label MakeSubLabel(string text, Color color) => new Label
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 9),
        ForeColor = color,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 2, 0, 2)
    };

    private Panel MakeSeparator() => new Panel
    {
        Dock = DockStyle.Fill,
        Height = 1,
        BackColor = Color.FromArgb(42, 42, 46),
        Margin = new Padding(0, 8, 0, 8)
    };

    private Button MakePrimaryButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Height = 42,
            BackColor = Color.FromArgb(0, 196, 160),
            ForeColor = Color.FromArgb(10, 46, 40),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 4, 0, 4),
            TextAlign = ContentAlignment.MiddleCenter
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

    private Control MakeAudioRow(string label, bool isSystem)
    {
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 4)
        };

        var toggleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            BackColor = Color.Transparent
        };
        toggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        toggleRow.Controls.Add(MakeSubLabel(label, Color.FromArgb(240, 240, 240)), 0, 0);

        var toggle = new ToggleSwitch { Anchor = AnchorStyles.Right };
        toggleRow.Controls.Add(toggle, 1, 0);

        var btn = new Button
        {
            Dock = DockStyle.Fill,
            Height = 36,
            BackColor = Color.FromArgb(28, 28, 30),
            ForeColor = Color.FromArgb(150, 150, 150),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9),
            Cursor = Cursors.Default,
            Margin = new Padding(0, 4, 0, 0),
            Enabled = false,
            UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(42, 42, 46);

        btn.Paint += (s, e) =>
        {
            e.Graphics.Clear(btn.BackColor);
            var textColor = btn.Enabled
                ? Color.FromArgb(0, 196, 160)
                : Color.FromArgb(150, 150, 150);
            TextRenderer.DrawText(e.Graphics, btn.Text, btn.Font,
                btn.ClientRectangle, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        // Показуємо дефолтний пристрій одразу
        try
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var defaultDevice = isSystem
                ? enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia)
                : enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Multimedia);
            btn.Text = defaultDevice.FriendlyName;
            if (isSystem) _systemDeviceId = defaultDevice.ID;
            else _micDeviceId = defaultDevice.ID;
        }
        catch { btn.Text = "Select device..."; }

        if (isSystem) _btnSystemDevice = btn;
        else _btnMicDevice = btn;

        // Відновлюємо збережений стан
        bool initialState = isSystem ? _recordSystem : _recordMic;
        if (initialState)
        {
            toggle.Checked = true;
            btn.Enabled = true;
            btn.Cursor = Cursors.Hand;
            btn.BackColor = Color.FromArgb(42, 42, 46);
        }

        // Відновлюємо збережений пристрій
        string? savedId = isSystem ? _systemDeviceId : _micDeviceId;
        if (savedId != null)
        {
            try
            {
                var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var device = enumerator.GetDevice(savedId);
                btn.Text = device.FriendlyName;
                if (isSystem) _systemDeviceId = savedId;
                else _micDeviceId = savedId;
            }
            catch { }
        }

        toggle.CheckedChanged += (s, e) =>
        {
            if (isSystem) _recordSystem = toggle.Checked;
            else _recordMic = toggle.Checked;

            btn.Enabled = toggle.Checked;
            btn.ForeColor = Color.FromArgb(0, 196, 160);
            btn.Cursor = toggle.Checked ? Cursors.Hand : Cursors.Default;
            btn.BackColor = toggle.Checked
                ? Color.FromArgb(42, 42, 46)
                : Color.FromArgb(28, 28, 30);

            InvokeSettingsChanged();
        };

        btn.Click += (s, e) =>
        {
            if (!(isSystem ? _recordSystem : _recordMic)) return;

            var devices = isSystem
                ? AudioRecorder.GetOutputDevices()
                : AudioRecorder.GetInputDevices();

            var menu = new ContextMenuStrip();
            menu.BackColor = Color.FromArgb(42, 42, 46);
            menu.ForeColor = Color.FromArgb(240, 240, 240);
            menu.RenderMode = ToolStripRenderMode.System;

            foreach (var (id, name) in devices)
            {
                var item = new ToolStripMenuItem(name);
                item.Click += (_, __) =>
                {
                    if (isSystem) _systemDeviceId = id;
                    else _micDeviceId = id;
                    btn.Text = name;
                };
                menu.Items.Add(item);
            }

            menu.Show(btn, new Point(0, btn.Height));
        };

        container.Controls.Add(toggleRow);
        container.Controls.Add(btn);

        return container;
    }

    // ─── Рядок хоткея ─────────────────────────────────────────────────────
    // При кліку переходить в режим очікування вводу
    // Escape — скасування, одиночні модифікатори — заборонені (підсвічує червоним)
    private Control MakeHotkeyRow(string label, string currentHotkey, Action<string> onChange, Action<Button> registerBtn)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Height = 46,
            AutoSize = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 4)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));

        var nameLabel = new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(190, 190, 190),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9)
        };

        var hotkeyBtn = new Button
        {
            Text = currentHotkey,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(42, 42, 46),
            ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
        hotkeyBtn.FlatAppearance.BorderColor = Color.FromArgb(58, 58, 62);

        bool isListening = false;

        void StartListening()
        {
            if (isListening) return;
            isListening = true;
            OnHotkeyInputStarted?.Invoke();
            hotkeyBtn.Text = "Press keys...";
            hotkeyBtn.ForeColor = Color.FromArgb(240, 240, 240);
            hotkeyBtn.BackColor = Color.FromArgb(50, 50, 60);

            string pendingHotkey = string.Empty;
            KeyEventHandler? keyDownHandler = null;
            KeyEventHandler? keyUpHandler = null;

            keyDownHandler = (s2, e2) =>
            {
                e2.SuppressKeyPress = true;

                if (e2.KeyCode == Keys.Escape)
                {
                    StopListening(currentHotkey, false);
                    return;
                }

                var parts = new List<string>();
                if (e2.Modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
                if (e2.Modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
                if (e2.Modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");

                var key = e2.KeyCode;
                bool isModifierOnly =
                    key == Keys.Alt || key == Keys.Menu ||
                    key == Keys.Control || key == Keys.ControlKey ||
                    key == Keys.LControlKey || key == Keys.RControlKey ||
                    key == Keys.Shift || key == Keys.ShiftKey ||
                    key == Keys.LShiftKey || key == Keys.RShiftKey ||
                    key == Keys.LMenu || key == Keys.RMenu ||
                    key == Keys.LWin || key == Keys.RWin;

                if (!isModifierOnly)
                {
                    string keyName = key switch
                    {
                        Keys.D0 => "0",
                        Keys.D1 => "1",
                        Keys.D2 => "2",
                        Keys.D3 => "3",
                        Keys.D4 => "4",
                        Keys.D5 => "5",
                        Keys.D6 => "6",
                        Keys.D7 => "7",
                        Keys.D8 => "8",
                        Keys.D9 => "9",
                        Keys.OemPeriod => ".",
                        Keys.Oemcomma => ",",
                        Keys.OemMinus => "-",
                        Keys.Oemplus => "=",
                        _ => key.ToString()
                    };
                    parts.Add(keyName);
                    pendingHotkey = string.Join("+", parts);
                }
                else
                {
                    pendingHotkey = string.Empty;
                }
            };

            keyUpHandler = (s2, e2) =>
            {
                bool isModifierOnly =
                    e2.KeyCode == Keys.Alt || e2.KeyCode == Keys.Menu ||
                    e2.KeyCode == Keys.Control || e2.KeyCode == Keys.ControlKey ||
                    e2.KeyCode == Keys.LControlKey || e2.KeyCode == Keys.RControlKey ||
                    e2.KeyCode == Keys.Shift || e2.KeyCode == Keys.ShiftKey ||
                    e2.KeyCode == Keys.LShiftKey || e2.KeyCode == Keys.RShiftKey ||
                    e2.KeyCode == Keys.LMenu || e2.KeyCode == Keys.RMenu ||
                    e2.KeyCode == Keys.LWin || e2.KeyCode == Keys.RWin;

                if (isModifierOnly)
                {
                    if (string.IsNullOrEmpty(pendingHotkey))
                    {
                        hotkeyBtn.KeyDown -= keyDownHandler;
                        hotkeyBtn.KeyUp -= keyUpHandler;
                        isListening = false;
                        hotkeyBtn.Text = "Press keys...";
                        hotkeyBtn.BackColor = Color.FromArgb(80, 30, 30);
                        hotkeyBtn.ForeColor = Color.FromArgb(240, 240, 240);
                        Task.Delay(800).ContinueWith(_ =>
                            hotkeyBtn.Invoke(() => { OnHotkeyInputStarted?.Invoke(); StartListening(); }));
                    }
                    return;
                }

                if (string.IsNullOrEmpty(pendingHotkey))
                {
                    hotkeyBtn.KeyDown -= keyDownHandler;
                    hotkeyBtn.KeyUp -= keyUpHandler;
                    isListening = false;
                    hotkeyBtn.Text = "Press keys...";
                    hotkeyBtn.BackColor = Color.FromArgb(80, 30, 30);
                    hotkeyBtn.ForeColor = Color.FromArgb(240, 240, 240);
                    Task.Delay(800).ContinueWith(_ =>
                        hotkeyBtn.Invoke(() => { OnHotkeyInputStarted?.Invoke(); StartListening(); }));
                    return;
                }

                StopListening(pendingHotkey, true);
            };

            void StopListening(string result, bool apply)
            {
                hotkeyBtn.KeyDown -= keyDownHandler;
                hotkeyBtn.KeyUp -= keyUpHandler;
                isListening = false;
                OnHotkeyInputFinished?.Invoke();
                hotkeyBtn.Text = result;
                hotkeyBtn.ForeColor = Color.FromArgb(0, 196, 160);
                hotkeyBtn.BackColor = Color.FromArgb(42, 42, 46);
                if (apply) { currentHotkey = result; onChange(result); }
            }

            hotkeyBtn.KeyDown += keyDownHandler;
            hotkeyBtn.KeyUp += keyUpHandler;
            hotkeyBtn.LostFocus += (sf, ef) => { if (isListening) StopListening(currentHotkey, false); };
            hotkeyBtn.Focus();
        }

        hotkeyBtn.Click += (s, e) => StartListening();
        panel.Controls.Add(nameLabel, 0, 0);
        panel.Controls.Add(hotkeyBtn, 1, 0);
        registerBtn(hotkeyBtn);
        return panel;
    }

    // ─── Селектор роздільної здатності ───────────────────────────────────
    // Показує тільки варіанти нижчі за нативну роздільну здатність
    private Control MakeResolutionSelector()
    {
        var screen = Screen.PrimaryScreen!.Bounds;
        int nativeWidth = screen.Width;
        int nativeHeight = screen.Height;

        var labels = new List<string>();
        var values = new List<string>();

        if (nativeHeight > 720) { labels.Add("720p"); values.Add("720p"); }
        if (nativeHeight > 1080) { labels.Add("1080p"); values.Add("1080p"); }
        if (nativeHeight > 1440) { labels.Add("1440p"); values.Add("1440p"); }

        labels.Add($"Native ({nativeWidth}x{nativeHeight})");
        values.Add("Native");

        if (!values.Contains(_currentResolution)) _currentResolution = "Native";

        return MakeArrowSelector(
            labels.ToArray(), values.ToArray(), _currentResolution,
            val => { _currentResolution = val; InvokeSettingsChanged(); });
    }

    // ─── Універсальний селектор зі стрілками (string версія) ─────────────
    private Control MakeArrowSelector(string[] labels, string[] values, string currentValue, Action<string> onChange)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Height = 42,
            AutoSize = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 4)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));

        int currentIndex = Math.Max(0, Array.IndexOf(values, currentValue));

        var valueLabel = new Label
        {
            Text = labels[currentIndex],
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            Height = 36,
            ForeColor = Color.FromArgb(0, 196, 160),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        var btnLeft = MakeArrowBtn("◀");
        var btnRight = MakeArrowBtn("▶");

        btnLeft.Click += (s, e) => { if (currentIndex > 0) { currentIndex--; valueLabel.Text = labels[currentIndex]; onChange(values[currentIndex]); } };
        btnRight.Click += (s, e) => { if (currentIndex < labels.Length - 1) { currentIndex++; valueLabel.Text = labels[currentIndex]; onChange(values[currentIndex]); } };

        panel.Controls.Add(btnLeft, 0, 0);
        panel.Controls.Add(valueLabel, 1, 0);
        panel.Controls.Add(btnRight, 2, 0);
        return panel;
    }

    // ─── Універсальний селектор зі стрілками (int версія) ────────────────
    private Control MakeArrowSelector(string[] labels, int[] values, int currentValue, Action<int> onChange)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Height = 42,
            AutoSize = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 4)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));

        int currentIndex = Math.Max(0, Array.IndexOf(values, currentValue));

        var valueLabel = new Label
        {
            Text = labels[currentIndex],
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(0, 196, 160),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        var btnLeft = MakeArrowBtn("◀");
        var btnRight = MakeArrowBtn("▶");

        btnLeft.Click += (s, e) => { if (currentIndex > 0) { currentIndex--; valueLabel.Text = labels[currentIndex]; onChange(values[currentIndex]); } };
        btnRight.Click += (s, e) => { if (currentIndex < labels.Length - 1) { currentIndex++; valueLabel.Text = labels[currentIndex]; onChange(values[currentIndex]); } };

        panel.Controls.Add(btnLeft, 0, 0);
        panel.Controls.Add(valueLabel, 1, 0);
        panel.Controls.Add(btnRight, 2, 0);
        return panel;
    }

    private Button MakeArrowBtn(string text)
    {
        var btn = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(0, 196, 160),
            Font = new Font("Segoe UI", 8),
            Cursor = Cursors.Hand,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 42, 46);
        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(58, 58, 62);
        return btn;
    }

    private void SettingsForm_Load(object sender, EventArgs e) { }
}