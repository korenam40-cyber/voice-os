using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace VoiceOS;

/// <summary>
/// The "Jarvis × Terminator" look. Two colours with fixed meanings, so the state
/// reads at a glance without reading a word: RED is machine vision — scanning,
/// waiting, something wrong; CYAN is Jarvis — controls, done, linked.
/// </summary>
internal static class Hud
{
    public static readonly Color Ground = Color.FromArgb(4, 8, 15);
    public static readonly Color Ink = Color.FromArgb(207, 230, 245);
    public static readonly Color Dim = Color.FromArgb(109, 138, 163);
    public static readonly Color Line = Color.FromArgb(22, 48, 74);
    public static readonly Color Cyan = Color.FromArgb(86, 217, 255);
    public static readonly Color Red = Color.FromArgb(255, 42, 42);
    public static readonly Color RedText = Color.FromArgb(255, 96, 96);
    public static readonly Color RedLine = Color.FromArgb(120, 32, 38);
    public static readonly Color RedGround = Color.FromArgb(22, 4, 7);
    public static readonly Color Ok = Color.FromArgb(92, 242, 166);

    public static readonly Font Mono = new("Consolas", 8.25f);
    public static readonly Font MonoSmall = new("Consolas", 7.25f);
    public static readonly Font Body = new("Segoe UI Semibold", 9.75f);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    /// <summary>False when Windows "Show animations" is off: then nothing scrolls,
    /// pulses or flickers, and the F8 indicator shows its final frame briefly.</summary>
    public static bool MotionEnabled
    {
        get
        {
            try { return !SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out bool on, 0) || on; }
            catch { return true; }
        }
    }

    /// <summary>The application icon, loaded once from the embedded copy.</summary>
    /// <remarks>A single-file publish unpacks no icon file beside the exe, so the
    /// forms cannot load one from disk. Null if anything goes wrong: a missing
    /// icon is a cosmetic problem and must never stop the app opening.</remarks>
    public static Icon? AppIcon { get; } = LoadAppIcon();

    private static Icon? LoadAppIcon()
    {
        try
        {
            using Stream? s = typeof(Hud).Assembly.GetManifestResourceStream("app.ico");
            return s is null ? null : new Icon(s);
        }
        catch
        {
            return null;
        }
    }

    public static Color Alpha(Color c, int a) => Color.FromArgb(Math.Clamp(a, 0, 255), c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                              (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar, so a window reads as one HUD instead of a HUD in a
    /// white frame. Does nothing on Windows versions without it.</summary>
    public static void DarkTitleBar(IntPtr handle)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        }
        catch
        {
            // Cosmetic only.
        }
    }

    public static void DoubleBuffer(Control c, bool transparent = false)
    {
        typeof(Control).GetMethod("SetStyle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(c, new object[] { ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                                      ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                                      (transparent ? ControlStyles.SupportsTransparentBackColor : 0), true });
    }
}

/// <summary>Flat HUD button. A trailing "(…)" in Text is drawn as a key hint on the
/// right, so existing text like "Stop and type (Ctrl+Alt+D)" keeps working.</summary>
internal sealed class HudButton : Button
{
    private bool _hover, _down, _active;

    /// <summary>Red outline: the main action (Hebrew dictation).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }

    /// <summary>Solid red: the action is running (dictating).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active { get => _active; set { _active = value; Invalidate(); } }

    /// <summary>Green frame and text on the normal dark fill: the thing this button stands for is up and working.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Positive { get => _positive; set { _positive = value; Invalidate(); } }
    private bool _positive;

    public HudButton()
    {
        Hud.DoubleBuffer(this);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Hud.Ground;
        ForeColor = Hud.Ink;
        Font = Hud.Body;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        g.Clear(Parent?.BackColor ?? Hud.Ground);

        var r = new Rectangle((int)(2 * s), (int)(2 * s), Width - (int)(5 * s), Height - (int)(5 * s));
        bool good = _positive && Enabled;
        Color accent = good ? Hud.Ok : Primary ? Hud.Red : Hud.Cyan;
        Color edge = !Enabled ? Hud.Line : good ? Hud.Ok : Primary ? Hud.Red : Hud.Alpha(Hud.Cyan, 120);

        Color fill = _active ? Hud.Red
                   : !Enabled ? Hud.Ground
                   : _down ? Hud.Alpha(accent, 80)
                   : _hover ? Hud.Alpha(accent, 34)
                   : Hud.Ground;
        using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);

        if (Enabled && (_hover || _active))
        {
            using var glow = new Pen(Hud.Alpha(accent, 60), 3 * s);
            g.DrawRectangle(glow, r);
        }
        using (var p = new Pen(edge, Math.Max(1, s))) g.DrawRectangle(p, r);

        // HUD corner ticks.
        if (Enabled)
        {
            using var tick = new Pen(accent, 2 * s);
            int t = (int)(7 * s);
            g.DrawLine(tick, r.Left, r.Top, r.Left + t, r.Top);
            g.DrawLine(tick, r.Left, r.Top, r.Left, r.Top + t);
            g.DrawLine(tick, r.Right, r.Bottom, r.Right - t, r.Bottom);
            g.DrawLine(tick, r.Right, r.Bottom, r.Right, r.Bottom - t);
        }

        string text = Text, hint = "";
        int i = text.LastIndexOf(" (", StringComparison.Ordinal);
        if (i > 0 && text.EndsWith(')')) { hint = text[(i + 2)..^1]; text = text[..i]; }

        Color ink = !Enabled ? Hud.Dim : _active ? Color.White : good ? Hud.Ok : Primary ? Hud.RedText : Hud.Ink;
        int pad = (int)(12 * s);
        int hintW = 0;
        if (hint.Length > 0)
        {
            hintW = TextRenderer.MeasureText(g, hint, Hud.Mono).Width;
            TextRenderer.DrawText(g, hint, Hud.Mono, new Rectangle(r.Right - pad - hintW, r.Top, hintW, r.Height),
                _active ? Color.FromArgb(255, 214, 214) : Hud.Dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        TextRenderer.DrawText(g, text, Font, new Rectangle(r.Left + pad, r.Top, r.Width - pad * 2 - hintW - (int)(6 * s), r.Height),
            ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (Focused && ShowFocusCues)
        {
            using var f = new Pen(Hud.Cyan) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(f, Rectangle.Inflate(r, -(int)(4 * s), -(int)(4 * s)));
        }
    }
}

/// <summary>A cyan pill switch with a title, a note and an optional EXPERIMENTAL tag.
/// Still a CheckBox underneath: Checked, CheckedChanged, Enabled and the space bar
/// all behave exactly as before.</summary>
internal sealed class HudSwitch : CheckBox
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Note { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string DisabledNote { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Experimental { get; set; }

    public HudSwitch()
    {
        Hud.DoubleBuffer(this);
        AutoSize = false;
        BackColor = Hud.Ground;
        ForeColor = Hud.Ink;
        Cursor = Cursors.Hand;
    }

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        g.Clear(Parent?.BackColor ?? Hud.Ground);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int kw = (int)(32 * s), kh = (int)(16 * s);
        var knob = new Rectangle((int)(10 * s), (Height - kh) / 2, kw, kh);
        using (var path = Pill(knob))
        {
            if (Checked && Enabled)
            {
                using var glow = new Pen(Hud.Alpha(Hud.Cyan, 70), 4 * s);
                g.DrawPath(glow, path);
                using var fill = new SolidBrush(Hud.Alpha(Hud.Cyan, 70));
                g.FillPath(fill, path);
            }
            using var edge = new Pen(Enabled ? Hud.Cyan : Hud.Line, 1.5f * s);
            g.DrawPath(edge, path);
        }
        int d = (int)(10 * s);
        int dx = Checked ? knob.Right - d - (int)(3 * s) : knob.Left + (int)(3 * s);
        using (var thumb = new SolidBrush(!Enabled ? Hud.Line : Checked ? Color.White : Hud.Cyan))
            g.FillEllipse(thumb, dx, knob.Top + (kh - d) / 2, d, d);

        int x = knob.Right + (int)(12 * s);
        int titleH = TextRenderer.MeasureText(g, "Ag", Hud.Body).Height;
        int noteH = TextRenderer.MeasureText(g, "Ag", Hud.Mono).Height;
        int top = (Height - titleH - noteH) / 2;
        Color ink = Enabled ? Hud.Ink : Hud.Dim;

        TextRenderer.DrawText(g, Title, Hud.Body, new Point(x, top), ink, TextFormatFlags.NoPadding);
        if (Experimental)
        {
            int tw = TextRenderer.MeasureText(g, Title, Hud.Body, Size.Empty, TextFormatFlags.NoPadding).Width;
            var tagSize = TextRenderer.MeasureText(g, "EXPERIMENTAL", Hud.MonoSmall, Size.Empty, TextFormatFlags.NoPadding);
            var tag = new Rectangle(x + tw + (int)(8 * s), top + (titleH - tagSize.Height) / 2 - 1,
                                    tagSize.Width + (int)(6 * s), tagSize.Height + 2);
            if (tag.Right < Width - 4)
            {
                using var tp = new Pen(Hud.Alpha(Hud.Red, Enabled ? 220 : 110));
                g.DrawRectangle(tp, tag);
                TextRenderer.DrawText(g, "EXPERIMENTAL", Hud.MonoSmall, new Point(tag.X + (int)(3 * s), tag.Y + 1),
                    Enabled ? Hud.RedText : Hud.Alpha(Hud.RedText, 140), TextFormatFlags.NoPadding);
            }
        }
        string note = !Enabled && DisabledNote.Length > 0 ? DisabledNote : Note;
        TextRenderer.DrawText(g, note, Hud.Mono, new Rectangle(x, top + titleH, Width - x - 4, noteH), Hud.Dim,
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues)
        {
            using var f = new Pen(Hud.Cyan) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(f, 2, 2, Width - 5, Height - 5);
        }
    }

    private static GraphicsPath Pill(Rectangle r)
    {
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, r.Height, r.Height, 90, 180);
        p.AddArc(r.Right - r.Height, r.Top, r.Height, r.Height, 270, 180);
        p.CloseFigure();
        return p;
    }
}

/// <summary>The Terminator view: a dark red vision field with scan lines and an
/// occasional flicker. Everything about setup lives inside it.</summary>
internal sealed class HudVisionPanel : Panel
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 90 };
    private readonly Random _rng = new();
    private int _ticksToFlicker = 70, _flash;

    public HudVisionPanel()
    {
        Hud.DoubleBuffer(this);
        BackColor = Hud.RedGround;
        _timer.Tick += (_, _) =>
        {
            if (_flash > 0) { _flash--; Invalidate(); return; }
            if (--_ticksToFlicker > 0) return;
            _ticksToFlicker = 55 + _rng.Next(45);
            _flash = 2;
            Invalidate();
        };
        if (Hud.MotionEnabled) _timer.Start();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        var r = ClientRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;
        using (var bg = new LinearGradientBrush(r, Color.FromArgb(58, 8, 12), Hud.RedGround, LinearGradientMode.Vertical))
            g.FillRectangle(bg, r);
        using (var scan = new Pen(Color.FromArgb(20, 255, 42, 42)))
        {
            int step = Math.Max(3, (int)(3 * s));
            for (int y = 0; y < r.Height; y += step) g.DrawLine(scan, 0, y, r.Width, y);
        }
        if (_flash > 0)
        {
            using var f = new SolidBrush(Color.FromArgb(_flash == 2 ? 34 : 14, 255, 90, 90));
            g.FillRectangle(f, r);
        }
        using var edge = new Pen(Hud.RedLine);
        g.DrawRectangle(edge, 0, 0, r.Width - 1, r.Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The rolling system readout. Every row is a real value.</summary>
internal sealed class HudFeed : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    private string[] _rows = Array.Empty<string>();
    private float _offset;
    private readonly bool _motion = Hud.MotionEnabled;

    public HudFeed()
    {
        Hud.DoubleBuffer(this);
        BackColor = Color.FromArgb(18, 3, 6);
        _timer.Tick += (_, _) => { _offset += 0.5f * DeviceDpi / 96f; Invalidate(); };
        if (_motion) _timer.Start();
    }

    public void SetRows(IEnumerable<(string Key, string Value)> rows)
    {
        _rows = rows.Select(r => $"{r.Key.ToUpperInvariant()} {new string('.', Math.Max(2, 16 - r.Key.Length))} {r.Value.ToUpperInvariant()}").ToArray();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        g.Clear(BackColor);
        if (_rows.Length > 0)
        {
            int lineH = TextRenderer.MeasureText(g, "Ag", Hud.Mono).Height + (int)(3 * s);
            int total = lineH * _rows.Length;
            int off = _motion && total > Height ? (int)(_offset % total) : 0;
            int copies = _motion && total > Height ? 2 : 1;
            for (int c = 0; c < copies; c++)
                for (int i = 0; i < _rows.Length; i++)
                {
                    int y = (int)(4 * s) + i * lineH - off + c * total;
                    if (y + lineH < 0 || y > Height) continue;
                    TextRenderer.DrawText(g, _rows[i], Hud.Mono, new Point((int)(8 * s), y), Hud.RedText, TextFormatFlags.NoPadding);
                }
        }
        int fade = (int)(12 * s);
        if (Height > fade * 2)
        {
            using var top = new LinearGradientBrush(new Rectangle(0, 0, Width, fade + 1), BackColor, Hud.Alpha(BackColor, 0), LinearGradientMode.Vertical);
            g.FillRectangle(top, 0, 0, Width, fade);
            using var bottom = new LinearGradientBrush(new Rectangle(0, Height - fade - 1, Width, fade + 1), Hud.Alpha(BackColor, 0), BackColor, LinearGradientMode.Vertical);
            g.FillRectangle(bottom, 0, Height - fade, Width, fade);
        }
        using var edge = new Pen(Hud.RedLine);
        g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The three setup steps as a live checklist: done steps light cyan, the
/// current one is a pulsing red target, later ones wait in grey.</summary>
internal sealed class HudStepper : Control
{
    private readonly (string Title, string Hint, bool Done)[] _steps =
    {
        ("Pick the window to type into", "", false),
        ("Open Voiceitt and start speaking", "", false),
        ("Hover the transcript, press F8", "", false),
    };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private double _phase;

    public HudStepper()
    {
        Hud.DoubleBuffer(this, transparent: true);
        BackColor = Color.Transparent;
        _timer.Tick += (_, _) => { _phase = (_phase + 0.04) % 1.0; InvalidateActiveNode(); };
        if (Hud.MotionEnabled) _timer.Start();
    }

    public void SetStep(int index, string hint, bool done)
    {
        _steps[index] = (_steps[index].Title, hint, done);
        Invalidate();
    }

    private int ActiveIndex => Array.FindIndex(_steps, st => !st.Done);

    private int HeaderH => TextRenderer.MeasureText("Ag", Hud.Mono).Height + (int)(8 * DeviceDpi / 96f);
    private int RowH => (int)(40 * DeviceDpi / 96f);

    private void InvalidateActiveNode()
    {
        int a = ActiveIndex;
        if (a < 0) return;
        int s = (int)(DeviceDpi / 96f);
        Invalidate(new Rectangle(0, HeaderH + a * RowH - 12 * s, 48 * s, 40 * s));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int done = _steps.Count(st => st.Done);
        int active = ActiveIndex;

        TextRenderer.DrawText(g, "SETUP SEQUENCE", Hud.Mono, new Point((int)(10 * s), 0), Hud.RedText, TextFormatFlags.NoPadding);
        string count = $"{done} / 3";
        int cw = TextRenderer.MeasureText(g, count, Hud.Mono).Width;
        TextRenderer.DrawText(g, count, Hud.Mono, new Point(Width - cw - (int)(10 * s), 0), done == 3 ? Hud.Cyan : Hud.Dim, TextFormatFlags.NoPadding);

        int node = (int)(16 * s);
        int nx = (int)(12 * s);
        using (var rail = new Pen(Hud.Alpha(Hud.Cyan, 70), Math.Max(1, s)))
            g.DrawLine(rail, nx + node / 2, HeaderH + node, nx + node / 2, HeaderH + 2 * RowH);

        for (int i = 0; i < _steps.Length; i++)
        {
            var (title, hint, isDone) = _steps[i];
            int y = HeaderH + i * RowH;
            var n = new Rectangle(nx, y + (int)(1 * s), node, node);

            if (isDone)
            {
                using (var glow = new Pen(Hud.Alpha(Hud.Cyan, 70), 4 * s)) g.DrawEllipse(glow, n);
                using (var fill = new SolidBrush(Hud.Cyan)) g.FillEllipse(fill, n);
                using var check = new Pen(Hud.Ground, 2 * s);
                g.DrawLines(check, new[] {
                    new PointF(n.Left + 4.5f * s, n.Top + 8.5f * s),
                    new PointF(n.Left + 7f * s, n.Top + 11f * s),
                    new PointF(n.Left + 11.5f * s, n.Top + 5.5f * s) });
            }
            else if (i == active)
            {
                if (Hud.MotionEnabled)
                {
                    int grow = (int)(_phase * 9 * s);
                    using var ping = new Pen(Hud.Alpha(Hud.Red, (int)((1 - _phase) * 200)), 1.5f * s);
                    g.DrawRectangle(ping, Rectangle.Inflate(n, grow, grow));
                }
                using var b = new Pen(Hud.Red, 2 * s);
                g.DrawRectangle(b, n);
                using var core = new SolidBrush(Hud.Alpha(Hud.Red, 90));
                g.FillRectangle(core, Rectangle.Inflate(n, -(int)(5 * s), -(int)(5 * s)));
            }
            else
            {
                using var p = new Pen(Hud.Line, 1.5f * s);
                using var bg = new SolidBrush(Hud.RedGround);
                g.FillEllipse(bg, n);
                g.DrawEllipse(p, n);
            }

            int tx = nx + node + (int)(12 * s);
            string label = i == active ? "► " + title : title;
            Color ink = isDone ? Hud.Ink : i == active ? Hud.RedText : Hud.Dim;
            TextRenderer.DrawText(g, label, Hud.Body, new Rectangle(tx, y - (int)(2 * s), Width - tx - 6, (int)(20 * s)), ink,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            if (hint.Length > 0)
                TextRenderer.DrawText(g, hint, Hud.Mono, new Rectangle(tx, y + (int)(17 * s), Width - tx - 6, (int)(16 * s)), Hud.Dim,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A one-line status bar with a coloured frame: red when unlinked, cyan when linked.</summary>
internal sealed class HudStatus : Control
{
    private Color _accent = Hud.Red;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent { get => _accent; set { _accent = value; Invalidate(); } }

    public HudStatus()
    {
        Hud.DoubleBuffer(this, transparent: true);
        BackColor = Color.Transparent;
        Font = Hud.Mono;
    }

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var fill = new SolidBrush(Hud.Alpha(_accent, 22))) g.FillRectangle(fill, r);
        using (var p = new Pen(Hud.Alpha(_accent, 150))) g.DrawRectangle(p, r);
        TextRenderer.DrawText(g, "■ " + Text, Font, new Rectangle((int)(8 * s), 0, Width - (int)(16 * s), Height),
            _accent == Hud.Red ? Hud.RedText : _accent, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// The F8 indicator. A click-through, never-focused overlay drawn on top of whatever
/// is on screen — right where the user is looking when they press F8, which is the
/// browser, not this app's corner window.
///
/// Terminator first: a red box snaps around the element that was read, a scan bar
/// sweeps it, a readout types what was found. Then Jarvis: on success a cyan ring
/// spins in at the pointer and the box turns cyan — TARGET ACQUIRED. On a miss the
/// red box glitches and says why.
///
/// Drawn with UpdateLayeredWindow and per-pixel alpha, so glows and the fade-out are
/// real transparency, and WS_EX_TRANSPARENT lets every click fall through to the
/// page underneath.
/// </summary>
internal sealed class TargetOverlay : Form
{
    private readonly Rectangle _target;
    private readonly Point _cursor;
    private readonly bool _ok;
    private readonly string[] _readout;
    private readonly string _tag;
    private readonly float _s;
    private readonly bool _motion = Hud.MotionEnabled;
    private readonly Stopwatch _clock = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private Rectangle _readoutRect, _tagRect;
    private static TargetOverlay? _current;
    private static readonly Font ReadFont = new("Consolas", 9f, FontStyle.Bold);

    private double Total => _motion ? 2.6 : 1.4;

    public static void Play(Rectangle target, Point cursor, bool ok, string[] readout, float scale)
    {
        _current?.Close();
        _current = new TargetOverlay(target, cursor, ok, readout, scale);
        _current.Show();
    }

    private TargetOverlay(Rectangle target, Point cursor, bool ok, string[] readout, float scale)
    {
        _target = target;
        _cursor = cursor;
        _ok = ok;
        _readout = readout;
        _s = scale;
        _tag = ok ? "TARGET ACQUIRED" : "NO MATCH — POINT AT THE TEXT";

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;

        var area = Screen.FromPoint(cursor).Bounds;
        using var measure = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(measure);
        int lineH = (int)(16 * _s);
        int w = (int)(_readout.Select(l => mg.MeasureString(l, ReadFont).Width).DefaultIfEmpty(0).Max() + 20 * _s);
        int h = _readout.Length * lineH + (int)(12 * _s);
        int ry = _target.Bottom + (int)(10 * _s);
        if (ry + h > area.Bottom) ry = _target.Top - (int)(10 * _s) - h;
        _readoutRect = new Rectangle(_target.Left, ry, w, h);

        var tagSize = mg.MeasureString(_tag, ReadFont);
        int tw = (int)(tagSize.Width + 14 * _s), th = (int)(tagSize.Height + 6 * _s);
        int ty = _target.Top - th - (int)(8 * _s);
        if (ty < area.Top) ty = _target.Top + (int)(6 * _s);
        _tagRect = new Rectangle(Math.Max(_target.Left, _target.Right - tw), ty, tw, th);

        int ring = (int)(60 * _s);
        var bounds = Rectangle.Union(Rectangle.Inflate(_target, (int)(14 * _s), (int)(14 * _s)), _readoutRect);
        bounds = Rectangle.Union(bounds, _tagRect);
        bounds = Rectangle.Union(bounds, new Rectangle(cursor.X - ring, cursor.Y - ring, ring * 2, ring * 2));
        bounds.Inflate((int)(6 * _s), (int)(6 * _s));
        bounds.Intersect(SystemInformation.VirtualScreen);
        Bounds = bounds;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // LAYERED | TRANSPARENT (clicks pass through) | TOOLWINDOW | NOACTIVATE | TOPMOST
            cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000 | 0x00000008;
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _clock.Start();
        _timer.Tick += (_, _) => Frame();
        _timer.Start();
        Frame();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        if (_current == this) _current = null;
        base.OnFormClosed(e);
    }

    private void Frame()
    {
        double t = _clock.Elapsed.TotalSeconds;
        if (t >= Total) { _timer.Stop(); Close(); return; }
        if (Width <= 0 || Height <= 0) return;

        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Draw(g, _motion ? t : 1.6);
        }
        double fade = t < 0.1 ? t / 0.1 : t > Total - 0.4 ? (Total - t) / 0.4 : 1;
        Push(bmp, (byte)(255 * Math.Clamp(fade, 0, 1)));
    }

    private Rectangle Local(Rectangle r) => new(r.X - Left, r.Y - Top, r.Width, r.Height);

    private void Draw(Graphics g, double t)
    {
        var box = Local(_target);
        box.Inflate((int)(6 * _s), (int)(4 * _s));

        double glitchAlpha = 1;
        if (!_ok && t < 1.7)
        {
            bool odd = (int)(t / 0.09) % 2 == 1;
            box.Offset(odd ? (int)(3 * _s) : -(int)(2 * _s), 0);
            glitchAlpha = odd ? 0.35 : 1;
        }
        Color boxColor = _ok ? Hud.Mix(Hud.Red, Hud.Cyan, (t - 0.9) / 0.25) : Hud.Red;
        int a = (int)(255 * glitchAlpha);

        // Glowing frame.
        using (var g1 = new Pen(Hud.Alpha(boxColor, a / 6), 8 * _s)) g.DrawRectangle(g1, box);
        using (var g2 = new Pen(Hud.Alpha(boxColor, a / 3), 4 * _s)) g.DrawRectangle(g2, box);
        using (var g3 = new Pen(Hud.Alpha(boxColor, a), 1.5f * _s)) g.DrawRectangle(g3, box);

        // Heavy corner brackets.
        using (var br = new Pen(Hud.Alpha(boxColor, a), 3.5f * _s))
        {
            int l = (int)(16 * _s);
            g.DrawLines(br, new[] { new Point(box.Left, box.Top + l), new Point(box.Left, box.Top), new Point(box.Left + l, box.Top) });
            g.DrawLines(br, new[] { new Point(box.Right - l, box.Top), new Point(box.Right, box.Top), new Point(box.Right, box.Top + l) });
            g.DrawLines(br, new[] { new Point(box.Left, box.Bottom - l), new Point(box.Left, box.Bottom), new Point(box.Left + l, box.Bottom) });
            g.DrawLines(br, new[] { new Point(box.Right - l, box.Bottom), new Point(box.Right, box.Bottom), new Point(box.Right, box.Bottom - l) });
        }

        // Scan bar, sweeping down and back twice.
        if (_motion && t < 1.4)
        {
            double p = (t % 0.7) / 0.7;
            double tri = p < 0.5 ? p * 2 : (1 - p) * 2;
            int y = box.Top + (int)(tri * box.Height);
            using var glow = new Pen(Hud.Alpha(Hud.Red, 90), 6 * _s);
            using var bar = new Pen(Hud.Red, 2 * _s);
            g.DrawLine(glow, box.Left, y, box.Right, y);
            g.DrawLine(bar, box.Left, y, box.Right, y);
        }

        // Readout, typed out.
        var rr = Local(_readoutRect);
        using (var bg = new SolidBrush(Color.FromArgb(225, 18, 3, 6))) g.FillRectangle(bg, rr);
        using (var edge = new Pen(Hud.Alpha(boxColor, 200), 1.2f * _s)) g.DrawRectangle(edge, rr);
        int totalChars = _readout.Sum(l => l.Length);
        int shown = _motion ? (int)(Math.Clamp((t - 0.1) / 0.7, 0, 1) * totalChars) : totalChars;
        Color readInk = _ok && t > 1.0 ? Hud.Cyan : Hud.RedText;
        using (var ink = new SolidBrush(readInk))
        {
            int y = rr.Top + (int)(6 * _s);
            foreach (var line in _readout)
            {
                if (shown <= 0) break;
                string part = line.Length <= shown ? line : line[..shown];
                g.DrawString(part, ReadFont, ink, rr.Left + 10 * _s, y);
                shown -= line.Length;
                y += (int)(16 * _s);
            }
        }

        // Jarvis lock-on at the pointer.
        if (_ok && t > 0.8)
        {
            double p = Math.Clamp((t - 0.8) / 0.3, 0, 1);
            double ease = 1 - Math.Pow(1 - p, 3);
            float radius = (float)(22 * _s * (2.2 - 1.2 * ease));
            var c = new PointF(_cursor.X - Left, _cursor.Y - Top);
            var state = g.Save();
            g.TranslateTransform(c.X, c.Y);
            g.RotateTransform((float)(t * 300 % 360));
            using (var ring = new Pen(Hud.Alpha(Hud.Cyan, (int)(255 * p)), 2.5f * _s) { DashPattern = new[] { 4f, 2.5f } })
                g.DrawEllipse(ring, -radius, -radius, radius * 2, radius * 2);
            using (var outer = new Pen(Hud.Alpha(Hud.Cyan, (int)(110 * p)), 1f * _s) { DashPattern = new[] { 1f, 3f } })
                g.DrawEllipse(outer, -radius * 1.35f, -radius * 1.35f, radius * 2.7f, radius * 2.7f);
            g.Restore(state);
            using var tick = new Pen(Hud.Alpha(Hud.Cyan, (int)(255 * p)), 2 * _s);
            float r1 = radius + 4 * _s, r2 = radius + 12 * _s;
            g.DrawLine(tick, c.X, c.Y - r1, c.X, c.Y - r2);
            g.DrawLine(tick, c.X, c.Y + r1, c.X, c.Y + r2);
            g.DrawLine(tick, c.X - r1, c.Y, c.X - r2, c.Y);
            g.DrawLine(tick, c.X + r1, c.Y, c.X + r2, c.Y);
        }

        // Verdict tag.
        if ((_ok && t > 1.0) || (!_ok && t > 0.3))
        {
            var tr = Local(_tagRect);
            Color tc = _ok ? Hud.Ok : Hud.Red;
            using (var bg = new SolidBrush(Color.FromArgb(230, 4, 8, 15))) g.FillRectangle(bg, tr);
            using (var p = new Pen(tc, 1.5f * _s)) g.DrawRectangle(p, tr);
            using var ink = new SolidBrush(tc);
            g.DrawString(_tag, ReadFont, ink, tr.Left + 7 * _s, tr.Top + 3 * _s);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Pt { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Sz { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Blend { public byte Op, Flags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Pt pptDst, ref Sz psize,
        IntPtr hdcSrc, ref Pt pptSrc, int crKey, ref Blend pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private void Push(Bitmap bmp, byte alpha)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hbmp);
        try
        {
            var dst = new Pt { X = Left, Y = Top };
            var size = new Sz { Cx = bmp.Width, Cy = bmp.Height };
            var src = new Pt();
            var blend = new Blend { Op = 0, Flags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}

/// <summary>GPU memory for the readout, from nvidia-smi. "n/a" without an NVIDIA GPU.</summary>
internal static class GpuProbe
{
    private static bool _unavailable;

    public static async Task<string> ReadAsync()
    {
        if (_unavailable) return "n/a";
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var parts = output.Split('\n')[0].Split(',');
            if (parts.Length < 2) return "n/a";
            double used = double.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture) / 1024;
            double total = double.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture) / 1024;
            return $"{used:0.0} / {total:0} GB";
        }
        catch
        {
            _unavailable = true;
            return "n/a";
        }
    }
}
