namespace VoiceOS;

/// <summary>
/// The practice screen: the command to say, in large letters, and whether it was understood.
/// On a miss it shows exactly what Voiceitt wrote, which is what decides whether a command
/// needs different words. Like the command panel, it never takes the focus.
/// </summary>
internal sealed class PracticeForm : Form
{
    private readonly Label _progress, _group, _say, _also, _result, _hint;

    private static Label Make(float size, Color color, int height, bool bold = false) => new()
    {
        Dock = DockStyle.Top,
        Height = height,
        Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size),
        ForeColor = color,
        TextAlign = ContentAlignment.MiddleCenter,
        AutoEllipsis = true,
    };

    public PracticeForm()
    {
        Text = "Practice commands";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Hud.Ground;
        Padding = new Padding(20, 14, 20, 14);
        Size = new Size(760, 440);
        if (Hud.AppIcon != null) Icon = Hud.AppIcon;

        _hint = Make(11, Hud.Dim, 40);
        _hint.Dock = DockStyle.Bottom;
        _hint.Text = "Nothing is carried out while practising  ·  say “skip” to move on  ·  “stop practice” to finish";
        _result = Make(17, Hud.Ink, 110, bold: true);
        _also = Make(11, Hud.Dim, 34);
        _say = Make(34, Hud.Ink, 90, bold: true);
        _group = Make(12, Hud.Cyan, 30, bold: true);
        _progress = Make(11, Hud.Dim, 30);

        var close = new Label
        {
            Text = "✕", Font = new Font("Segoe UI Semibold", 13f), ForeColor = Hud.Dim,
            AutoSize = true, Cursor = Cursors.Hand, Location = new Point(Width - 40, 8),
        };
        close.Click += (_, _) => Hide();

        // Added bottom-up: WinForms docks the last-added Top control highest.
        Controls.Add(_result);
        Controls.Add(_also);
        Controls.Add(_say);
        Controls.Add(_group);
        Controls.Add(_progress);
        Controls.Add(_hint);
        Controls.Add(close);
        close.BringToFront();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Hud.Cyan, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }

    /// <summary>Show the current command, and the result of the last attempt if there was one.</summary>
    public void ShowState(PracticeSession session, PracticeAttempt? last)
    {
        if (last != null)
        {
            (_result.Text, _result.ForeColor) = last.Outcome switch
            {
                PracticeOutcome.Right => ($"✓  Got it: “{last.Heard}”", Hud.Ok),
                PracticeOutcome.OtherCommand =>
                    ($"✗  Voiceitt wrote “{last.Heard}”\nThat would be “{last.MatchedInstead!.Phrases[0]}”. Try again.", Hud.RedText),
                PracticeOutcome.NotUnderstood => ($"✗  Voiceitt wrote “{last.Heard}”\nNot understood. Try again, or say “skip”.", Hud.RedText),
                PracticeOutcome.Skipped => ("Skipped.", Hud.Dim),
                _ => ("", Hud.Ink),
            };
        }
        else
        {
            _result.Text = "";
        }

        if (session.Done)
        {
            ShowSummary(session);
        }
        else
        {
            var cmd = session.Current!;
            _progress.Text = $"Command {session.Index + 1} of {session.Count}   ·   {session.Right} right so far";
            _group.Text = (cmd.Group ?? "").ToUpperInvariant();
            _say.Text = cmd.Number is int n ? $"Say:  {n}   or   {cmd.Phrases[0]}" : $"Say:  {cmd.Phrases[0]}";
            _also.Text = cmd.Phrases.Count > 1 ? "also works: " + string.Join(", ", cmd.Phrases.Skip(1).Take(4)) : "";
        }

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 3);
        if (!Visible) Show();
    }

    private void ShowSummary(PracticeSession session)
    {
        _progress.Text = "Practice finished";
        _group.Text = "";
        _say.Text = $"{session.Right} right of {session.Attempts} tries";
        var missed = session.Misses.Select(m => m.Target?.Phrases[0]).Where(p => p != null).Distinct().ToList();
        _also.Text = "";
        _result.ForeColor = missed.Count == 0 ? Hud.Ok : Hud.Ink;
        _result.Text = missed.Count == 0
            ? "Every command was understood."
            : "Needed another try:  " + string.Join(",  ", missed.Take(12)) + (missed.Count > 12 ? " …" : "")
              + "\nTell Claude — these may need different words.";
        _hint.Text = "Click ✕ to close  ·  “Computer, practice commands” to start again";
    }
}
