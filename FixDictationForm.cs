using System.Text;

namespace VoiceOS;

/// <summary>
/// "What did you actually say?" — the review screen for the last dictation.
///
/// This is where training data comes from now. Reading prompts aloud can only
/// teach the model sentences someone wrote in advance; a dictation the user
/// fixes teaches it the words they really use, with the errors they really make.
/// Published work on dysarthric ASR found user corrections to be the most
/// valuable data of all — adding 8.8 h of them to 92 h of read speech cut error
/// on spontaneous speech from 16.1% to 7.8%.
///
/// One row per phrase, because that is the unit the recognizer works in and the
/// unit the audio was cut into. A row is saved only when its box is ticked, and
/// editing a row ticks it: nobody can confirm, by accident, a sentence they did
/// not read.
/// </summary>
internal sealed class FixDictationForm : Form
{
    private readonly List<(int Index, string Original, TextBox Box, CheckBox Save)> _rows = new();

    /// <summary>(phrase index, the text the user says they actually said).</summary>
    public List<(int Index, string Text)> ToSave { get; } = new();

    public FixDictationForm(IReadOnlyList<(int Index, string Text, double Seconds)> phrases)
    {
        Text = "What did you actually say?";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(620, Math.Min(140 + phrases.Count * 64, 620));
        Font = new Font("Segoe UI", 10F);
        // The main window is always-on-top; without this, this dialog opens behind it.
        TopMost = true;
        if (Hud.AppIcon != null) Icon = Hud.AppIcon;

        var help = new Label
        {
            Dock = DockStyle.Top,
            Height = 62,
            Padding = new Padding(12, 10, 12, 4),
            Text = "Fix anything that came out wrong, then Save.\r\n" +
                   "Only ticked lines are saved, and editing a line ticks it. " +
                   "What you save is used to train the model on your voice.",
        };

        var list = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };

        int y = 4;
        foreach (var (index, text, seconds) in phrases)
        {
            var save = new CheckBox
            {
                Left = 8,
                Top = y + 6,
                Width = 130,
                Text = $"save ({seconds:0.0}s)",
                Checked = false,
            };
            var box = new TextBox
            {
                Left = 144,
                Top = y,
                Width = 440,
                Text = text,
                // Hebrew: type and read right-to-left, like every other Hebrew field.
                RightToLeft = RightToLeft.Yes,
                Font = new Font("Segoe UI", 12F),
            };
            string original = text;
            box.TextChanged += (_, _) =>
            {
                // Changing a line means the user has looked at it: tick it for them,
                // and untick if they put it back exactly as it was.
                save.Checked = box.Text.Trim() != original.Trim() || save.Checked;
            };
            list.Controls.Add(save);
            list.Controls.Add(box);
            _rows.Add((index, original, box, save));
            y += 64;
        }

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12, 8, 12, 8) };
        var saveButton = new Button3D { Text = "Save for training", Dock = DockStyle.Right, Width = 190 };
        var cancelButton = new Button3D { Text = "Cancel", Dock = DockStyle.Left, Width = 120 };
        saveButton.Click += (_, _) => Accept();
        cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);

        Controls.Add(list);
        Controls.Add(buttons);
        Controls.Add(help);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    private void Accept()
    {
        foreach (var (index, _, box, save) in _rows)
        {
            string text = box.Text.Trim();
            if (save.Checked && text.Length > 0)
                ToSave.Add((index, text));
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>A one-line summary for the log.</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        int corrections = 0;
        foreach (var (index, original, box, save) in _rows)
        {
            if (!save.Checked) continue;
            if (box.Text.Trim() != original.Trim()) corrections++;
        }
        sb.Append($"{ToSave.Count} phrase(s) saved");
        if (corrections > 0) sb.Append($", {corrections} corrected");
        return sb.ToString();
    }
}
