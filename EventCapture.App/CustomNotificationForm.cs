using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EventCapture.App;

public class CustomNotificationForm : Form
{
    private static int _activeNotifications;

    private readonly System.Windows.Forms.Timer _lifeTimer;
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private readonly int _notificationIndex;

    private bool _isClosing;

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    protected override bool ShowWithoutActivation => true;

    public CustomNotificationForm(string message)
    {
        _notificationIndex = _activeNotifications;
        _activeNotifications++;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Opacity = 0;

        float scale = GetUiScale();

        Height = ScaleValue(44, scale);

        int minWidth = ScaleValue(180, scale);
        int maxWidth = ScaleValue(340, scale);

        using var measureGraphics = CreateGraphics();

        var textSize = measureGraphics.MeasureString(
            message,
            new Font("Segoe UI", 8.8f * scale, FontStyle.Regular));

        int calculatedWidth =
            ScaleValue(82, scale) +
            (int)Math.Ceiling(textSize.Width);

        Width = Math.Clamp(
            calculatedWidth,
            minWidth,
            maxWidth);

        BackColor = Color.FromArgb(22, 22, 25);

        var accentPanel = new Panel
        {
            Width = ScaleValue(3, scale),
            Dock = DockStyle.Left,
            BackColor = Color.FromArgb(0, 230, 195)
        };

        var iconSize = ScaleValue(20, scale);

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
            Font = new Font("Segoe UI", 8.8f * scale, FontStyle.Regular),
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

        SetRoundedRegion(ScaleValue(8, scale));

        _lifeTimer = new System.Windows.Forms.Timer { Interval = 2200 };
        _lifeTimer.Tick += (_, _) =>
        {
            _lifeTimer.Stop();
            BeginFadeOut();
        };

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _fadeTimer.Tick += (_, _) =>
        {
            if (!_isClosing)
            {
                Opacity += 0.08;

                if (Opacity >= 0.92)
                {
                    Opacity = 0.92;
                    _fadeTimer.Stop();
                    _lifeTimer.Start();
                }
            }
            else
            {
                Opacity -= 0.08;

                if (Opacity <= 0)
                {
                    _fadeTimer.Stop();
                    Close();
                }
            }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Поки можна залишити закоментованим для тестів.
        // try
        // {
        //     SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        // }
        // catch
        // {
        // }

        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;

        float scale = GetUiScale();

        Location = new Point(
            area.Left + (area.Width - Width) / 2,
            area.Top + ScaleValue(18, scale) + (_notificationIndex * (Height + ScaleValue(8, scale))));

        _fadeTimer.Start();
    }

    private float GetUiScale()
    {
        float dpiScale = DeviceDpi / 96f;

        // ВАЖЛИВО:
        // На 192 DPI не масштабуємо x2, бо toast стає гігантським.
        // Робимо м'яке DPI scaling із верхньою межею.
        return Math.Clamp(dpiScale, 1.0f, 1.25f);
    }

    private static int ScaleValue(int value, float scale)
    {
        return Math.Max(1, (int)Math.Round(value * scale));
    }

    private void BeginFadeOut()
    {
        _isClosing = true;
        _fadeTimer.Start();
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

            using var circlePen = new Pen(accent, Math.Max(1.5f, 1.8f * _scale));
            using var checkPen = new Pen(accent, Math.Max(1.7f, 2.0f * _scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            float padding = 2.5f * _scale;
            var rect = new RectangleF(
                padding,
                padding,
                Width - padding * 2,
                Height - padding * 2);

            e.Graphics.DrawEllipse(circlePen, rect);

            var p1 = new PointF(Width * 0.32f, Height * 0.52f);
            var p2 = new PointF(Width * 0.45f, Height * 0.65f);
            var p3 = new PointF(Width * 0.70f, Height * 0.36f);

            e.Graphics.DrawLines(checkPen, new[] { p1, p2, p3 });
        }
    }
}