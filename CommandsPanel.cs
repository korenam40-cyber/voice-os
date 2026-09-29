namespace VoiceOS;

/// <summary>
/// The list of voice commands, always in view, so they never have to be remembered. The user
/// asked for it on 2026-09-28: "I don't remember all the commands".
///
/// It never takes the keyboard focus. The user dictates into another window, and a panel
/// that stole focus would make the next words land in the wrong place. Each command is
/// shown with the words that worked for this user in the Voiceitt tests (the first phrase
/// in commands.json), not the textbook name: "grab that", not "copy".
///
/// When Baby has her own window, this list moves into it.
/// </summary>
internal sealed class CommandsPanel : Form
{
    private readonly FlowLayoutPanel _flow;
    private readonly Label _header;
    private readonly Label _status;

    private static readonly Font GroupFont = new("Segoe UI Semibold", 11f);
    private static readonly Font ItemFont = new("Segoe UI", 11f);
    private static readonly Font HeaderFont = new("Segoe UI Semibold", 13f);

    public CommandsPanel()
    {
        Text = "Voice commands";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Hud.Ground;
        ForeColor = Hud.Ink;
        Opacity = 0.93;
        Padding = new Padding(14, 10, 14, 10);
        if (Hud.AppIcon != null) Icon = Hud.AppIcon;

        _header = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Font = HeaderFont,
            ForeColor = Hud.Cyan,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var close = new Label
        {
            Text = "✕",
            Dock = DockStyle.Right,
            Width = 28,
            Font = HeaderFont,
            ForeColor = Hud.Dim,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
        };
        close.Click += (_, _) => Hide();
        _header.Controls.Add(close);

        // The connection light (user's request, 2026-09-29): is Voiceitt connected, and
        // where does dictation go -- visible without opening the main window.
        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Font = GroupFont,
            ForeColor = Hud.Dim,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Text = "●  Checking the connection to Voiceitt…",
        };

        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = true,
            AutoScroll = false,
            BackColor = Hud.Ground,
        };

        var footer = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Font = ItemFont,
            ForeColor = Hud.Dim,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "“Computer, hide commands” closes this · “Computer, show commands” brings it back",
        };

        Controls.Add(_flow);
        Controls.Add(footer);
        Controls.Add(_status);
        Controls.Add(_header);
    }

    /// <summary>Never take focus: the user is dictating into another window.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Hud.Line, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }

    /// <summary>
    /// The connection light. Green: Voiceitt connected and typing. Yellow: connected, but
    /// typing is off (commands only). Red: not connected, or paused.
    /// </summary>
    public void SetStatus(bool connected, bool paused, bool typing, string target)
    {
        string text; Color color;
        if (!connected)
        {
            (text, color) = ("●  Voiceitt NOT connected — open Voiceitt in Chrome, or reload its tab (F5)", Hud.RedText);
        }
        else if (paused)
        {
            (text, color) = ("●  Voiceitt connected — PAUSED (Ctrl+Alt+P to resume)", Hud.RedText);
        }
        else if (!typing)
        {
            (text, color) = ("●  Voiceitt connected — typing OFF, listening for commands", Color.FromArgb(255, 205, 80));
        }
        else
        {
            (text, color) = ($"●  Voiceitt connected — typing into {target}", Hud.Ok);
        }
        if (_status.Text == text && _status.ForeColor == color) return;
        _status.Text = text;
        _status.ForeColor = color;
    }

    /// <summary>Fill the panel from the loaded commands and size it to fit the screen.</summary>
    public void ShowCommands(CommandSet set)
    {
        string prefix = set.Prefixes.Count > 0 ? set.Prefixes[0] : "";
        _header.Text = prefix.Length > 0
            ? $"Say “{char.ToUpperInvariant(prefix[0])}{prefix[1..]}”, then:"
            : "Voice commands";

        _flow.SuspendLayout();
        _flow.Controls.Clear();
        foreach (var group in set.Commands.GroupBy(c => c.Group ?? "Other"))
        {
            var box = new Panel { AutoSize = true, Margin = new Padding(0, 4, 26, 10), BackColor = Hud.Ground };
            var title = new Label
            {
                Text = group.Key.ToUpperInvariant(),
                Font = GroupFont,
                ForeColor = Hud.Cyan,
                AutoSize = true,
                Location = new Point(0, 0),
            };
            var items = new Label
            {
                Text = string.Join("\n", group.Select(c => c.Number is int n ? $"{n,2}   {c.Phrases[0]}" : $"      {c.Phrases[0]}")),
                Font = ItemFont,
                ForeColor = Hud.Ink,
                AutoSize = true,
                Location = new Point(2, title.PreferredHeight + 2),
            };
            box.Controls.Add(title);
            box.Controls.Add(items);
            _flow.Controls.Add(box);
        }
        _flow.ResumeLayout();

        // Tall enough for the longest group, as tall as the screen allows, then as many
        // columns as that takes. Placed on the right edge, where it covers the least.
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int chrome = Padding.Vertical + 34 + 30 + 26 + 8;
        int tallest = _flow.Controls.Cast<Control>().Max(c => c.PreferredSize.Height + c.Margin.Vertical);
        int height = Math.Min(area.Height - 40, Math.Max(tallest + chrome, 420));
        int columnHeight = height - chrome;
        int columns = 1, used = 0, widest = 0, total = 0;
        foreach (Control c in _flow.Controls)
        {
            int h = c.PreferredSize.Height + c.Margin.Vertical;
            if (used + h > columnHeight && used > 0) { columns++; total += widest; used = 0; widest = 0; }
            used += h;
            widest = Math.Max(widest, c.PreferredSize.Width + c.Margin.Horizontal);
        }
        total += widest;
        int width = Math.Min(area.Width - 40, Math.Max(total + Padding.Horizontal + 4, 560));
        SetBounds(area.Right - width - 12, area.Top + 20, width, height);

        if (!Visible) Show();
        Invalidate();
    }
}
