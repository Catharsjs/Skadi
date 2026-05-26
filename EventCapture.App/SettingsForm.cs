using EventCapture.Core.Capture;
using EventCapture.Core.Diagnostics;
namespace EventCapture.App;

// Бічна панель налаштувань застосунку
public partial class SettingsForm : Form
{
    // Події
    public event Action<int, int, string, string, string, string, string, bool, string?, bool, string?>? OnSettingsChanged;
    public event Action<bool>? OnOverlayToggled;
    public event Action? OnHotkeyInputStarted;
    public event Action? OnHotkeyInputFinished;

    // Поля (...
    // Хоткеї
    private string _hotkeyScreenshot;
    private string _hotkeySaveVideo;
    private string _hotkeyToggleUI;
    private Button? _btnHotkeyScreenshot;
    private Button? _btnHotkeySaveVideo;
    private Button? _btnHotkeyToggleUI;
    private Label _labelHotkeyScreenshot = null!;
    private Label _labelHotkeySaveVideo = null!;

    // Аудіо
    private bool _recordSystem = false;
    private bool _recordMic = false;
    private string? _systemDeviceId = null;
    private string? _micDeviceId = null;
    private Button _btnSystemDevice = null!;
    private Button _btnMicDevice = null!;

    // Лог подій
    private Label? _eventLog;
    private System.Windows.Forms.Timer? _delayTimer;
    private System.Windows.Forms.Timer? _fadeTimer;
    private int _fadeAlpha = 255;

    // UI
    private readonly MainForm _mainForm;
    private string _currentResolution = "Native";
    private Label _folderValueLabel = null!;
    private ToggleSwitch _toggleOverlay = null!;
    // ...) Поля

    // Публічні властивості (...
    public int BufferDurationSeconds { get; private set; }
    public int FrameRate { get; private set; }
    public string SaveFolder { get; private set; }
    // ...) Публічні властивості

    // Масштабування (...
    // Референс: ноутбук 2560×1440 (висота робочої зони ~1400px).
    private float _scale = 1f;
    private int S(int v) => (int)(v * _scale);
    private float Sf(float v) => v * _scale;
    // ...) Масштабування

    public SettingsForm(
        MainForm mainForm,
        string saveFolder,
        int fps,
        int bufferSeconds,
        string resolution = "Native",
        string hotkeyScreenshot = "Alt+F1",
        string hotkeySaveVideo = "Alt+F2",
        string hotkeyToggleUI = "Alt+F3",
        bool recordSystem = false,
        string? systemDeviceId = null,
        bool recordMic = false,
        string? micDeviceId = null)
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

        AppLogger.Info("SettingsForm ініціалізовано.");
    }

    // Параметри вікна (...
    // Приховуємо форму з панелі завдань (toolwindow).
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
    // ...) Параметри вікна

    // Побудова UI (...
    private void BuildUI()
    {
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        using var g = CreateGraphics();
        float dpi = g.DpiX;
        int physH = screen.Height;
        int physW = screen.Width;

        // Визначаємо профіль монітора (...
        (float scale, int panelWidth, float fontSize) = (dpi, physH) switch
        {
            // 1440p HiDPI (ноутбуки 14–16", 192dpi).
            ( >= 144, >= 1400) => (scale: 1.0f, panelWidth: 560, fontSize: 9f),

            // 1440p звичайний (27" монітор, 96dpi).
            ( < 144, >= 1400) => (scale: 1.3f, panelWidth: 500, fontSize: 9f),

            // 1080p HiDPI (ноутбуки 125–150% scaling, 120–144dpi).
            ( >= 120, >= 1060) => (scale: 1.1f, panelWidth: 460, fontSize: 9f),

            // 1080p звичайний (24" монітор, 96dpi).
            ( < 120, >= 1060) => (scale: 1.0f, panelWidth: 420, fontSize: 14f),

            // 768p і менше.
            _ => (scale: 0.85f, panelWidth: 380, fontSize: 9f),
        };

        _scale = scale;
        // ...) Визначаємо профіль монітора

        // Налаштування форми (...
        FormBorderStyle = FormBorderStyle.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(panelWidth, screen.Height);
        Location = new Point(screen.Right - panelWidth, screen.Top);
        BackColor = Color.FromArgb(28, 28, 30);
        ForeColor = Color.FromArgb(240, 240, 240);
        Font = new Font("Segoe UI", fontSize);
        Padding = new Padding(S(14), 0, S(14), S(14));
        // ...) Налаштування форми

        // Головний layout (...
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, S(4), 0, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // ...) Головний layout

        // Заголовок (...
        layout.Controls.Add(MakeTitle("EventCapture"));
        layout.Controls.Add(MakeSubLabel("● Buffer Active", Color.FromArgb(0, 196, 160)));
        layout.Controls.Add(MakeSeparator());
        // ...) Заголовок

        // Кнопки дій (...
        layout.Controls.Add(MakePrimaryButton("Save Screenshot", () => _mainForm.TakeScreenshot()));
        _labelHotkeyScreenshot = MakeSubLabel(_hotkeyScreenshot, Color.FromArgb(0, 196, 160));
        layout.Controls.Add(_labelHotkeyScreenshot);
        layout.Controls.Add(MakePrimaryButton("Save Video", () => _mainForm.SaveVideo()));
        _labelHotkeySaveVideo = MakeSubLabel(_hotkeySaveVideo, Color.FromArgb(0, 196, 160));
        layout.Controls.Add(_labelHotkeySaveVideo);
        layout.Controls.Add(MakeSeparator());
        // ...) Кнопки дій

        // Відео налаштування (...
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
        // ...) Відео налаштування

        // Аудіо (...
        layout.Controls.Add(MakeSubLabel("Audio", Color.FromArgb(150, 150, 150)));
        layout.Controls.Add(MakeAudioRow("Record System Audio", isSystem: true));
        layout.Controls.Add(MakeAudioRow("Record Microphone", isSystem: false));
        layout.Controls.Add(MakeSeparator());
        // ...) Аудіо

        // Папка збереження (...
        layout.Controls.Add(MakeSubLabel("Save Folder", Color.FromArgb(150, 150, 150)));
        _folderValueLabel = new Label
        {
            Text = SaveFolder,
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = S(28),
            Font = new Font("Segoe UI", Sf(8.5f)),
            ForeColor = Color.FromArgb(0, 196, 160),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, S(1), 0, S(1))
        };
        layout.Controls.Add(_folderValueLabel);

        var browseBtn = MakeSecondaryButton("Browse...", () =>
        {
            using var dlg = new FolderBrowserDialog();

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                SaveFolder = dlg.SelectedPath;
                _folderValueLabel.Text = SaveFolder;
                InvokeSettingsChanged();

                AppLogger.Info($"Папку збереження змінено: {SaveFolder}");
            }
        });

        browseBtn.Paint += (s, e) =>
        {
            e.Graphics.Clear(browseBtn.BackColor);
            TextRenderer.DrawText(
                e.Graphics, browseBtn.Text, browseBtn.Font,
                browseBtn.ClientRectangle, Color.FromArgb(240, 240, 240),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        layout.Controls.Add(browseBtn);
        layout.Controls.Add(MakeSeparator());
        layout.Controls.Add(MakeSeparator());
        // ...) Папка збереження

        // Show System Info тумблер (...
        var toggleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, S(3), 0, S(3))
        };
        toggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(54)));
        toggleRow.Controls.Add(
            MakeSubLabel("Show System Info", Color.FromArgb(240, 240, 240)), 0, 0);

        _toggleOverlay = new ToggleSwitch
        {
            Anchor = AnchorStyles.Right,
            TabStop = false
        };
        _toggleOverlay.CheckedChanged += (s, e) =>
        {
            OnOverlayToggled?.Invoke(_toggleOverlay.Checked);
            ClearFocus();
        };

        toggleRow.Controls.Add(_toggleOverlay, 1, 0);
        layout.Controls.Add(toggleRow);
        layout.Controls.Add(MakeSeparator());
        // ...) Show System Info тумблер

        // Хоткеї (...
        layout.Controls.Add(MakeSubLabel("Hot Keys", Color.FromArgb(150, 150, 150)));
        layout.Controls.Add(MakeHotkeyRow("Save Screenshot", _hotkeyScreenshot, val =>
        {
            _hotkeyScreenshot = val;
            if (_hotkeySaveVideo == val) { _hotkeySaveVideo = "Unassigned"; _btnHotkeySaveVideo!.Text = "Unassigned"; }
            if (_hotkeyToggleUI == val) { _hotkeyToggleUI = "Unassigned"; _btnHotkeyToggleUI!.Text = "Unassigned"; }
            InvokeSettingsChanged();
        }, btn => _btnHotkeyScreenshot = btn));

        layout.Controls.Add(MakeHotkeyRow("Save Video", _hotkeySaveVideo, val =>
        {
            _hotkeySaveVideo = val;
            if (_hotkeyScreenshot == val) { _hotkeyScreenshot = "Unassigned"; _btnHotkeyScreenshot!.Text = "Unassigned"; }
            if (_hotkeyToggleUI == val) { _hotkeyToggleUI = "Unassigned"; _btnHotkeyToggleUI!.Text = "Unassigned"; }
            InvokeSettingsChanged();
        }, btn => _btnHotkeySaveVideo = btn));

        layout.Controls.Add(MakeHotkeyRow("Toggle UI", _hotkeyToggleUI, val =>
        {
            _hotkeyToggleUI = val;
            if (_hotkeyScreenshot == val) { _hotkeyScreenshot = "Unassigned"; _btnHotkeyScreenshot!.Text = "Unassigned"; }
            if (_hotkeySaveVideo == val) { _hotkeySaveVideo = "Unassigned"; _btnHotkeySaveVideo!.Text = "Unassigned"; }
            InvokeSettingsChanged();
        }, btn => _btnHotkeyToggleUI = btn));
        layout.Controls.Add(MakeSeparator());
        // ...) Хоткеї

        // Event log (...
        _eventLog = new Label
        {
            Dock = DockStyle.Fill,
            Height = S(40),
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(0, 196, 160),
            Font = new Font("Segoe UI", Sf(8.5f)),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, S(8), 0, S(8)),
            Visible = true,
            Text = string.Empty
        };
        layout.Controls.Add(_eventLog);
        // ...) Event log

        Controls.Add(layout);

        // Exit кнопка (...
        var exitBtn = MakeExitButton();
        exitBtn.Dock = DockStyle.Bottom;
        Controls.Add(exitBtn);
        // ...) Exit кнопка
    }

    // Перебудова UI при переміщенні форми на інший монітор.
    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        var newScreen = Screen.FromControl(this);
        var currentScreen = Screen.FromPoint(new Point(Left - 1, Top));

        if (newScreen.DeviceName != currentScreen.DeviceName)
        {
            AppLogger.Debug("SettingsForm перейшла на інший монітор, перебудова UI.");
            Controls.Clear();
            BuildUI();
        }
    }
    // ...) Побудова UI

    // Сповіщення про зміну налаштувань (...
    // Оновлення підписів кнопок для хоткеїв
    private void InvokeSettingsChanged()
    {
        _labelHotkeyScreenshot.Text = _hotkeyScreenshot;
        _labelHotkeySaveVideo.Text = _hotkeySaveVideo;

        OnSettingsChanged?.Invoke(
            FrameRate, BufferDurationSeconds, SaveFolder,
            _currentResolution,
            _hotkeyScreenshot, _hotkeySaveVideo, _hotkeyToggleUI,
            _recordSystem, _systemDeviceId,
            _recordMic, _micDeviceId);
    }
    // ...) Сповіщення про зміну налаштувань

    // Логер подій (...
    // Виводить повідомлення у нижній рядок панелі з плавним затуханням.
    public void LogEvent(string message)
    {
        if (InvokeRequired) { Invoke(() => LogEvent(message)); return; }
        if (_eventLog == null) return;

        // Скидаємо попередні таймери перед новим повідомленням.
        _fadeTimer?.Stop();
        _fadeTimer?.Dispose();
        _fadeTimer = null;

        _delayTimer?.Stop();
        _delayTimer?.Dispose();
        _delayTimer = null;

        _eventLog.Text = message;
        _eventLog.ForeColor = Color.FromArgb(0, 196, 160);
        _fadeAlpha = 255;

        _delayTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _delayTimer.Tick += (s, e) =>
        {
            _delayTimer?.Stop();
            _delayTimer?.Dispose();
            _delayTimer = null;

            _fadeTimer = new System.Windows.Forms.Timer { Interval = 30 };
            _fadeTimer.Tick += (s2, e2) =>
            {
                _fadeAlpha -= 8;

                if (_fadeAlpha <= 0)
                {
                    if (_eventLog != null)
                    {
                        _eventLog.Text = string.Empty;
                        _eventLog.ForeColor = Color.FromArgb(0, 196, 160);
                    }

                    var t = _fadeTimer;
                    _fadeTimer = null;
                    t?.Stop();
                    t?.Dispose();
                }
                else
                {
                    if (_eventLog != null)
                    {
                        int r = (int)(0 * _fadeAlpha / 255.0 + 28 * (1 - _fadeAlpha / 255.0));
                        int g = (int)(196 * _fadeAlpha / 255.0 + 28 * (1 - _fadeAlpha / 255.0));
                        int b = (int)(160 * _fadeAlpha / 255.0 + 30 * (1 - _fadeAlpha / 255.0));
                        _eventLog.ForeColor = Color.FromArgb(r, g, b);
                    }
                }
            };
            _fadeTimer.Start();
        };
        _delayTimer.Start();
    }
    // ...) Логер подій

    private void ClearFocus()
    {
        ActiveControl = null;
    }

    // Make* хелпери — базові елементи UI (...
    private Label MakeTitle(string text) => new Label
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.None,
        Font = new Font("Segoe UI", Sf(13f), FontStyle.Bold),
        ForeColor = Color.FromArgb(0, 196, 160),
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, S(4))
    };

    private Label MakeSubLabel(string text, Color color) => new Label
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.None,
        Font = new Font("Segoe UI", Sf(9f)),
        ForeColor = color,
        BackColor = Color.Transparent,
        Margin = new Padding(0, S(2), 0, S(2))
    };

    private Panel MakeSeparator() => new Panel
    {
        Dock = DockStyle.Fill,
        Height = 1,
        BackColor = Color.FromArgb(42, 42, 46),
        Margin = new Padding(0, S(2), 0, S(2))
    };

    private Button MakePrimaryButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Height = S(38),
            BackColor = Color.FromArgb(0, 196, 160),
            ForeColor = Color.FromArgb(10, 46, 40),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", Sf(9f), FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, S(6), 0, S(6)),
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
            Height = S(36),
            BackColor = Color.FromArgb(42, 42, 46),
            ForeColor = Color.FromArgb(240, 240, 240),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", Sf(9f)),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, S(4), 0, S(4))
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
            Height = S(44),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(42, 42, 46),
            ForeColor = Color.FromArgb(220, 80, 80),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", Sf(9f)),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, S(4), 0, S(4))
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(58, 58, 62);
        btn.Click += (s, e) => Hide();
        return btn;
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
            Font = new Font("Segoe UI", Sf(8f)),
            Cursor = Cursors.Hand,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 42, 46);
        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(58, 58, 62);
        return btn;
    }
    // ...) Make* хелпери — базові елементи UI

    // Поле аудіо (...
    private Control MakeAudioRow(string label, bool isSystem)
    {
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, S(4), 0, S(4))
        };

        // Рядок з підписом і тумблером.
        var toggleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            BackColor = Color.Transparent
        };
        toggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(54)));
        toggleRow.Controls.Add(MakeSubLabel(label, Color.FromArgb(240, 240, 240)), 0, 0);

        var toggle = new ToggleSwitch
        {
            Anchor = AnchorStyles.Right,
            TabStop = false
        };
        toggleRow.Controls.Add(toggle, 1, 0);

        // Кнопка вибору аудіо пристрою.
        var btn = new Button
        {
            Dock = DockStyle.Fill,
            Height = S(36),
            BackColor = Color.FromArgb(28, 28, 30),
            ForeColor = Color.FromArgb(150, 150, 150),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", Sf(9f)),
            Cursor = Cursors.Default,
            Margin = new Padding(0, S(4), 0, 0),
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
            TextRenderer.DrawText(
                e.Graphics, btn.Text, btn.Font,
                btn.ClientRectangle, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        // Пристрій за замовчуванням
        try
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var defaultDevice = isSystem
                ? enumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.Role.Multimedia)
                : enumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Capture,
                    NAudio.CoreAudioApi.Role.Multimedia);

            btn.Text = defaultDevice.FriendlyName;

            if (isSystem) _systemDeviceId = defaultDevice.ID;
            else _micDeviceId = defaultDevice.ID;
        }
        catch (Exception ex)
        {
            btn.Text = "Select device...";
            AppLogger.Debug($"MakeAudioRow — пристрій за замовчуванням недоступний: {ex.Message}");
        }

        if (isSystem) _btnSystemDevice = btn;
        else _btnMicDevice = btn;

        // Відновлюємо стан увімкнення з збережених налаштувань.
        bool initialState = isSystem ? _recordSystem : _recordMic;
        if (initialState)
        {
            toggle.Checked = true;
            btn.Enabled = true;
            btn.Cursor = Cursors.Hand;
            btn.BackColor = Color.FromArgb(42, 42, 46);
        }

        // Відновлюємо збережений пристрій.
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
            catch (Exception ex)
            {
                AppLogger.Debug($"MakeAudioRow — збережений пристрій не знайдено ({savedId}): {ex.Message}");
            }
        }

        // Ввімкнення/вимкнення вибору пристрою
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
            ClearFocus();
        };

        // Випадаючий список пристроїв
        btn.Click += (s, e) =>
        {
            if (!(isSystem ? _recordSystem : _recordMic)) return;

            var devices = isSystem
                ? AudioRecorder.GetOutputDevices()
                : AudioRecorder.GetInputDevices();

            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(42, 42, 46),
                ForeColor = Color.FromArgb(240, 240, 240),
                RenderMode = ToolStripRenderMode.System
            };

            foreach (var (id, name) in devices)
            {
                var item = new ToolStripMenuItem(name);
                item.Click += (_, __) =>
                {
                    if (isSystem)
                    {
                        _systemDeviceId = id;
                        _mainForm.SetUserSelectedSystemDevice(id);
                    }
                    else
                    {
                        _micDeviceId = id;
                    }

                    btn.Text = name;
                    InvokeSettingsChanged();

                    AppLogger.Info($"Аудіо пристрій змінено: [{(isSystem ? "система" : "мікрофон")}] {name}");
                };
                menu.Items.Add(item);
            }
            menu.Show(btn, new Point(0, btn.Height));
        };
        container.Controls.Add(toggleRow);
        container.Controls.Add(btn);
        return container;
    }
    // ...) Поле аудіо

    // Поле хоткея (...
    private Control MakeHotkeyRow(
        string label,
        string currentHotkey,
        Action<string> onChange,
        Action<Button> registerBtn)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            TabStop = false,
            ColumnCount = 2,
            Height = S(46),
            AutoSize = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, S(4), 0, S(4))
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));

        var nameLabel = new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(190, 190, 190),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", Sf(9f))
        };

        var hotkeyBtn = new Button
        {
            Text = currentHotkey,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(42, 42, 46),
            ForeColor = Color.FromArgb(0, 196, 160),
            Font = new Font("Segoe UI", Sf(9f), FontStyle.Bold),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
        hotkeyBtn.FlatAppearance.BorderColor = Color.FromArgb(58, 58, 62);

        bool isListening = false;

        // Починаємо прослуховування натискань клавіш.
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

                // Escape — скасувати.
                if (e2.KeyCode == Keys.Escape)
                {
                    StopListening(currentHotkey, false);
                    return;
                }

                // Backspace — зняти призначення.
                if (e2.KeyCode == Keys.Back)
                {
                    StopListening("Unassigned", true);
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

                // Відпущено тільки модифікатор — повторно запускаємо прослуховування
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

                // Відпущено клавішу без повного хоткея — повторно запускаємо
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

            // Зупиняємо прослуховування, застосовуємо результат
            void StopListening(string result, bool apply)
            {
                hotkeyBtn.KeyDown -= keyDownHandler;
                hotkeyBtn.KeyUp -= keyUpHandler;
                isListening = false;
                OnHotkeyInputFinished?.Invoke();
                hotkeyBtn.Text = result;
                hotkeyBtn.ForeColor = Color.FromArgb(0, 196, 160);
                hotkeyBtn.BackColor = Color.FromArgb(42, 42, 46);

                if (apply)
                {
                    currentHotkey = result;
                    onChange(result);
                    AppLogger.Info($"Хоткей [{label}] змінено: {result}");
                }
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
    // ...) Поле хоткея

    // Селектори (...
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

        if (!values.Contains(_currentResolution))
            _currentResolution = "Native";

        return MakeArrowSelector(
            labels.ToArray(), values.ToArray(), _currentResolution,
            val => { _currentResolution = val; InvokeSettingsChanged(); });
    }

    // Селектор зі стрілками для рядкових значень.
    private Control MakeArrowSelector(
        string[] labels,
        string[] values,
        string currentValue,
        Action<string> onChange)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Height = S(42),
            AutoSize = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, S(4), 0, S(4))
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(28)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(28)));

        int currentIndex = Math.Max(0, Array.IndexOf(values, currentValue));

        var valueLabel = new Label
        {
            Text = labels[currentIndex],
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            Height = S(36),
            ForeColor = Color.FromArgb(0, 196, 160),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", Sf(10f), FontStyle.Bold)
        };

        var btnLeft = MakeArrowBtn("◀");
        var btnRight = MakeArrowBtn("▶");

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

    // Селектор зі стрілками для цілочисельних значень.
    private Control MakeArrowSelector(
        string[] labels,
        int[] values,
        int currentValue,
        Action<int> onChange)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Height = S(42),
            AutoSize = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, S(4), 0, S(4))
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(28)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(28)));

        int currentIndex = Math.Max(0, Array.IndexOf(values, currentValue));

        var valueLabel = new Label
        {
            Text = labels[currentIndex],
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(0, 196, 160),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", Sf(10f), FontStyle.Bold)
        };

        var btnLeft = MakeArrowBtn("◀");
        var btnRight = MakeArrowBtn("▶");

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
    // ...) Селектори

    // Оновлення назви системного аудіо (...
    public void UpdateSystemDeviceName(string deviceName)
    {
        if (InvokeRequired) { Invoke(() => UpdateSystemDeviceName(deviceName)); return; }
        _btnSystemDevice.Text = deviceName;
    }
    // ...) Оновлення назви системного аудіо

    private void SettingsForm_Load(object sender, EventArgs e) { }
}