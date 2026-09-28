using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceOS;

/// <summary>What a command does. Interpreted by <see cref="CommandExecutor"/>.</summary>
internal sealed class CommandAction
{
    /// <summary>keys | scroll | window | app | typing | none</summary>
    public string Type { get; set; } = "none";
    /// <summary>For "keys": chords separated by spaces, e.g. "ctrl+c" or "win+shift+right".</summary>
    public string? Keys { get; set; }
    /// <summary>For "scroll": wheel notches, positive is up.</summary>
    public int Amount { get; set; }
    /// <summary>For "window" (minimize/maximize/restore/close) and "typing" (on/off).</summary>
    public string? Value { get; set; }
    /// <summary>For "app": the process name to look for, without .exe.</summary>
    public string? Process { get; set; }
    /// <summary>For "app": what to start when it isn't running. Environment variables expand.</summary>
    public string? Launch { get; set; }
    /// <summary>For "keys": send to whatever has focus, without bringing the dictation window forward
    /// (media keys, show desktop).</summary>
    public bool Global { get; set; }
}

internal sealed class VoiceCommand
{
    public string Id { get; set; } = "";
    /// <summary>Spoken back after the command runs.</summary>
    public string? Say { get; set; }
    /// <summary>Heading the command is listed under in the command panel.</summary>
    public string? Group { get; set; }
    /// <summary>What Voiceitt writes when the user says the command, in English.</summary>
    public List<string> Phrases { get; set; } = new();
    /// <summary>Reserved for the local Hebrew engine; not matched yet.</summary>
    public List<string> Hebrew { get; set; } = new();
    public CommandAction Action { get; set; } = new();
}

internal sealed class CommandSet
{
    /// <summary>The explanation at the top of the file, kept when the file is saved back.</summary>
    [JsonPropertyName("_about")] public string? About { get; set; }
    public List<string> Prefixes { get; set; } = new();
    public List<VoiceCommand> Commands { get; set; } = new();

    /// <summary>Where the loaded list came from, for the log.</summary>
    [JsonIgnore] public string Source { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string UserFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceOS", "commands.json");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>Writes this list to the user's own copy in %APPDATA%, which from then on
    /// replaces the built-in list.</summary>
    public void SaveToUserFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserFilePath)!);
        File.WriteAllText(UserFilePath, ToJson(), Encoding.UTF8);
        Source = UserFilePath;
    }

    /// <summary>
    /// Points an "app" command at a program: "Computer, this is mail" while eM Client is the
    /// dictation window makes "Computer, open mail" find and start exactly that program.
    /// Returns false if there is no such app command.
    /// </summary>
    public bool AssignApp(string commandId, string exePath)
    {
        var cmd = Commands.FirstOrDefault(c => c.Id == commandId);
        if (cmd == null || !cmd.Action.Type.Equals("app", StringComparison.OrdinalIgnoreCase)) return false;
        // Split on both separators, so the result doesn't depend on the OS reading the path.
        string file = exePath[(exePath.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        cmd.Action.Process = Path.GetFileNameWithoutExtension(file);
        cmd.Action.Launch = exePath;
        return true;
    }

    public static CommandSet Parse(string json)
    {
        var set = JsonSerializer.Deserialize<CommandSet>(json, Options) ?? new CommandSet();
        set.Prefixes = set.Prefixes.Select(CommandMatcher.Normalize).Where(p => p.Length > 0).ToList();
        set.Commands = set.Commands.Where(c => c.Phrases.Count > 0).ToList();
        return set;
    }

    /// <summary>The user's own copy in %APPDATA% if there is one, else the list built into the exe.
    /// A broken user file is reported, not silently replaced: the error says what to fix.</summary>
    public static CommandSet Load(out string? error)
    {
        error = null;
        if (File.Exists(UserFilePath))
        {
            try
            {
                var set = Parse(File.ReadAllText(UserFilePath));
                set.Source = UserFilePath;
                return set;
            }
            catch (Exception ex)
            {
                error = $"Couldn't read {UserFilePath} ({ex.Message}) — using the built-in commands instead.";
            }
        }
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("commands.json");
        if (stream == null)
        {
            error = (error == null ? "" : error + " ") + "The built-in command list is missing.";
            return new CommandSet();
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var builtIn = Parse(reader.ReadToEnd());
        builtIn.Source = "built-in";
        return builtIn;
    }
}

internal sealed record CommandMatch(VoiceCommand Command, string Phrase, double Score);

/// <summary>
/// Matches what Voiceitt wrote against the closed list of commands. Pure logic, no Windows,
/// so it can be tested anywhere.
///
/// The rule that matters: a command runs only when it clearly beats every OTHER command.
/// A misheard command that runs the wrong action is worse than one that does nothing — the
/// user then has to notice, work out what happened, and undo it, all by voice.
/// </summary>
internal static class CommandMatcher
{
    /// <summary>Similarity a phrase must reach. 1.0 is identical text.</summary>
    public const double MinScore = 0.80;
    /// <summary>How far the best command must lead the best different command.</summary>
    public const double MinMargin = 0.10;

    /// <summary>Lower case, punctuation removed, single spaces. "Computer, scroll down." → "computer scroll down".</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == '\'')
            {
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                if (ch != '\'') sb.Append(ch);
            }
            else
            {
                space = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>True if the text starts with a prefix word. `rest` is what follows it, normalized
    /// (empty when the prefix was said alone).</summary>
    public static bool TrySplitPrefix(string text, IEnumerable<string> prefixes, out string rest)
    {
        string norm = Normalize(text);
        foreach (string p in prefixes)
        {
            if (norm == p) { rest = ""; return true; }
            if (norm.StartsWith(p + " ", StringComparison.Ordinal)) { rest = norm[(p.Length + 1)..]; return true; }
        }
        rest = "";
        return false;
    }

    /// <summary>
    /// Could this still become a prefix as more words arrive? Voiceitt writes word by word, so
    /// "comp" may be the start of "computer". Used only to decide whether to hold text back briefly.
    /// </summary>
    public static bool MayBecomePrefix(string text, IEnumerable<string> prefixes)
    {
        string norm = Normalize(text);
        if (norm.Length == 0) return false;
        return prefixes.Any(p => p.StartsWith(norm, StringComparison.Ordinal));
    }

    /// <summary>The command meant by `heard` (already stripped of the prefix), or null with a
    /// reason. Never guesses between two close commands.</summary>
    public static CommandMatch? Match(string heard, CommandSet set, out string reason)
    {
        string norm = Normalize(heard);
        reason = "";
        if (norm.Length == 0) { reason = "nothing after the prefix"; return null; }

        CommandMatch? best = null;
        double runnerUp = 0;
        foreach (var cmd in set.Commands)
        {
            double cmdBest = 0;
            string cmdPhrase = "";
            foreach (string phrase in cmd.Phrases)
            {
                double s = Similarity(norm, Normalize(phrase));
                if (s > cmdBest) { cmdBest = s; cmdPhrase = phrase; }
            }
            if (best == null || cmdBest > best.Score)
            {
                if (best != null) runnerUp = Math.Max(runnerUp, best.Score);
                best = new CommandMatch(cmd, cmdPhrase, cmdBest);
            }
            else
            {
                runnerUp = Math.Max(runnerUp, cmdBest);
            }
        }

        if (best == null) { reason = "no commands loaded"; return null; }
        if (best.Score < MinScore)
        {
            reason = $"closest was \"{best.Phrase}\" ({best.Score:P0}), not close enough";
            return null;
        }
        if (best.Score < 1.0 && best.Score - runnerUp < MinMargin)
        {
            reason = $"\"{best.Phrase}\" and another command are too alike ({best.Score:P0} vs {runnerUp:P0})";
            return null;
        }
        return best;
    }

    /// <summary>1 − edit distance / longer length, on characters.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        int longer = Math.Max(a.Length, b.Length);
        return 1.0 - (double)Levenshtein(a, b) / longer;
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
