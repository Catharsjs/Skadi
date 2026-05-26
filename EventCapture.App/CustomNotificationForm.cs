using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
namespace EventCapture.App;

public class CustomNotificationForm : Form
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int BaseHeight = 44;
    private const int BaseMinWidth = 180;
    private const int BaseMaxWidth = 340;
    private const int BaseTextPadding = 82;
    private const int BaseTopOffset = 18;
    private const int BaseStackGap = 8;
    private static int _activeNotifications;
    private readonly System.Windows.Forms.Timer _lifeTimer;
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private readonly int _notificationIndex;
    private bool _isClosing;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(
        IntPtr hWnd,
        uint dwAffinity);

    protected override bool ShowWithoutActivation => true;
    public CustomNotificationForm(string message)
    {
        _notificationIndex = _activeNotifications;
        _activeNotifications++;

        InitializeWindow();

        float scale = GetUiScale();

        InitializeSize(message, scale);
        InitializeControls(message, scale);
        SetRoundedRegion(ScaleValue(8, scale));

        _lifeTimer = CreateLifeTimer();
        _fadeTimer = CreateFadeTimer();
    }

    // Базові параметри вікна (...
    private void InitializeWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Opacity = 0;

        BackColor = Color.FromArgb(22, 22, 25);
    }

    private void InitializeSize(string message, float scale)
    {
        Height = ScaleValue(BaseHeight, scale);

        using var graphics = CreateGraphics();

        using var font = new Font(
            "Segoe UI",
            8.8f * scale,
            FontStyle.Regular);

        var textSize = graphics.MeasureString(message, font);

        int calculatedWidth =
            ScaleValue(BaseTextPadding, scale) +
            (int)Math.Ceiling(textSize.Width);

        Width = Math.Clamp(
            calculatedWidth,
            ScaleValue(BaseMinWidth, scale),
            ScaleValue(BaseMaxWidth, scale));
    }
    // ...) Базові параметри вікна


    // Елементи сповіщення (...
    private void InitializeControls(string message, float scale)
    {
        var accentPanel = new Panel
        {
            Width = ScaleValue(3, scale),
            Dock = DockStyle.Left,
            BackColor = Color.FromArgb(0, 230, 195)
        };

        int iconSize = ScaleValue(20, scale);

        var icon = new CheckIconControl(scale)
        {
            Left = ScaleValue(22, scale),
            Top = (Height - iconSize) / 2,
            Width = iconSize,
            Height = iconSize
        };

        var messageLabel = new Label
        {
            Text = message,
            ForeColor = Color.White,
            Font = new Font(
                "Segoe UI",
                8.8f * scale,
                FontStyle.Regular),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Left = ScaleValue(54, scale),
            Top = 0,
            Width = Width - ScaleValue(64, scale),
            Height = Height
        };
        Controls.Add(accentPanel);
        Controls.Add(icon);
        Controls.Add(messageLabel);
    }
    // ...) Елементи сповіщення


    // Анімація сповіщення (...
    private System.Windows.Forms.Timer CreateLifeTimer()
    {
        var timer = new System.Windows.Forms.Timer
        {
            Interval = 2200
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            BeginFadeOut();
        };

        return timer;
    }

    private System.Windows.Forms.Timer CreateFadeTimer()
    {
        var timer = new System.Windows.Forms.Timer
        {
            Interval = 15
        };

        timer.Tick += (_, _) =>
        {
            if (!_isClosing)
            {
                FadeIn(timer);
            }
            else
            {
                FadeOut(timer);
            }
        };
        return timer;
    }

    private void FadeIn(System.Windows.Forms.Timer timer)
    {
        Opacity += 0.08;

        if (Opacity < 0.92)
            return;

        Opacity = 0.92;
        timer.Stop();
        _lifeTimer.Start();
    }

    private void FadeOut(System.Windows.Forms.Timer timer)
    {
        Opacity -= 0.08;
        if (Opacity > 0)
            return;

        timer.Stop();
        Close();
    }

    private void BeginFadeOut()
    {
        _isClosing = true;
        _fadeTimer.Start();
    }
    // ...) Анімація сповіщення

    // Масштабування DPI (...
    private float GetUiScale()
    {
        float dpiScale = DeviceDpi / 96f;

        // На високому DPI повне масштабування x2 робить сповіщення надто великим.
        // Тому використовується обмежене масштабування.
        return Math.Clamp(dpiScale, 1.0f, 1.25f);
    }

    private static int ScaleValue(int value, float scale)
    {
        return Math.Max(
            1,
            (int)Math.Round(value * scale));
    }
    // ...) Масштабування DPI


    // Відображення вікна (...
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        ExcludeFromScreenCapture();
        SetInitialPosition();

        _fadeTimer.Start();
    }

    private void ExcludeFromScreenCapture()
    {
        try
        {
            SetWindowDisplayAffinity(
                Handle,
                WDA_EXCLUDEFROMCAPTURE);
        }
        catch
        {
            // Якщо API недоступний, сповіщення просто залишиться видимим для захоплення.
        }
    }

    private void SetInitialPosition()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;

        float scale = GetUiScale();

        Location = new Point(
            area.Left + (area.Width - Width) / 2,
            area.Top +
            ScaleValue(BaseTopOffset, scale) +
            _notificationIndex * (Height + ScaleValue(BaseStackGap, scale)));
    }

    private void SetRoundedRegion(int radius)
    {
        using var path = new GraphicsPath();

        path.AddArc(0, 0, radius, radius, 180, 90);
        path.AddArc(Width - radius, 0, radius, radius, 270, 90);
        path.AddArc(Width - radius, Height - radius, radius, radius, 0, 90);
        path.AddArc(0, Height - radius, radius, radius, 90, 90);

        path.CloseFigure();

        Region = new Region(path);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_activeNotifications > 0)
            _activeNotifications--;

        _lifeTimer.Dispose();
        _fadeTimer.Dispose();
        base.OnFormClosed(e);
    }
    // ...) Відображення вікна

    // Іконка успішної дії (...
    private sealed class CheckIconControl : Control
    {
        private readonly float _scale;

        public CheckIconControl(float scale)
        {
            _scale = scale;

            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);

            DoubleBuffered = true;
            BackColor = Color.FromArgb(22, 22, 25);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var accent = Color.FromArgb(0, 230, 195);

            using var circlePen = new Pen(
                accent,
                Math.Max(1.5f, 1.8f * _scale));

            using var checkPen = new Pen(
                accent,
                Math.Max(1.7f, 2.0f * _scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            DrawCircle(e.Graphics, circlePen);
            DrawCheckMark(e.Graphics, checkPen);
        }

        private void DrawCircle(Graphics graphics, Pen pen)
        {
            float padding = 2.5f * _scale;

            var rect = new RectangleF(
                padding,
                padding,
                Width - padding * 2,
                Height - padding * 2);

            graphics.DrawEllipse(pen, rect);
        }

        private void DrawCheckMark(Graphics graphics, Pen pen)
        {
            var p1 = new PointF(
                Width * 0.32f,
                Height * 0.52f);

            var p2 = new PointF(
                Width * 0.45f,
                Height * 0.65f);

            var p3 = new PointF(
                Width * 0.70f,
                Height * 0.36f);

            graphics.DrawLines(
                pen,
                new[] { p1, p2, p3 });
        }
    }
    // ...) Іконка успішної дії
}