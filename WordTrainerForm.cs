using System.Drawing.Drawing2D;

namespace VoiceOS;

/// <summary>
/// "Teach a word" — say one word a few times, type what it should be, and the
/// system learns that word.
///
/// Asked for on 2026-09-20: some words come out wrong every single time, and
/// fixing them one dictation at a time never catches up. Here the user names the
/// word, records it, and the takes become training pairs for exactly that word.
///
/// Two things happen when they press Save, on different timescales, and the
/// window says both plainly:
///   * the word joins the personal vocabulary, so the correction layer can
///     produce it from the next dictation onwards;
///   * the recordings become training data, which only changes the model itself
///     at the next training round.
/// Claiming "the model has learned it" the moment Save is pressed would be a lie
/// the user would discover the hard way, mid-sentence.
///
/// The word alone comes first, because that is the sound being taught. The rest
/// put it in a short sentence: a model fed only isolated words gets worse at
/// running speech, where words are joined to their neighbours. Every line is
/// editable — what the user would really say beats anything generated here.
/// </summary>
internal sealed class WordTrainerForm : Form
{
    private sealed class Row
    {
        public required TextBox Box;
        public required HudButton Record;
        public required Label State;
        public int TakeIndex = -1;
    }

    private readonly LocalDictationClient _client;
    private readonly List<Row> _rows = new();
    private readonly TextBox _wordBox = new();
    private readonly HudButton _setButton = new();
    private readonly Panel _takesPanel = new();
    private readonly HudStatus _status = new();
    private readonly HudButton _saveButton = new();
    private readonly HudButton _cancelButton = new();
    private readonly Label _headline = new();
    private readonly Label _subline = new();

    private string _word = "";
    private bool _recording;
    private bool _busy;
    private bool _saved;

    /// <summary>What happened, for the main window's log.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Summary { get; private set; } = "";

    public WordTrainerForm(LocalDictationClient client)
    {
        _client = client;

        Text = "Teach a word";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        BackColor = Hud.Ground;
        ForeColor = Hud.Ink;
        Font = Hud.Body;
        ClientSize = new Size(S(620), S(600));
        KeyPreview = true;
        // The main window is always-on-top, so without this its own dialog opens
        // behind it — the user sees nothing happen when they press Ctrl+Alt+W.
        TopMost = true;
        if (Hud.AppIcon != null) Icon = Hud.AppIcon;
        Hud.DoubleBuffer(this);

        // -- header: the same red vision strip as the main window -----------
        var vision = new HudVisionPanel { Dock = DockStyle.Top, Height = S(78) };
        _headline.Text = "WORD TRAINING";
        _headline.Font = new Font("Consolas", 13.5f, FontStyle.Bold);
        _headline.ForeColor = Hud.RedText;
        _headline.AutoSize = true;
        _headline.BackColor = Color.Transparent;
        _headline.Location = new Point(S(16), S(12));
        _subline.Text = "SAY IT A FEW TIMES / TYPE WHAT IT SHOULD BE";
        _subline.Font = Hud.MonoSmall;
        _subline.ForeColor = Hud.Alpha(Hud.RedText, 190);
        _subline.AutoSize = true;
        _subline.BackColor = Color.Transparent;
        _subline.Location = new Point(S(18), S(44));
        vision.Controls.Add(_headline);
        vision.Controls.Add(_subline);

        // -- the word itself ------------------------------------------------
        var wordPanel = new Panel { Dock = DockStyle.Top, Height = S(84), BackColor = Hud.Ground };
        var wordLabel = new Label
        {
            Text = "THE WORD, SPELLED THE WAY YOU WANT IT TYPED",
            Font = Hud.MonoSmall,
            ForeColor = Hud.Dim,
            AutoSize = true,
            Location = new Point(S(16), S(10)),
        };
        _wordBox.Location = new Point(S(16), S(32));
        _wordBox.Size = new Size(S(420), S(34));
        _wordBox.BackColor = Color.FromArgb(9, 17, 28);
        _wordBox.ForeColor = Hud.Ink;
        _wordBox.BorderStyle = BorderStyle.FixedSingle;
        _wordBox.Font = new Font("Segoe UI", 14f);
        _wordBox.RightToLeft = RightToLeft.Yes;   // Hebrew, like every other Hebrew field
        _wordBox.TextChanged += (_, _) => UpdateButtons();

        _setButton.Text = "SET WORD";
        _setButton.Location = new Point(S(448), S(32));
        _setButton.Size = new Size(S(150), S(34));
        _setButton.Click += async (_, _) => await SetWordAsync();

        wordPanel.Controls.Add(wordLabel);
        wordPanel.Controls.Add(_wordBox);
        wordPanel.Controls.Add(_setButton);

        // -- the takes ------------------------------------------------------
        _takesPanel.Dock = DockStyle.Fill;
        _takesPanel.AutoScroll = true;
        _takesPanel.BackColor = Hud.Ground;
        _takesPanel.Padding = new Padding(S(12), S(8), S(12), S(8));
        Hud.DoubleBuffer(_takesPanel);
        _takesPanel.Paint += (_, e) => DrawEmptyState(e.Graphics);

        // -- status and buttons ---------------------------------------------
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(104), BackColor = Hud.Ground };
        _status.Dock = DockStyle.Top;
        _status.Height = S(40);
        SetStatus("TYPE THE WORD, THEN SET WORD", Hud.Dim);

        _saveButton.Text = "SAVE AND TEACH";
        _saveButton.Primary = true;
        _saveButton.Size = new Size(S(230), S(44));
        _saveButton.Location = new Point(S(370), S(50));
        _saveButton.Enabled = false;
        _saveButton.Click += async (_, _) => await SaveAsync();

        _cancelButton.Text = "CANCEL";
        _cancelButton.Size = new Size(S(140), S(44));
        _cancelButton.Location = new Point(S(20), S(50));
        _cancelButton.Click += (_, _) => Close();

        bottom.Controls.Add(_saveButton);
        bottom.Controls.Add(_cancelButton);
        bottom.Controls.Add(_status);

        Controls.Add(_takesPanel);
        Controls.Add(bottom);
        Controls.Add(wordPanel);
        Controls.Add(vision);

        CancelButton = _cancelButton;
        UpdateButtons();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Hud.DarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _wordBox.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Enter in the word box sets the word instead of closing the dialog.
        if (keyData == Keys.Enter && _wordBox.Focused && !_busy)
        {
            _ = SetWordAsync();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        // Recordings held by the service are never on disk, but leaving them
        // there would attach them to the NEXT word taught. Clear them.
        if (!_saved && (_rows.Exists(r => r.TakeIndex >= 0) || _word.Length > 0))
        {
            if (_recording) await _client.WordRecordStopAsync(_word);
            await _client.WordDiscardAsync();
        }
        base.OnFormClosing(e);
    }

    // -- steps ---------------------------------------------------------------
    private async Task SetWordAsync()
    {
        string word = _wordBox.Text.Trim();
        if (word.Length == 0 || _busy) return;

        _busy = true;
        UpdateButtons();
        var (prompts, error) = await _client.WordStartAsync(word);
        _busy = false;

        if (error != null)
        {
            SetStatus(error.Contains("dictation") ? "STOP THE DICTATION FIRST" : $"ERROR: {Upper(error)}",
                      Hud.RedText);
            UpdateButtons();
            return;
        }

        _word = word;
        _headline.Text = $"TEACHING: {word}";
        BuildRows(prompts);
        SetStatus($"RECORD EACH LINE / 0 OF {_rows.Count} TAKES", Hud.Cyan);
        UpdateButtons();
    }

    private void BuildRows(List<LocalDictationClient.WordPrompt> prompts)
    {
        _takesPanel.Controls.Clear();
        _rows.Clear();

        int y = S(6);
        for (int i = 0; i < prompts.Count; i++)
        {
            var prompt = prompts[i];
            var number = new Label
            {
                Text = $"{i + 1:00}",
                Font = Hud.Mono,
                ForeColor = prompt.Alone ? Hud.Cyan : Hud.Dim,
                AutoSize = true,
                Location = new Point(S(4), y + S(12)),
            };
            var kind = new Label
            {
                Text = prompt.Alone ? "THE WORD ALONE" : "IN A SENTENCE — EDIT IT TO WHAT YOU'D SAY",
                Font = Hud.MonoSmall,
                ForeColor = Hud.Alpha(Hud.Dim, 190),
                AutoSize = true,
                Location = new Point(S(36), y),
            };
            var box = new TextBox
            {
                Text = prompt.Text,
                Location = new Point(S(36), y + S(18)),
                Size = new Size(S(390), S(30)),
                BackColor = Color.FromArgb(9, 17, 28),
                ForeColor = Hud.Ink,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 12f),
                RightToLeft = RightToLeft.Yes,
            };
            var record = new HudButton
            {
                Text = "RECORD",
                Location = new Point(S(436), y + S(16)),
                Size = new Size(S(130), S(34)),
            };
            var state = new Label
            {
                Text = "NOT RECORDED",
                Font = Hud.MonoSmall,
                ForeColor = Hud.Dim,
                AutoSize = true,
                MaximumSize = new Size(S(560), 0),
                Location = new Point(S(36), y + S(52)),
            };

            var row = new Row { Box = box, Record = record, State = state };
            record.Click += async (_, _) => await ToggleTakeAsync(row);
            _rows.Add(row);

            _takesPanel.Controls.Add(number);
            _takesPanel.Controls.Add(kind);
            _takesPanel.Controls.Add(box);
            _takesPanel.Controls.Add(record);
            _takesPanel.Controls.Add(state);
            y += S(86);
        }
        _takesPanel.Invalidate();
    }

    private async Task ToggleTakeAsync(Row row)
    {
        if (_busy) return;

        if (_recording)
        {
            // Second press on the SAME row ends its take; on another row it is
            // ignored, so two takes can never overlap on one microphone.
            if (!row.Record.Active) return;

            _busy = true;
            var take = await _client.WordRecordStopAsync(row.Box.Text.Trim());
            _recording = false;
            _busy = false;
            row.Record.Active = false;
            row.Record.Text = "RECORD AGAIN";

            if (!take.Kept)
            {
                row.State.Text = take.Reason switch
                {
                    "no_speech" => "NOTHING HEARD — TRY AGAIN, CLOSER TO THE MICROPHONE",
                    "too_short" => "TOO SHORT — HOLD THE RECORDING A MOMENT LONGER",
                    null => "NOT KEPT",
                    _ => $"NOT KEPT: {Upper(take.Reason)}",
                };
                row.State.ForeColor = Hud.RedText;
            }
            else
            {
                row.TakeIndex = NextTakeIndex();
                row.State.Text = take.Matched
                    ? $"KEPT {take.Seconds:0.0}s / THE MODEL ALREADY GETS THIS ONE RIGHT"
                    : $"KEPT {take.Seconds:0.0}s / IT HEARD: {take.Heard}";
                row.State.ForeColor = take.Matched ? Hud.Ok : Hud.Cyan;
            }
            UpdateProgress();
            UpdateButtons();
            return;
        }

        if (row.Box.Text.Trim().Length == 0)
        {
            SetStatus("THAT LINE IS EMPTY", Hud.RedText);
            return;
        }

        // Re-recording a line replaces its take, so a line never counts twice.
        if (row.TakeIndex >= 0)
        {
            _busy = true;
            await _client.WordDropAsync(row.TakeIndex);
            DropIndex(row.TakeIndex);
            row.TakeIndex = -1;
            _busy = false;
        }

        _busy = true;
        string? error = await _client.WordRecordStartAsync();
        _busy = false;
        if (error != null)
        {
            SetStatus($"COULD NOT RECORD: {Upper(error)}", Hud.RedText);
            return;
        }
        _recording = true;
        row.Record.Active = true;
        row.Record.Text = "STOP";
        row.State.Text = "RECORDING — SAY IT, THEN PRESS STOP";
        row.State.ForeColor = Hud.RedText;
        SetStatus("RECORDING", Hud.Red);
        UpdateButtons();
    }

    private async Task SaveAsync()
    {
        if (_busy || _recording) return;
        _busy = true;
        UpdateButtons();
        var (saved, vocabulary, error) = await _client.WordSaveAsync();
        _busy = false;

        if (error != null)
        {
            SetStatus($"NOT SAVED: {Upper(error)}", Hud.RedText);
            UpdateButtons();
            return;
        }

        _saved = true;
        Summary = $"Taught “{_word}”: {saved} recording(s) saved for training" +
                  (vocabulary ? ", word added to your vocabulary." : ".");
        DialogResult = DialogResult.OK;
        Close();
    }

    // -- bookkeeping ---------------------------------------------------------
    private int NextTakeIndex()
    {
        // The service appends takes to one list, so a new take's index is the
        // number of takes already held.
        int n = 0;
        foreach (var r in _rows) if (r.TakeIndex >= 0) n++;
        return n;
    }

    private void DropIndex(int dropped)
    {
        // The service closes the gap, so every later take shifts down by one.
        foreach (var r in _rows)
            if (r.TakeIndex > dropped) r.TakeIndex--;
    }

    private int TakeCount()
    {
        int n = 0;
        foreach (var r in _rows) if (r.TakeIndex >= 0) n++;
        return n;
    }

    private void UpdateProgress()
    {
        int n = TakeCount();
        if (n == 0) SetStatus($"RECORD EACH LINE / 0 OF {_rows.Count} TAKES", Hud.Cyan);
        else if (n < 4)
            SetStatus($"{n} OF {_rows.Count} TAKES / {4 - n} MORE FOR A GOOD LESSON", Hud.Cyan);
        else
            SetStatus($"{n} OF {_rows.Count} TAKES / READY TO SAVE", Hud.Ok);
    }

    private void UpdateButtons()
    {
        _setButton.Enabled = !_busy && !_recording && _wordBox.Text.Trim().Length > 0;
        _wordBox.Enabled = !_recording && !_busy;
        _saveButton.Enabled = !_busy && !_recording && TakeCount() > 0;
        _cancelButton.Enabled = !_busy;
        foreach (var r in _rows)
            r.Record.Enabled = !_busy && (!_recording || r.Record.Active);
    }

    private void SetStatus(string text, Color accent)
    {
        _status.Text = text;
        _status.Accent = accent;
    }

    private void DrawEmptyState(Graphics g)
    {
        if (_rows.Count > 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(S(20), S(30), _takesPanel.Width - S(40), S(150));
        using var pen = new Pen(Hud.Alpha(Hud.Line, 200)) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, rect);
        string[] lines =
        {
            "AWAITING A WORD.",
            "",
            "Type the word above the way it should be written, then SET WORD.",
            "You'll record it a few times: alone first, then in short sentences",
            "you can rewrite into whatever you'd really say.",
            "",
            "Saving adds the word to your vocabulary straight away. The model",
            "itself learns it at the next training round.",
        };
        using var brush = new SolidBrush(Hud.Dim);
        using var head = new SolidBrush(Hud.Cyan);
        float y = rect.Y + S(14);
        for (int i = 0; i < lines.Length; i++)
        {
            g.DrawString(lines[i], i == 0 ? Hud.Mono : Hud.MonoSmall,
                         i == 0 ? head : brush, rect.X + S(14), y);
            y += i == 0 ? S(22) : S(15);
        }
    }

    private static string Upper(string s) => s.ToUpperInvariant();

    private int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);
}
