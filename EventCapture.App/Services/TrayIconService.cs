using System.Drawing;
using System.Windows.Forms;

namespace EventCapture.App.Services;

public sealed class TrayIconService : IDisposable
{
    private static readonly Color BackgroundColor =
        Color.FromArgb(28, 28, 30);

    private static readonly Color PrimaryTextColor =
        Color.FromArgb(240, 240, 240);

    private static readonly Color DangerColor =
        Color.FromArgb(220, 80, 80);

    private readonly NotifyIcon _icon;
    private readonly Icon _trayIconImage;
    private readonly ToolStripMenuItem _autoStartItem;

    public event Action? ToggleUiRequested;
    public event Action? ExitRequested;

    public TrayIconService(string iconPath)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = BackgroundColor,
            ForeColor = PrimaryTextColor,

            Font = new Font(
                "Segoe UI",
                9.5f,
                FontStyle.Regular),

            ShowImageMargin = false,
            ShowCheckMargin = false,

            Padding = new Padding(1),
            Renderer = new SkadiMenuRenderer()
        };

        var openItem =
            CreateMenuItem("Open Skadi");

        _autoStartItem =
            CreateMenuItem("Launch at Startup");

        _autoStartItem.Text =
    "Launch at Startup";

        _autoStartItem.Checked =
            AppSettings.IsAutoStartEnabled();
        UpdateAutoStartAppearance();
        var exitItem =
            CreateMenuItem(
                "Exit",
                DangerColor);

        openItem.Click += (_, _) =>
            ToggleUiRequested?.Invoke();

        _autoStartItem.CheckOnClick = false;

        _autoStartItem.Click += (_, _) =>
        {
            bool newState =
                !_autoStartItem.Checked;

            _autoStartItem.Checked =
                newState;

            AppSettings.SetAutoStart(
                newState);

            UpdateAutoStartAppearance();

            menu.BeginInvoke(
                new Action(() =>
                {
                    _autoStartItem.Select();
                    menu.Refresh();
                }));
        };

        exitItem.Click += (_, _) =>
            ExitRequested?.Invoke();

        menu.Opening += (_, _) =>
        {
            _autoStartItem.Checked =
                AppSettings.IsAutoStartEnabled();

            UpdateAutoStartAppearance();
        };

        bool keepMenuOpen = false;

        menu.ItemClicked += (_, eventArgs) =>
        {
            keepMenuOpen =
                ReferenceEquals(
                    eventArgs.ClickedItem,
                    _autoStartItem);
        };

        menu.Closing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason !=
                    ToolStripDropDownCloseReason.ItemClicked ||
                !keepMenuOpen)
            {
                return;
            }

            eventArgs.Cancel = true;
            keepMenuOpen = false;

            menu.BeginInvoke(
                new Action(() =>
                {
                    _autoStartItem.Select();
                    _autoStartItem.Invalidate();
                    menu.Refresh();
                }));
        };

        void UpdateAutoStartAppearance()
        {
            _autoStartItem.Invalidate();
            menu.Invalidate();
        }

        menu.Items.AddRange(
        [
            openItem,

            CreateSeparator(),

            _autoStartItem,

            CreateSeparator(),

            exitItem
        ]);

        _trayIconImage =
            new Icon(iconPath);

        _icon = new NotifyIcon
        {
            Text = "Skadi",
            Icon = _trayIconImage,
            ContextMenuStrip = menu,
            Visible = true
        };

        _icon.DoubleClick += (_, _) =>
            ToggleUiRequested?.Invoke();
    }

    // Тимчасово залишається, оскільки App.xaml.cs
    // може викликати цей метод.
    public void UpdateHotkeys(
        string screenshot,
        string record)
    {
    }

    private static ToolStripMenuItem CreateMenuItem(
        string text,
        Color? textColor = null)
    {
        return new ToolStripMenuItem
        {
            Text = text,

            ForeColor =
                textColor ?? PrimaryTextColor,

            BackColor = BackgroundColor,

            Padding = new Padding(
                left: 10,
                top: 5,
                right: 24,
                bottom: 5),

            AutoSize = true
        };
    }

    private static ToolStripSeparator CreateSeparator()
    {
        return new ToolStripSeparator
        {
            Margin = new Padding(
                left: 10,
                top: 3,
                right: 10,
                bottom: 3)
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;

        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _trayIconImage.Dispose();
    }

    private sealed class SkadiMenuRenderer :
        ToolStripProfessionalRenderer
    {
        private static readonly Color Background =
            Color.FromArgb(28, 28, 30);

        private static readonly Color Hover =
            Color.FromArgb(50, 50, 54);

        private static readonly Color Border =
            Color.FromArgb(58, 58, 62);

        private static readonly Color Accent =
            Color.FromArgb(0, 196, 160);

        public SkadiMenuRenderer()
            : base(new SkadiColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(
            ToolStripRenderEventArgs e)
        {
            using var brush =
                new SolidBrush(Background);

            e.Graphics.FillRectangle(
                brush,
                e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(
            ToolStripRenderEventArgs e)
        {
            // Image margin навмисно не малюється.
        }

        protected override void OnRenderMenuItemBackground(
     ToolStripItemRenderEventArgs e)
        {
            Color color =
                e.Item.Selected
                    ? Hover
                    : Background;

            using var brush =
                new SolidBrush(color);

            e.Graphics.FillRectangle(
                brush,
                new Rectangle(
                    Point.Empty,
                    e.Item.Size));
        }

        protected override void OnRenderItemText(
     ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is not ToolStripMenuItem)
            {
                base.OnRenderItemText(e);
                return;
            }

            string[] parts =
                e.Text.Split('\t', 2);

            const int leftPadding = 12;
            const int rightPadding = 34;

            var mainTextBounds =
                new Rectangle(
                    leftPadding,
                    0,
                    e.Item.Width -
                    leftPadding -
                    rightPadding,
                    e.Item.Height);

            TextRenderer.DrawText(
                e.Graphics,
                parts[0],
                e.TextFont,
                mainTextBounds,
                e.TextColor,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix);

           
        }

        protected override void OnRenderSeparator(
            ToolStripSeparatorRenderEventArgs e)
        {
            int y =
                e.Item.Height / 2;

            using var pen =
                new Pen(Border);

            e.Graphics.DrawLine(
                pen,
                10,
                y,
                e.Item.Width - 10,
                y);
        }

        protected override void OnRenderToolStripBorder(
        ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is not { } toolStrip)
                return;

            using var borderPen =
                new Pen(Border);

            e.Graphics.DrawRectangle(
                borderPen,
                0,
                0,
                toolStrip.Width - 1,
                toolStrip.Height - 1);

            foreach (ToolStripItem stripItem
                     in toolStrip.Items)
            {
                if (stripItem is ToolStripMenuItem
                    {
                        Checked: true
                    })
                {
                    DrawRightCheckMark(
                     e.Graphics,
                     (ToolStripMenuItem)stripItem);
                }
            }
        }
        private static void DrawRightCheckMark(
     Graphics graphics,
     ToolStripMenuItem item)
        {
            const int leftPadding = 12;
            const int gapAfterText = 12;

            string visibleText =
                item.Text.Split('\t', 2)[0];

            Size textSize =
                TextRenderer.MeasureText(
                    visibleText,
                    item.Font,
                    new Size(
                        int.MaxValue,
                        int.MaxValue),
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPrefix |
                    TextFormatFlags.NoPadding);

            int checkSize =
            Math.Max(
                10,
                item.Font.Height * 72 / 100);

            int checkLeft =
                item.Bounds.Left +
                leftPadding +
                textSize.Width +
                gapAfterText;

            int centerX =
                checkLeft +
                checkSize / 2;

            int centerY =
                item.Bounds.Top +
                item.Bounds.Height / 2;

            float penWidth =
             Math.Max(
                 1.8f,
                 checkSize * 0.12f);

            using var pen =
                new Pen(
                    Accent,
                    penWidth)
                {
                    StartCap =
                        System.Drawing.Drawing2D
                            .LineCap.Round,

                    EndCap =
                        System.Drawing.Drawing2D
                            .LineCap.Round,

                    LineJoin =
                        System.Drawing.Drawing2D
                            .LineJoin.Round
                };

            graphics.SmoothingMode =
                System.Drawing.Drawing2D
                    .SmoothingMode.AntiAlias;

            graphics.DrawLines(
                pen,
                [
                    new PointF(
                centerX - checkSize * 0.42f,
                centerY),

            new PointF(
                centerX - checkSize * 0.10f,
                centerY + checkSize * 0.30f),

            new PointF(
                centerX + checkSize * 0.48f,
                centerY - checkSize * 0.40f)
                ]);
        }
    }

    private sealed class SkadiColorTable :
        ProfessionalColorTable
    {
        private static readonly Color Background =
            Color.FromArgb(28, 28, 30);

        private static readonly Color Hover =
            Color.FromArgb(50, 50, 54);

        private static readonly Color Border =
            Color.FromArgb(58, 58, 62);

        public override Color ToolStripDropDownBackground =>
            Background;

        public override Color ImageMarginGradientBegin =>
            Background;

        public override Color ImageMarginGradientMiddle =>
            Background;

        public override Color ImageMarginGradientEnd =>
            Background;

        public override Color MenuItemSelected =>
            Hover;

        public override Color MenuItemSelectedGradientBegin =>
            Hover;

        public override Color MenuItemSelectedGradientEnd =>
            Hover;

        public override Color MenuBorder =>
            Border;

        public override Color SeparatorDark =>
            Border;

        public override Color SeparatorLight =>
            Border;
    }
}