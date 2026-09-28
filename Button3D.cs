using System.Drawing.Drawing2D;

namespace VoiceOS;

/// <summary>A custom-drawn push button with a raised, tactile 3D look: gradient
/// fill, drop shadow, top sheen highlight, and a real pressed-in effect on click.</summary>
internal sealed class Button3D : Button
{
    private bool _isPressed;
    private bool _isHovered;

    public Button3D()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _isPressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _isPressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _isHovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _isHovered = false;
        _isPressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Fill with the parent's background first so the rounded corners blend in
        // instead of showing the control's square bounding box.
        using (var bg = new SolidBrush(Parent?.BackColor ?? BackColor))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        var rect = new Rectangle(1, 1, Width - 3, Height - 4);
        int radius = Math.Min(8, rect.Height / 2);

        using var path = RoundedRect(rect, radius);

        if (!_isPressed)
        {
            using var shadowBrush = new SolidBrush(Color.FromArgb(55, 0, 0, 0));
            var shadowRect = rect;
            shadowRect.Offset(0, 2);
            using var shadowPath = RoundedRect(shadowRect, radius);
            g.FillPath(shadowBrush, shadowPath);
        }

        Color top, bottom;
        if (!Enabled)
        {
            top = Color.FromArgb(200, 200, 205);
            bottom = Color.FromArgb(170, 170, 176);
        }
        else if (_isHovered)
        {
            top = Color.FromArgb(96, 156, 232);
            bottom = Color.FromArgb(40, 100, 196);
        }
        else
        {
            top = Color.FromArgb(78, 140, 222);
            bottom = Color.FromArgb(32, 88, 182);
        }
        if (_isPressed)
        {
            (top, bottom) = (bottom, top);
        }

        using (var gradient = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical))
        {
            g.FillPath(gradient, path);
        }

        using (var borderPen = new Pen(Color.FromArgb(30, 60, 110)))
        {
            g.DrawPath(borderPen, path);
        }

        if (!_isPressed && Enabled)
        {
            var sheenRect = new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, Math.Max(1, rect.Height / 2 - 2));
            using var sheenPath = RoundedRect(sheenRect, radius);
            using var sheenBrush = new LinearGradientBrush(sheenRect, Color.FromArgb(90, 255, 255, 255), Color.FromArgb(10, 255, 255, 255), LinearGradientMode.Vertical);
            g.FillPath(sheenBrush, sheenPath);
        }

        var textRect = rect;
        if (_isPressed) textRect.Offset(0, 1);
        Color textColor = Enabled ? ForeColor : Color.FromArgb(230, 230, 230);
        TextRenderer.DrawText(g, Text, Font, textRect, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
