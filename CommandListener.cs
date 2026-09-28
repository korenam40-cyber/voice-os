namespace VoiceOS;

internal enum CommandOutcome
{
    /// <summary>A command was recognized and should run.</summary>
    Run,
    /// <summary>The prefix was said alone; the next thing said is the command.</summary>
    Armed,
    /// <summary>Prefix heard, but what followed matched no command clearly. Nothing is typed.</summary>
    Rejected,
    /// <summary>Held back because it looked like the start of the prefix, but it wasn't —
    /// type it after all.</summary>
    Released,
    /// <summary>The prefix was said alone and nothing followed in time.</summary>
    Expired,
}

internal sealed record CommandResolution(CommandOutcome Outcome, string Heard, CommandMatch? Match = null,
                                         string Reason = "");

/// <summary>
/// Decides, as Voiceitt's text arrives word by word, which words are a command and which are
/// dictation to type. No Windows here, so the timing rules can be tested.
///
/// The prefix ("Computer") counts only at the START of an utterance — right after a pause, or
/// when Voiceitt's box was empty. The same word in the middle of a sentence is just a word.
/// Once the prefix is seen, text is held back (never typed) until the words stop changing
/// for <see cref="Settle"/>, and only then matched: matching after the first word would run
/// "scroll" before the user finished saying "scroll down".
/// </summary>
internal sealed class CommandListener
{
    public CommandSet Commands { get; set; }
    /// <summary>No new words for this long ends the command.</summary>
    public TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(1.2);
    /// <summary>After "Computer" said alone, how long to wait for the command.</summary>
    public TimeSpan ArmedFor { get; set; } = TimeSpan.FromSeconds(6);

    private string? _held;
    private bool _heldIsCommandOnly; // true when the prefix was already heard (armed)
    private DateTime _heldChanged;
    private DateTime? _armedUntil;

    public CommandListener(CommandSet commands) => Commands = commands;

    public bool IsHolding => _held != null;
    public bool IsArmed => _armedUntil != null;

    /// <summary>
    /// Offer new text that would otherwise be typed. Returns true if it was taken (held as a
    /// possible command) — the caller must then NOT type it.
    /// </summary>
    /// <param name="delta">Newly arrived text, exactly as it would be typed.</param>
    /// <param name="utteranceStart">True after a pause or when Voiceitt's box was empty.</param>
    public bool Offer(string delta, bool utteranceStart, DateTime now)
    {
        if (delta.Length == 0) return false;
        if (_held != null)
        {
            _held += delta;
            _heldChanged = now;
            return true;
        }
        if (_armedUntil != null && now <= _armedUntil)
        {
            _held = delta;
            _heldIsCommandOnly = true;
            _heldChanged = now;
            _armedUntil = null;
            return true;
        }
        if (!utteranceStart) return false;

        string firstWord = FirstWord(delta);
        if (CommandMatcher.TrySplitPrefix(delta, Commands.Prefixes, out _)
            || CommandMatcher.MayBecomePrefix(firstWord, Commands.Prefixes)
            || Commands.Prefixes.Contains(CommandMatcher.Normalize(firstWord)))
        {
            _held = delta;
            _heldIsCommandOnly = false;
            _heldChanged = now;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Replace the held text with what Voiceitt's box now says from where the hold began.
    /// Voiceitt revises words it already wrote ("Computer scroll" → "Computer, scroll down"), so the
    /// held text is re-read from the box, not built by appending pieces.
    /// </summary>
    public void UpdateHeld(string text, DateTime now)
    {
        if (_held == null || text == _held) return;
        _held = text;
        _heldChanged = now;
    }

    /// <summary>Call on every poll. Returns a decision once held text has settled, or when a
    /// lone prefix times out; null otherwise.</summary>
    public CommandResolution? Tick(DateTime now)
    {
        if (_held == null)
        {
            if (_armedUntil != null && now > _armedUntil)
            {
                _armedUntil = null;
                return new CommandResolution(CommandOutcome.Expired, "");
            }
            return null;
        }
        if (now - _heldChanged < Settle) return null;

        string text = _held;
        bool commandOnly = _heldIsCommandOnly;
        _held = null;
        _heldIsCommandOnly = false;

        string rest;
        if (commandOnly)
        {
            rest = CommandMatcher.Normalize(text);
        }
        else if (!CommandMatcher.TrySplitPrefix(text, Commands.Prefixes, out rest))
        {
            return new CommandResolution(CommandOutcome.Released, text);
        }

        if (rest.Length == 0)
        {
            _armedUntil = now + ArmedFor;
            return new CommandResolution(CommandOutcome.Armed, text.Trim());
        }

        var match = CommandMatcher.Match(rest, Commands, out string reason);
        return match != null
            ? new CommandResolution(CommandOutcome.Run, rest, match)
            : new CommandResolution(CommandOutcome.Rejected, rest, null, reason);
    }

    /// <summary>Forget anything held or armed (pause, box cleared).</summary>
    public void Reset()
    {
        _held = null;
        _heldIsCommandOnly = false;
        _armedUntil = null;
    }

    private static string FirstWord(string text)
    {
        string t = text.TrimStart();
        int end = 0;
        while (end < t.Length && !char.IsWhiteSpace(t[end])) end++;
        return t[..end];
    }
}
