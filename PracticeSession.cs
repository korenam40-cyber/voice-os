namespace VoiceOS;

internal enum PracticeOutcome
{
    /// <summary>Understood as the command being practised.</summary>
    Right,
    /// <summary>Understood, but as a different command.</summary>
    OtherCommand,
    /// <summary>Not understood as any command.</summary>
    NotUnderstood,
    Skipped,
    Finished,
}

internal sealed record PracticeAttempt(VoiceCommand? Target, string Heard, PracticeOutcome Outcome,
                                       VoiceCommand? MatchedInstead = null, string Reason = "");

/// <summary>
/// Practising the commands (the user asked for it on 2026-09-29). One command at a time; the
/// user says it, and is told whether it would have worked -- but NOTHING is carried out, so
/// even "close window" is safe to practise. What Voiceitt wrote is shown on a miss, because
/// that is what decides whether a phrasing needs changing. Pure, so it is tested.
/// </summary>
internal sealed class PracticeSession
{
    private readonly CommandSet _set;
    private readonly List<VoiceCommand> _items;

    public int Index { get; private set; }
    public int Right { get; private set; }
    public int Attempts { get; private set; }
    /// <summary>Commands that went wrong at least once, with what was heard.</summary>
    public List<PracticeAttempt> Misses { get; } = new();

    public PracticeSession(CommandSet set)
    {
        _set = set;
        _items = set.Commands.Where(c => c.Action.Type != "practice").ToList();
    }

    public int Count => _items.Count;
    public bool Done => Index >= _items.Count;
    public VoiceCommand? Current => Done ? null : _items[Index];

    private static readonly string[] SkipWords = { "skip", "next", "skip it" };
    private static readonly string[] StopWords = { "stop practice", "end practice", "finish practice" };

    /// <summary>One try at the current command. The wake word may be said or left out.</summary>
    public PracticeAttempt Attempt(string heard)
    {
        string norm = CommandMatcher.Normalize(heard);
        if (CommandMatcher.TrySplitPrefix(heard, _set.Prefixes, out string rest)) norm = rest;
        var target = Current;

        if (StopWords.Any(w => CommandMatcher.Similarity(norm, w) >= CommandMatcher.MinScore))
        {
            Index = _items.Count;
            return new PracticeAttempt(target, heard, PracticeOutcome.Finished);
        }
        if (target == null) return new PracticeAttempt(null, heard, PracticeOutcome.Finished);
        if (SkipWords.Any(w => CommandMatcher.Similarity(norm, w) >= CommandMatcher.MinScore))
        {
            Index++;
            return new PracticeAttempt(target, heard, PracticeOutcome.Skipped);
        }

        Attempts++;
        var match = CommandMatcher.Match(norm, _set, out string reason);
        PracticeAttempt result;
        if (match?.Command.Id == target.Id)
        {
            Right++;
            Index++;
            result = new PracticeAttempt(target, heard, PracticeOutcome.Right);
        }
        else if (match != null)
        {
            result = new PracticeAttempt(target, heard, PracticeOutcome.OtherCommand, match.Command);
        }
        else
        {
            result = new PracticeAttempt(target, heard, PracticeOutcome.NotUnderstood, null, reason);
        }
        if (result.Outcome != PracticeOutcome.Right) Misses.Add(result);
        return result;
    }
}
