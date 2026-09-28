// Checks for the voice-command logic: matching, the prefix rule, and the word-by-word timing.
// No Windows, no Voiceitt. Run: dotnet run --project tests/CommandTests
using VoiceOS;

int passed = 0, failed = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(!ok && detail.Length > 0 ? " — " + detail : "")}");
    if (ok) passed++; else failed++;
}

var set = CommandSet.Load(out string? loadError);
Check("the built-in command list loads", loadError == null && set.Commands.Count > 30, loadError ?? "");
// "Computer" was written as "week" 4 of 5 times and "back" once; "computer" 5 of 5 (user test, 2026-09-27).
Check("the prefix is computer", set.Prefixes.SequenceEqual(new[] { "computer" }));

// Every phrase must match its own command -- otherwise two commands are too alike to use.
var clashes = new List<string>();
foreach (var cmd in set.Commands)
    foreach (var phrase in cmd.Phrases)
    {
        var m = CommandMatcher.Match(phrase, set, out string why);
        if (m?.Command.Id != cmd.Id) clashes.Add($"\"{phrase}\" -> {m?.Command.Id ?? why}");
    }
Check("every phrase in the list selects its own command", clashes.Count == 0, string.Join("; ", clashes));

// Every key chord in the list must parse.
var badKeys = set.Commands.Where(c => c.Action.Type == "keys")
    .SelectMany(c => (c.Action.Keys ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(k => !KeyChord.TryParse(k, out _, out _)).Select(k => $"{c.Id}: {k}")).ToList();
Check("every key chord in the list is understood", badKeys.Count == 0, string.Join("; ", badKeys));

// Normalizing what Voiceitt writes.
Check("punctuation and case are ignored", CommandMatcher.Normalize("Computer, Scroll down.") == "computer scroll down");
Check("prefix split", CommandMatcher.TrySplitPrefix("Computer, scroll down.", set.Prefixes, out var rest) && rest == "scroll down");
Check("prefix alone", CommandMatcher.TrySplitPrefix("Computer.", set.Prefixes, out rest) && rest == "");
Check("a word that only starts like the prefix is not it", !CommandMatcher.TrySplitPrefix("Computers are cool", set.Prefixes, out _));

// Matching: small mistakes pass, different commands never get confused.
Check("one letter wrong still matches", CommandMatcher.Match("scroll dawn", set, out _)?.Command.Id == "scroll-down");
Check("scroll up is not scroll down", CommandMatcher.Match("scroll up", set, out _)?.Command.Id == "scroll-up");
Check("page up is not page down", CommandMatcher.Match("page up", set, out _)?.Command.Id == "page-up");
Check("ordinary words are not a command", CommandMatcher.Match("is a nice candle", set, out _) == null);
Check("nothing after the prefix is not a command", CommandMatcher.Match("", set, out _) == null);

// Key chords.
Check("ctrl+shift+tab parses to three keys", KeyChord.TryParse("ctrl+shift+tab", out var keys, out _) && keys.Count == 3);
Check("f11 parses", KeyChord.TryParse("f11", out keys, out _) && keys[0].Code == 0x7A);
Check("an unknown key is refused", !KeyChord.TryParse("ctrl+banana", out _, out _));

// The word-by-word listener.
var t0 = new DateTime(2026, 9, 27, 12, 0, 0);
DateTime At(double s) => t0.AddSeconds(s);

var L = new CommandListener(set);
Check("ordinary dictation passes straight through", !L.Offer("Hello there", true, At(0)));
Check("the prefix mid-sentence is typed, not held", !L.Offer(" computer", false, At(0.3)));

L = new CommandListener(set);
Check("the prefix at the start of an utterance is held", L.Offer("Computer", true, At(0)));
Check("the following words are held too", L.Offer(" scroll", false, At(0.4)) && L.Offer(" down", false, At(0.8)));
Check("nothing is decided while words are still coming", L.Tick(At(1.5)) == null);
var r = L.Tick(At(2.1));
Check("once the words settle, the command runs", r?.Outcome == CommandOutcome.Run && r.Match?.Command.Id == "scroll-down",
      $"{r?.Outcome} {r?.Reason}");

L = new CommandListener(set);
L.Offer("Computer.", true, At(0));
r = L.Tick(At(1.3));
Check("the prefix alone arms the listener", r?.Outcome == CommandOutcome.Armed && L.IsArmed);
Check("the next words are the command, even without the prefix", L.Offer(" Copy.", true, At(3)));
r = L.Tick(At(4.3));
Check("and it runs", r?.Outcome == CommandOutcome.Run && r.Match?.Command.Id == "copy", $"{r?.Outcome} {r?.Reason}");

L = new CommandListener(set);
L.Offer("Computer", true, At(0));
L.Tick(At(1.3));
r = L.Tick(At(8));
Check("an armed listener gives up after a while", r?.Outcome == CommandOutcome.Expired && !L.IsArmed);
Check("and dictation types normally again", !L.Offer("Hello", true, At(9)));

L = new CommandListener(set);
L.Offer("Computer is slow today", true, At(0));
r = L.Tick(At(1.3));
Check("prefix + no command is rejected, and nothing is typed", r?.Outcome == CommandOutcome.Rejected);

L = new CommandListener(set);
Check("a word that may become the prefix is held", L.Offer("Comp", true, At(0)));
L.Offer("any is here", false, At(0.2));
r = L.Tick(At(1.5));
Check("and released for typing when it wasn't the prefix", r?.Outcome == CommandOutcome.Released && r.Heard == "Company is here",
      $"{r?.Outcome} \"{r?.Heard}\"");

L = new CommandListener(set);
L.Offer("Computer scroll", true, At(0));
L.UpdateHeld("Computer, scroll down.", At(0.5));
r = L.Tick(At(1.8));
Check("a word Voiceitt rewrites is read again, not glued on", r?.Outcome == CommandOutcome.Run && r.Match?.Command.Id == "scroll-down",
      $"{r?.Outcome} \"{r?.Heard}\"");

// "Computer, this is my mail": connecting a command to a program, and saving it.
var copy = CommandSet.Parse(set.ToJson());
Check("the list survives being saved and read back", copy.Commands.Count == set.Commands.Count && copy.About == set.About);
Check("a program command can be connected to a program",
      copy.AssignApp("open-mail", @"D:\Apps\eM Client\MailClient.exe"));
var mail = copy.Commands.First(c => c.Id == "open-mail").Action;
Check("and it remembers the program and where it lives",
      mail.Process == "MailClient" && mail.Launch == @"D:\Apps\eM Client\MailClient.exe", $"{mail.Process} {mail.Launch}");
Check("a command that isn't a program can't be connected", !copy.AssignApp("copy", @"C:\x.exe"));
Check("the sounds are real WAV data", ToneWav.Make((880, 0.1)).Take(4).SequenceEqual("RIFF"u8.ToArray()));

Console.WriteLine($"\n{passed}/{passed + failed} passed");
return failed == 0 ? 0 : 1;
