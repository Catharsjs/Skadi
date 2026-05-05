namespace EventCapture.App;

public class ToggleSwitch : Control
{
    private bool _checked = false;
    private readonly Color _onColor = Color.FromArgb(0, 196, 160);
    private readonly Color _offColor = Color.FromArgb(60, 60, 64);
    private readonly Color _thumbColor = Color.FromArgb(240, 240, 240);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set { _checked = value; Invalidate(); }
    }

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        Size = new Size(44, 24);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(28, 28, 30);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var trackColor = _checked ? _onColor : _offColor;
        var trackRect = new Rectangle(0, 4, Width - 1, Height - 9);
        int radius = trackRect.Height / 2;

        using var trackBrush = new SolidBrush(trackColor);
        g.FillEllipse(trackBrush, trackRect.X, trackRect.Y, radius * 2, radius * 2);
        g.FillEllipse(trackBrush, trackRect.Right - radius * 2, trackRect.Y, radius * 2, radius * 2);
        g.FillRectangle(trackBrush, trackRect.X + radius, trackRect.Y, trackRect.Width - radius * 2, radius * 2);

        int thumbX = _checked ? Width - 22 : 2;
        using var thumbBrush = new SolidBrush(_thumbColor);
        g.FillEllipse(thumbBrush, thumbX, 2, 20, 20);
    }
}