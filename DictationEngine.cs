using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace VoiceOS;

internal sealed class DictationEngine : IDisposable
{
    public event Action<string>? Log;
    public event Action? StatusChanged;

    public bool IsCalibrated => _target != null || ConnectorLive;
    public bool IsPaused { get; private set; } = true;
    public DateTime? LastSuccessfulPoll { get; private set; }

    /// <summary>Null when everything is working. When something needs the user's
    /// attention, this holds a short, plain-English instruction for exactly what to
    /// do about it — the UI surfaces this in place of the normal status text.</summary>
    public string? ActionNeeded { get; private set; }

    private void SetAction(string? message)
    {
        if (ActionNeeded == message) return;
        ActionNeeded = message;
        StatusChanged?.Invoke();
    }

    private int _consecutiveRelocateFailures;
    private int _consecutiveTypeFailures;
    private const int RelocateFailureThreshold = 2; // ~0.5s of real failures, not a single blip
    private const int TypeFailureThreshold = 3;
    public bool AllowBackspaceCorrections { get; set; } = false;
    public bool ClearOnTriggerWord { get; set; } = true;
    public bool ConvertSpokenPunctuation { get; set; } = false;

    // Saying "new paragraph" clears Voiceitt's box on command rather than clearing
    // automatically after every type. Voiceitt turns that phrase into a real blank
    // line before we see it, so this matches the resulting double line break, not
    // any literal words. The paragraph break itself still gets typed normally.
    private static readonly Regex NewParagraphPattern = new(@"\n[ \t]*\n", RegexOptions.Compiled);

    // Voiceitt sometimes stops converting spoken punctuation commands into real
    // symbols and just writes the word instead (e.g. "period" instead of "."). This
    // is a best-effort client-side workaround for that, not a general grammar model —
    // it's a blunt word swap, so words like "period" that are also ordinary English
    // ("for a period of time") will get mangled too. Opt-in for that reason.
    private static readonly (Regex Pattern, string Replacement)[] PunctuationWordRules =
    {
        (new Regex(@"\s*\bexclamation\s+(?:mark|point)\b\s?", RegexOptions.IgnoreCase | RegexOptions.Compiled), "! "),
        (new Regex(@"\s*\bquestion\s+mark\b\s?", RegexOptions.IgnoreCase | RegexOptions.Compiled), "? "),
        (new Regex(@"\s*\bsemicolon\b\s?", RegexOptions.IgnoreCase | RegexOptions.Compiled), "; "),
        (new Regex(@"\s*\bcolon\b\s?", RegexOptions.IgnoreCase | RegexOptions.Compiled), ": "),
        (new Regex(@"\s*\bcomma\b\s?", RegexOptions.IgnoreCase | RegexOptions.Compiled), ", "),
        (new Regex(@"\s*\bperiod\b\s?", RegexOptions.IgnoreCase | RegexOptions.Compiled), ". "),
        (new Regex(@"\s*\b(?:hyphen|dash)\b\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "-"),
    };

    private static string NormalizeSpokenPunctuation(string text)
    {
        foreach (var (pattern, replacement) in PunctuationWordRules)
        {
            text = pattern.Replace(text, replacement);
        }
        return text;
    }

    public string TargetWindowTitle { get; private set; } = "(none selected — pick a window from the dropdown)";
    public string LastSeenText { get; private set; } = "";
    public string VoiceittWindowTitle => ConnectorLive ? "Chrome connector" : _target?.WindowTitle ?? "(not calibrated)";

    // ---- Chrome connector: reads Voiceitt's text straight from the page ----------------
    // Preferred over reading the accessibility tree, which shifts whenever Voiceitt
    // restyles itself. The tree-reading path below stays as the fallback.
    private ExtensionServer? _ext;
    private bool _extActivated;
    private bool _extClearPending;
    private bool _userToggledPause;
    private string? _lastSource;

    public bool ConnectorLive => _ext?.IsLive == true;
    public string ConnectorFolder => ConnectorInstaller.Folder;

    /// <summary>Writes the extension files and starts listening for it.</summary>
    public void StartConnector()
    {
        try
        {
            _settings.ConnectorToken ??= ConnectorInstaller.NewToken();
            _settings.Save();
            bool changed = ConnectorInstaller.Install(_settings.ConnectorToken);

            var server = new ExtensionServer(_settings.ConnectorToken);
            if (!server.Start(out string error))
            {
                Log?.Invoke($"Chrome connector is unavailable ({error}). Using the slower fallback.");
                return;
            }
            _ext = server;
            if (changed)
                Log?.Invoke("Chrome connector files were written/updated. If it's already installed, open chrome://extensions and press its reload button.");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Chrome connector couldn't start: {ex.Message}");
        }
    }

    /// <summary>First time the connector reports in: start watching without any calibration.</summary>
    private void ActivateConnectorSource(string currentText)
    {
        _extActivated = true;
        // With no tree-based calibration there's no baseline yet — whatever is already in
        // the box was said before we connected and must not be typed now.
        if (_target == null)
        {
            _lastText = currentText;
            LastSeenText = currentText;
        }
        if (IsPaused && !_userToggledPause) IsPaused = false;
        Log?.Invoke("Chrome connector connected — reading Voiceitt directly, no calibration needed.");
        StatusChanged?.Invoke();
    }

    private void NoteSource(string source)
    {
        if (_lastSource == source) return;
        if (_lastSource != null)
            Log?.Invoke(source == "connector"
                ? "Back to reading Voiceitt through the Chrome connector."
                : "Chrome connector went quiet — reading Voiceitt the slower way for now.");
        _lastSource = source;
    }
    public IntPtr TargetWindowHandle => _targetHwnd;

    private CalibratedTarget? _target;
    private string _lastText = "";
    private IntPtr _targetHwnd = IntPtr.Zero;
    private readonly AppSettings _settings = AppSettings.Load();

    private readonly System.Windows.Forms.Timer _pollTimer;

    private const int PollIntervalMs = 250;
    private const int MaxSafeBackspaces = 300;
    private const int MaxPlausibleResetChars = 150;

    // ---- Voice commands ("Computer, scroll down") ------------------------------------------
    // Voiceitt's text is offered to the listener before it is typed. Words that belong to a
    // command are held back and never typed; everything else is typed exactly as before.

    private CommandListener? _commands;
    private CommandExecutor? _executor;
    private readonly Feedback _feedback = new();
    private MicGuard? _mic;
    private DateTime _lastBoxChange = DateTime.MinValue;
    /// <summary>Where, in Voiceitt's box, the text being held as a possible command begins.</summary>
    private int _holdStart;

    /// <summary>A pause at least this long before a word makes it the start of an utterance,
    /// where the command prefix counts.</summary>
    private static readonly TimeSpan UtterancePause = TimeSpan.FromSeconds(1.0);

    public bool VoiceCommandsEnabled { get; set; } = true;

    /// <summary>The loaded command list, for the command panel.</summary>
    public CommandSet? Commands => _commands?.Commands;

    /// <summary>Raised when the command list is (re)loaded, so the panel can refresh.</summary>
    public event Action? CommandsChanged;

    /// <summary>"Computer, show commands" (true) / "hide commands" (false). The UI owns the panel.</summary>
    public event Action<bool>? CommandPanelRequested;

    /// <summary>"Computer, stop typing" turns this off: Voiceitt is still read, so commands keep
    /// working, but nothing else is typed until "Computer, start typing".</summary>
    public bool TypingEnabled { get; private set; } = true;

    /// <summary>Loads the command list and says so in the log. Call once, after Log is wired up.</summary>
    public void LoadVoiceCommands()
    {
        if (_mic == null)
        {
            _mic = new MicGuard(_settings, m => Log?.Invoke(m));
            _mic.RecoverFromCrash();
            _feedback.SpeakingStarted = _mic.Hold;
            _feedback.SpeakingEnded = () => _mic.Release();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _mic.Release();
        }
        _feedback.Mode = Enum.TryParse<FeedbackMode>(_settings.CommandFeedback, true, out var mode)
            ? mode : FeedbackMode.Sound;
        var set = CommandSet.Load(out string? error);
        if (error != null) Log?.Invoke(error);
        _commands = new CommandListener(set);
        _executor ??= new CommandExecutor(() => _targetHwnd, h => ActivateWindow(h), SetTypingTarget,
                                          m => Log?.Invoke(m));
        string prefix = set.Prefixes.Count > 0 ? set.Prefixes[0] : "(none)";
        Log?.Invoke($"Voice commands: {set.Commands.Count} loaded ({set.Source}). Say \"{prefix}\", then a command. "
                    + $"Confirmation: {_feedback.Mode.ToString().ToLowerInvariant()}.");
        CommandsChanged?.Invoke();
    }

    /// <summary>Settles held command text and runs what it names. Called on every poll.</summary>
    private void ResolveCommands()
    {
        _executor?.Tick();
        if (_commands == null) return;
        var r = _commands.Tick(DateTime.Now);
        if (r == null) return;

        switch (r.Outcome)
        {
            case CommandOutcome.Armed:
                Log?.Invoke($"COMMAND: heard \"{r.Heard}\" — listening for a command.");
                _feedback.Give(FeedbackKind.Listening, "yes?");
                break;
            case CommandOutcome.Expired:
                Log?.Invoke("COMMAND: no command followed — back to dictation.");
                break;
            case CommandOutcome.Released:
                // Looked like the start of the prefix but wasn't: it is dictation after all.
                if (TypingEnabled) SendToTarget(0, r.Heard);
                break;
            case CommandOutcome.Rejected:
                Log?.Invoke($"COMMAND NOT UNDERSTOOD: \"{r.Heard}\" — {r.Reason}. Nothing was done.");
                _feedback.Give(FeedbackKind.NotUnderstood, "didn't catch that");
                break;
            case CommandOutcome.Run:
                RunCommand(r);
                break;
        }
    }

    private void RunCommand(CommandResolution r)
    {
        var cmd = r.Match!.Command;
        string heard = r.Heard == CommandMatcher.Normalize(r.Match.Phrase) ? "" : $" (heard \"{r.Heard}\")";
        string? error;
        switch (cmd.Action.Type.ToLowerInvariant())
        {
            case "typing":
                TypingEnabled = !string.Equals(cmd.Action.Value, "off", StringComparison.OrdinalIgnoreCase);
                StatusChanged?.Invoke();
                heard += $" — typing is now {(TypingEnabled ? "ON" : "OFF, still listening for commands")}";
                error = null;
                break;
            case "feedback":
                error = SetFeedback(cmd.Action.Value);
                break;
            case "assign":
                error = AssignProgram(cmd.Action.Value, ref heard);
                break;
            case "edit-commands":
                error = OpenCommandsFile();
                break;
            case "panel":
                if (CommandPanelRequested == null) { error = "the command panel isn't available"; break; }
                CommandPanelRequested(!string.Equals(cmd.Action.Value, "hide", StringComparison.OrdinalIgnoreCase));
                error = null;
                break;
            default:
                _executor!.Execute(cmd.Action, out string e);
                error = e.Length > 0 ? e : null;
                break;
        }

        if (error == null)
        {
            Log?.Invoke($"COMMAND: {r.Match.Phrase}{heard}");
            _feedback.Give(FeedbackKind.Done, cmd.Say);
        }
        else
        {
            Log?.Invoke($"COMMAND FAILED: {r.Match.Phrase} — {error}");
            _feedback.Give(FeedbackKind.Failed, "couldn't do that");
        }
    }

    private string? SetFeedback(string? value)
    {
        if (!Enum.TryParse<FeedbackMode>(value, true, out var mode)) return $"unknown feedback \"{value}\"";
        _feedback.Mode = mode;
        _settings.CommandFeedback = mode.ToString().ToLowerInvariant();
        _settings.Save();
        return null;
    }

    /// <summary>"Computer, this is my mail": the dictation window's program becomes what
    /// "Computer, open mail" finds and starts. Saved to the user's command file.</summary>
    private string? AssignProgram(string? commandId, ref string detail)
    {
        if (_commands == null || commandId == null) return "no command list loaded";
        if (_targetHwnd == IntPtr.Zero || !Interop.IsWindow(_targetHwnd))
            return "open the program and pick it as the dictation window first";
        string exe = ElementLocator.GetProcessExePath(_targetHwnd);
        if (exe.Length == 0) return "couldn't tell which program that window belongs to";
        if (!_commands.Commands.AssignApp(commandId, exe)) return $"there is no program command \"{commandId}\"";
        try
        {
            _commands.Commands.SaveToUserFile();
        }
        catch (Exception ex)
        {
            return $"couldn't save the command list: {ex.Message}";
        }
        detail += $" — now {exe}";
        return null;
    }

    /// <summary>Opens the user's own command list in Notepad, creating it from the built-in list
    /// the first time, and reloads it when Notepad closes.</summary>
    private string? OpenCommandsFile()
    {
        try
        {
            if (!System.IO.File.Exists(CommandSet.UserFilePath))
                (_commands?.Commands ?? CommandSet.Load(out _)).SaveToUserFile();
            var notepad = System.Diagnostics.Process.Start("notepad.exe", $"\"{CommandSet.UserFilePath}\"");
            if (notepad != null)
            {
                notepad.EnableRaisingEvents = true;
                var ui = System.Threading.SynchronizationContext.Current;
                notepad.Exited += (_, _) =>
                {
                    if (ui != null) ui.Post(_ => LoadVoiceCommands(), null);
                    else LoadVoiceCommands();
                };
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"couldn't open the command list: {ex.Message}";
        }
    }

    private static bool EndsUtterance(string text)
    {
        string t = text.TrimEnd(' ', '\t');
        return t.Length == 0 || ".?!\n".Contains(t[^1]);
    }

    public DictationEngine()
    {
        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();
    }

    public void SetCalibration(CalibratedTarget target)
    {
        _target = target;
        // Baseline starts at whatever text is already showing (e.g. Voiceitt's own
        // "tap the blue button..." placeholder), not blank — otherwise the very next
        // poll sees that existing text as a brand-new "delta" and types it out.
        _lastText = target.TextPreview;
        LastSeenText = target.TextPreview;
        IsPaused = false;
        _consecutiveRelocateFailures = 0;
        ActionNeeded = null;
        Log?.Invoke($"Calibrated on window: \"{target.WindowTitle}\". Watching for new speech...");
        StatusChanged?.Invoke();

        _settings.CalibrationWindowTitle = target.WindowTitle;
        _settings.CalibrationProcessExe = ElementLocator.GetProcessExeName(target.WindowHandle);
        _settings.CalibrationPath = target.Path;
        _settings.Save();
    }

    /// <summary>Best-effort: reuse the last session's calibration if the same app window
    /// (by process + similar title) is still around and its page structure still matches.
    /// Saves the user from recalibrating every single time they open the tool.</summary>
    public bool TryAutoRestoreCalibration(IntPtr ownHwnd)
    {
        if (_settings.CalibrationPath == null || string.IsNullOrEmpty(_settings.CalibrationWindowTitle)) return false;

        var candidates = WindowPicker.EnumerateCandidateWindows(ownHwnd);
        var match = candidates.FirstOrDefault(c =>
            string.Equals(c.ProcessName, _settings.CalibrationProcessExe, StringComparison.OrdinalIgnoreCase) &&
            TitleUtil.IsSimilar(c.Title, _settings.CalibrationWindowTitle!));
        if (match == null) return false;

        var probe = new CalibratedTarget
        {
            WindowHandle = match.Handle,
            WindowTitle = match.Title,
            Path = _settings.CalibrationPath!,
        };

        AutomationElement? element;
        try { element = ElementLocator.Relocate(probe); } catch { element = null; }
        if (element == null) return false;

        string preview;
        try { preview = ElementLocator.GetText(element); } catch { preview = ""; }

        _target = new CalibratedTarget
        {
            WindowHandle = match.Handle,
            WindowTitle = match.Title,
            Path = _settings.CalibrationPath!,
            TextPreview = preview,
            LiveElement = element,
        };
        _lastText = preview;
        LastSeenText = preview;
        IsPaused = false;
        Log?.Invoke($"Automatically restored last session's calibration on \"{match.Title}\".");
        StatusChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Finds Voiceitt's transcript box on its own — no hovering, no F8. Looks through
    /// open browser windows titled "Voiceitt" for its text field and calibrates on it.
    /// </summary>
    public bool TryAutoCalibrate(IntPtr ownHwnd)
    {
        foreach (var w in WindowPicker.EnumerateCandidateWindows(ownHwnd))
        {
            if (!TitleUtil.Normalize(w.Title).Contains("Voiceitt", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ElementLocator.IsBrowserWindow(w.Handle, out _)) continue;

            var found = ElementLocator.AutoCalibrate(w.Handle);
            if (found == null) continue;

            SetCalibration(found);
            Log?.Invoke($"Found Voiceitt's text box automatically on \"{w.Title}\" — no calibration needed.");
            return true;
        }
        return false;
    }

    /// <summary>Re-finds the text box after the saved path stopped working, keeping the
    /// current baseline so any speech still waiting to be typed isn't lost or repeated.</summary>
    private bool TrySelfHeal()
    {
        if (_target == null) return false;
        var healed = ElementLocator.AutoCalibrate(_target.WindowHandle);
        if (healed == null) return false;

        _target = healed;
        _settings.CalibrationWindowTitle = healed.WindowTitle;
        _settings.CalibrationProcessExe = ElementLocator.GetProcessExeName(healed.WindowHandle);
        _settings.CalibrationPath = healed.Path;
        _settings.Save();
        Log?.Invoke("Lost the text box, found it again automatically.");
        return true;
    }

    /// <summary>Finds a currently-open window matching the last session's chosen typing
    /// target, so it doesn't need to be picked from the dropdown again every launch.</summary>
    public WindowChoice? TryFindRestoredTarget(IntPtr ownHwnd)
    {
        if (string.IsNullOrEmpty(_settings.TargetWindowTitle)) return null;
        var candidates = WindowPicker.EnumerateCandidateWindows(ownHwnd);
        return candidates.FirstOrDefault(c =>
            string.Equals(c.ProcessName, _settings.TargetProcessExe, StringComparison.OrdinalIgnoreCase) &&
            TitleUtil.IsSimilar(c.Title, _settings.TargetWindowTitle!));
    }

    /// <summary>Explicitly picks the window speech gets typed into, chosen by the user from the dropdown.</summary>
    public void SetTypingTarget(IntPtr hwnd, string title)
    {
        _targetHwnd = hwnd;
        TargetWindowTitle = title;
        _consecutiveTypeFailures = 0;
        if (ActionNeeded != null && !ActionNeeded.StartsWith("LOST VOICEITT")) ActionNeeded = null;
        Log?.Invoke($"Will type into: \"{title}\".");
        StatusChanged?.Invoke();

        _settings.TargetWindowTitle = title;
        _settings.TargetProcessExe = ElementLocator.GetProcessExeName(hwnd);
        _settings.Save();
    }

    private bool _typedDictationBefore;

    /// <summary>
    /// Types a finished Hebrew dictation (from the local engine, not Voiceitt) into
    /// the chosen target window, through the same verified activation and keystroke
    /// path as Voiceitt text. Consecutive dictations are separated by a space.
    /// </summary>
    public bool TypeDictation(string text)
    {
        // Trim spaces, NOT newlines: saying שורה חדשה makes the engine return a
        // leading line break, and Trim() was silently eating it -- so the words
        // vanished with no new line to show for them (user report, 2026-09-25).
        string clean = text.Trim(' ', '\t', '\r');
        if (clean.Length == 0) return false;

        // Phrases are separated by a space, but not when the phrase opens with
        // punctuation or a line break: עובד followed by " ." reads as a typo, and
        // a space before a line break is just trailing whitespace.
        bool separate = _typedDictationBefore
                        && !_dictationEndedWithBreak
                        && clean[0] != '\n'
                        && !".,?!:;".Contains(clean[0]);
        bool ok = SendToTarget(0, (separate ? " " : "") + clean);
        if (ok)
        {
            _typedDictationBefore = true;
            // A phrase that ended a line must not be followed by a space, or the
            // next line starts indented by one.
            _dictationEndedWithBreak = clean[clean.Length - 1] == '\n';
        }
        return ok;
    }

    private bool _dictationEndedWithBreak;

    public void TogglePause()
    {
        _userToggledPause = true;
        IsPaused = !IsPaused;
        _commands?.Reset();
        Log?.Invoke(IsPaused ? "Paused." : "Resumed.");
        StatusChanged?.Invoke();
    }

    private bool _pollBusy;

    private readonly Dictionary<string, DateTime> _lastLogged = new();

    /// <summary>Logs a message at most once every 10 seconds per key, so a condition that
    /// persists across the 4-per-second polling can't bury the log.</summary>
    private void LogOnce(string key, string message)
    {
        if (_lastLogged.TryGetValue(key, out var t) && (DateTime.Now - t).TotalSeconds < 10) return;
        _lastLogged[key] = DateTime.Now;
        Log?.Invoke(message);
    }

    private void Poll()
    {
        if (_pollBusy) return;

        // Preferred source: the Chrome connector hands us the text box's contents
        // straight from the page — nothing to locate, nothing that restyling can break.
        if (_ext != null && _ext.TryGetText(out string connectorText))
        {
            NoteSource("connector");
            if (!_extActivated) ActivateConnectorSource(connectorText);

            // Hearing from the connector means Voiceitt is plainly there. Any "lost
            // Voiceitt" alert raised earlier by the tree-based path (e.g. in the moments
            // before the connector connected) is out of date — and this branch never
            // reaches the code that would clear it, so it has to be cleared here.
            _consecutiveRelocateFailures = 0;
            if (ActionNeeded != null && ActionNeeded.StartsWith("LOST VOICEITT")) SetAction(null);
            if (IsPaused) return;

            _pollBusy = true;
            try
            {
                if (_extClearPending) HandleConnectorClearOutcome();
                ProcessText(connectorText, fromConnector: true);
            }
            catch (Exception ex)
            {
                LogOnce("poll-ex", $"Internal error while reading Voiceitt: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _pollBusy = false;
            }
            return;
        }

        if (IsPaused || _target == null) return;
        if (_extActivated) NoteSource("fallback");
        _pollBusy = true;
        try
        {
            // Always re-find the element fresh rather than reusing a cached reference
            // between polls. A cached AutomationElement wrapper has twice now been
            // observed to keep answering property queries successfully while no
            // longer reflecting live text updates — a fresh lookup for the same node
            // reads correctly every time. Voiceitt's page structure is shallow enough
            // that walking it fresh every 250ms has no noticeable cost, so there's no
            // reason to keep the caching shortcut that kept causing this.
            AutomationElement? element = null;
            try
            {
                element = ElementLocator.Relocate(_target);
            }
            catch (Exception ex)
            {
                LogOnce("relocate-ex", $"Error while looking for Voiceitt's text box: {ex.Message}");
            }

            if (element == null)
            {
                // Relocate already tried the saved path AND a direct search for the text
                // field, so this is a real loss, not a blip. Try to find it from scratch
                // before bothering the user.
                if (++_consecutiveRelocateFailures == 1)
                    LogOnce("relocate-fail", "Can't see Voiceitt's text box — searching for it again...");
                if (_consecutiveRelocateFailures >= RelocateFailureThreshold)
                {
                    if (TrySelfHeal()) { _consecutiveRelocateFailures = 0; return; }
                    SetAction("LOST VOICEITT — is it open? Otherwise hover its text and press F8");
                }
                return;
            }
            _consecutiveRelocateFailures = 0;
            if (ActionNeeded != null && ActionNeeded.StartsWith("LOST VOICEITT")) SetAction(null);

            _target.LiveElement = element;

            string current = ElementLocator.GetText(element);

            // An element that isn't a text field can never carry the transcript — its
            // text just reads as "" forever while Voiceitt has real speech, and the tool
            // would sit there looking healthy. That's what a calibration on the
            // placeholder or a neighbouring container produces. Re-find the real box.
            if (current.Length == 0 && !ElementLocator.IsTextBox(element))
            {
                if (TrySelfHeal()) return;
            }

            ProcessText(current, fromConnector: false);
        }
        catch (Exception ex)
        {
            // Never let a failure here vanish silently — a stalled pipeline that looks
            // healthy is worse than a loud one.
            LogOnce("poll-ex", $"Internal error while reading Voiceitt: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _pollBusy = false;
        }
    }

    /// <summary>Everything that happens once we have Voiceitt's current text, whichever way we got it.</summary>
    private void ProcessText(string raw, bool fromConnector)
    {
        string current = ConvertSpokenPunctuation ? NormalizeSpokenPunctuation(raw) : raw;

        LastSuccessfulPoll = DateTime.Now;
        StatusChanged?.Invoke();

        ApplyDelta(current);
        ResolveCommands();

        // Only once everything said so far has actually been typed is it safe for the
        // connector to wipe the box. Handing it the exact text it should still contain
        // means words spoken in the meantime are never cleared before they are typed.
        if (fromConnector && _extClearPending)
            _ext?.SetClearExpect(_lastText == current ? raw : null);
    }

    private void HandleConnectorClearOutcome()
    {
        switch (_ext!.TakeOutcome())
        {
            case ExtensionServer.ClearOutcome.Done:
                _extClearPending = false;
                _lastText = "";
                LastSeenText = "";
                Log?.Invoke("Cleared Voiceitt's text box.");
                break;
            case ExtensionServer.ClearOutcome.TimedOut:
                _extClearPending = false;
                Log?.Invoke("Voiceitt's box kept changing, so it wasn't cleared this time.");
                break;
        }
    }

    private void ApplyDelta(string newText)
    {
        if (newText == _lastText) return;

        string toType;
        int backspaces = 0;

        if (newText.StartsWith(_lastText, StringComparison.Ordinal))
        {
            toType = newText[_lastText.Length..];
        }
        else if (AllowBackspaceCorrections)
        {
            int common = CommonPrefixLength(_lastText, newText);
            backspaces = _lastText.Length - common;
            toType = newText[common..];
            if (backspaces > MaxSafeBackspaces)
            {
                Log?.Invoke($"Skipped a large correction ({backspaces} chars) as a safety precaution.");
                _lastText = newText;
                return;
            }
        }
        else if (newText.Length > MaxPlausibleResetChars)
        {
            // Not a simple extension, AND suspiciously large — a real Voiceitt reset
            // (new utterance) or correction is normally a short phrase, not hundreds
            // of characters appearing between two 250ms polls. This pattern almost
            // always means we briefly latched onto the wrong element. Resync quietly
            // rather than typing a huge, likely-wrong blob into whatever has focus.
            Log?.Invoke($"Ignored an implausible text change ({_lastText.Length} -> {newText.Length} chars, not a simple continuation) — likely misread the page.");
            _lastText = newText;
            LastSeenText = newText;
            return;
        }
        else
        {
            // Not a simple extension (Voiceitt likely revised earlier words, e.g.
            // retroactively inserting paragraph breaks into text we already typed,
            // or reset for a new utterance). Type only what's new beyond the point
            // where the two texts still agree — retyping newText in full here would
            // duplicate everything already sent, without ever deleting anything
            // (backspacing stays opt-in via AllowBackspaceCorrections above).
            int common = CommonPrefixLength(_lastText, newText);
            toType = newText[common..];
        }

        if (backspaces == 0 && toType.Length == 0)
        {
            _lastText = newText;
            LastSeenText = newText;
            return;
        }

        if (IsVoiceittUiChrome(toType) || IsVoiceittUiChrome(newText))
        {
            Log?.Invoke($"Ignored Voiceitt's own status text (\"{Truncate(toType.Length > 0 ? toType : newText)}\") instead of typing it as if it were speech.");
            _lastText = newText;
            LastSeenText = newText;
            return;
        }

        // Voice commands see the words before anything is typed. A pause, an empty box or a
        // finished sentence before these words makes them the start of an utterance.
        DateTime now = DateTime.Now;
        bool utteranceStart = EndsUtterance(_lastText) || now - _lastBoxChange >= UtterancePause;
        _lastBoxChange = now;
        bool held = false;
        if (backspaces == 0 && VoiceCommandsEnabled && _commands != null)
        {
            if (_commands.IsHolding)
            {
                _commands.UpdateHeld(_holdStart <= newText.Length ? newText[_holdStart..] : newText, now);
                held = true;
            }
            else if (_commands.Offer(toType, utteranceStart, now))
            {
                _holdStart = newText.Length - toType.Length;
                held = true;
            }
        }
        if (held)
        {
            if (now < _feedback.SpeakingUntil)
                Log?.Invoke($"(Heard \"{Truncate(toType.Trim())}\" while the confirmation was playing — if that was the speaker, use headphones or lower the volume.)");
            _lastText = newText;
            LastSeenText = newText;
            return;
        }
        if (!TypingEnabled)
        {
            // Typing is off by voice: keep up with Voiceitt's text so nothing is typed later,
            // but only commands act on it.
            _lastText = newText;
            LastSeenText = newText;
            return;
        }

        // Voiceitt converts a spoken "new paragraph" into an actual blank-line break
        // in its own text before we ever see it — there's no literal word to match.
        // Two consecutive line breaks is how a paragraph break (as opposed to a
        // single "new line") shows up, so that's what we watch for instead.
        bool clearRequested = ClearOnTriggerWord && NewParagraphPattern.IsMatch(toType);
        if (clearRequested)
        {
            Log?.Invoke("Heard \"new paragraph\" — will clear Voiceitt's text after typing this.");
        }

        // Voiceitt's own box supplies natural spacing between words while it keeps
        // growing. Once we clear it, the next chunk starts from nothing, so without
        // this the new text would run straight into whatever was typed just before it.
        // Computed fresh each call (not consumed until success) so a failed attempt
        // retries with the same leading space next time rather than losing it.
        string finalToType = toType;
        if (_pendingLeadingSpace && finalToType.Length > 0 && !char.IsWhiteSpace(finalToType[0]))
        {
            finalToType = " " + finalToType;
        }

        bool typed = true;
        if (backspaces > 0 || finalToType.Length > 0)
        {
            typed = SendToTarget(backspaces, finalToType);
        }

        if (!typed)
        {
            // Don't advance the baseline — leave _lastText where it was so the next
            // poll recomputes this same (or larger) delta and retries automatically,
            // instead of silently discarding speech that never actually got typed.
            Log?.Invoke("Will retry this on the next check rather than losing it.");
            return;
        }

        _lastText = newText;
        LastSeenText = newText;
        if (finalToType.Length > 0) _pendingLeadingSpace = false;

        if (clearRequested)
        {
            ClearVoiceittText();
            _pendingLeadingSpace = true;
        }
    }

    private bool _pendingLeadingSpace;

    // Voiceitt reuses the same on-screen box for its idle prompt and listening
    // status, not just actual transcribed speech. Without this, every mic
    // start/stop gets typed out as if the user had said it.
    private static readonly HashSet<string> KnownVoiceittUiStrings = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tap the blue button and start dictating",
        "Listening tap to stop",
        "Listening... tap to stop",
        "Click to dictate",
    };

    private static bool IsVoiceittUiChrome(string s)
    {
        string normalized = s.Trim().TrimEnd('.', '!', '…');
        return KnownVoiceittUiStrings.Contains(normalized);
    }

    private static int CommonPrefixLength(string a, string b)
    {
        int max = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < max && a[i] == b[i]) i++;
        return i;
    }

    private bool SendToTarget(int backspaces, string text)
    {
        if (_targetHwnd == IntPtr.Zero)
        {
            Log?.Invoke("No typing target selected yet — pick a window from the dropdown.");
            SetAction("NO TARGET — pick a window under TYPE INTO");
            return false;
        }
        if (!Interop.IsWindow(_targetHwnd))
        {
            Log?.Invoke($"\"{TargetWindowTitle}\" was closed — pick a new target window from the dropdown.");
            _targetHwnd = IntPtr.Zero;
            SetAction($"TARGET CLOSED — pick a new window under TYPE INTO");
            return false;
        }

        IntPtr previousForeground = Interop.GetForegroundWindow();

        if (!ActivateWindow(_targetHwnd))
        {
            int lastErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log?.Invoke($"Couldn't bring \"{TargetWindowTitle}\" to the foreground to type into it (Win32 error {lastErr}).");
            if (++_consecutiveTypeFailures >= TypeFailureThreshold)
            {
                SetAction($"CAN'T REACH {TargetWindowTitle} — click into it once, then keep talking");
            }
            return false;
        }

        // Give the target time to actually finish activating, then verify it really
        // is the foreground window before typing anything — SetForegroundWindow can
        // return true even when the switch didn't fully land.
        System.Threading.Thread.Sleep(100);
        if (Interop.GetForegroundWindow() != _targetHwnd)
        {
            System.Threading.Thread.Sleep(150);
        }
        if (Interop.GetForegroundWindow() != _targetHwnd)
        {
            Log?.Invoke($"\"{TargetWindowTitle}\" didn't actually come to the foreground — skipped this update so nothing gets typed into the wrong place.");
            if (previousForeground != IntPtr.Zero && Interop.IsWindow(previousForeground))
                Interop.SetForegroundWindow(previousForeground);
            if (++_consecutiveTypeFailures >= TypeFailureThreshold)
            {
                SetAction($"CAN'T REACH {TargetWindowTitle} — click into it once, then keep talking");
            }
            return false;
        }

        var inputs = new List<Interop.INPUT>();
        for (int i = 0; i < backspaces; i++)
        {
            inputs.Add(KeyInput(Interop.VK_BACK, false));
            inputs.Add(KeyInput(Interop.VK_BACK, true));
        }
        foreach (char c in text)
        {
            if (c == '\n')
            {
                // Many modern text boxes (web-based compose UIs especially) only
                // insert a line break in response to an actual Enter keypress, not
                // a raw newline character typed via Unicode input — so a plain
                // character here would silently vanish instead of breaking the line.
                inputs.Add(KeyInput(Interop.VK_RETURN, false));
                inputs.Add(KeyInput(Interop.VK_RETURN, true));
            }
            else if (c == '\r')
            {
                continue; // paired with \n in \r\n; the Enter above already covers it
            }
            else
            {
                inputs.Add(UnicodeCharInput(c, false));
                inputs.Add(UnicodeCharInput(c, true));
            }
        }

        bool success = false;
        if (inputs.Count > 0)
        {
            var arr = inputs.ToArray();
            uint sent = Interop.SendInput((uint)arr.Length, arr, System.Runtime.InteropServices.Marshal.SizeOf<Interop.INPUT>());
            if (sent != arr.Length)
            {
                int lastErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Log?.Invoke($"WARNING: Windows only delivered {sent}/{arr.Length} keystrokes to \"{TargetWindowTitle}\" (Win32 error {lastErr}). The target app may be running as Administrator (needs this tool run as Administrator too), or security software is blocking simulated input.");
                if (++_consecutiveTypeFailures >= TypeFailureThreshold)
                {
                    SetAction($"BLOCKED TYPING INTO {TargetWindowTitle} — try running as Administrator");
                }
            }
            else
            {
                Log?.Invoke($"Typed into \"{TargetWindowTitle}\": {Truncate(text)}");
                success = true;
                _consecutiveTypeFailures = 0;
                if (ActionNeeded != null && !ActionNeeded.StartsWith("LOST VOICEITT")) SetAction(null);
            }
        }

        System.Threading.Thread.Sleep(30);
        if (previousForeground != IntPtr.Zero && Interop.IsWindow(previousForeground))
        {
            Interop.SetForegroundWindow(previousForeground);
        }

        return success;
    }

    /// <summary>
    /// Empties Voiceitt's own box (after a spoken "new paragraph") so it doesn't keep
    /// accumulating an ever-growing transcript. Never touches the target app's text.
    /// Through the Chrome connector this is instant and needs no window switching;
    /// without it, falls back to clicking into Voiceitt and pressing Select-All + Delete.
    /// </summary>
    private void ClearVoiceittText()
    {
        if (_ext != null && _ext.IsLive)
        {
            _ext.RequestClear();
            _extClearPending = true; // finished (or given up on) in HandleConnectorClearOutcome
            return;
        }
        ClearVoiceittTextViaWindow();
    }

    private void ClearVoiceittTextViaWindow()
    {
        if (_target == null) return;
        IntPtr voiceittHwnd = _target.WindowHandle;
        if (!Interop.IsWindow(voiceittHwnd)) return;

        IntPtr previousForeground = Interop.GetForegroundWindow();

        if (!ActivateWindow(voiceittHwnd, _target.WindowTitle))
        {
            Log?.Invoke("Couldn't switch back to Voiceitt to clear its text box.");
            return;
        }

        System.Threading.Thread.Sleep(80);

        // Bringing the browser WINDOW to the foreground doesn't guarantee the page's
        // own cursor focus lands back on the transcript element specifically —
        // especially right after activating a different app via the taskbar-click
        // workaround, which seems to leave Chrome's internal focus state unsettled.
        // A real click directly on the element is far more reliable than hoping.
        ClickElementCenter(_target.LiveElement);

        var clearInputs = new[]
        {
            KeyInput(Interop.VK_CONTROL, false),
            KeyInput(Interop.VK_A, false),
            KeyInput(Interop.VK_A, true),
            KeyInput(Interop.VK_CONTROL, true),
            KeyInput(Interop.VK_DELETE, false),
            KeyInput(Interop.VK_DELETE, true),
        };
        Interop.SendInput((uint)clearInputs.Length, clearInputs, System.Runtime.InteropServices.Marshal.SizeOf<Interop.INPUT>());

        System.Threading.Thread.Sleep(150);

        // Verify it actually worked before trusting our baseline reset — Voiceitt's
        // transcript box might not be a real editable field, in which case these
        // keystrokes would silently have no effect at all.
        AutomationElement? checkElement = _target.LiveElement;
        if (checkElement == null)
        {
            try { checkElement = ElementLocator.Relocate(_target); } catch { checkElement = null; }
        }
        string afterClear = _lastText;
        if (checkElement != null)
        {
            try { afterClear = ElementLocator.GetText(checkElement); } catch { }
        }

        if (string.IsNullOrEmpty(afterClear) || IsVoiceittUiChrome(afterClear))
        {
            Log?.Invoke("Cleared Voiceitt's text box.");
            _lastText = afterClear;
            LastSeenText = afterClear;
        }
        else
        {
            Log?.Invoke("Tried to clear Voiceitt's text box, but it still shows text — Voiceitt's box may not support being cleared this way.");
        }

        if (previousForeground != IntPtr.Zero && Interop.IsWindow(previousForeground) && previousForeground != voiceittHwnd)
        {
            Interop.SetForegroundWindow(previousForeground);
        }
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..57] + "...";

    /// <summary>Simulates a genuine mouse click at the center of an element to force real page-level focus onto it.</summary>
    private static void ClickElementCenter(AutomationElement? element)
    {
        if (element == null) return;
        System.Windows.Rect rect;
        try { rect = element.Current.BoundingRectangle; }
        catch { return; }
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;

        int x = (int)(rect.Left + rect.Width / 2);
        int y = (int)(rect.Top + rect.Height / 2);

        Interop.GetCursorPos(out var original);
        Interop.SetCursorPos(x, y);

        var clicks = new[]
        {
            new Interop.INPUT { type = Interop.INPUT_MOUSE, U = new Interop.InputUnion { mi = new Interop.MOUSEINPUT { dwFlags = Interop.MOUSEEVENTF_LEFTDOWN } } },
            new Interop.INPUT { type = Interop.INPUT_MOUSE, U = new Interop.InputUnion { mi = new Interop.MOUSEINPUT { dwFlags = Interop.MOUSEEVENTF_LEFTUP } } },
        };
        Interop.SendInput((uint)clicks.Length, clicks, System.Runtime.InteropServices.Marshal.SizeOf<Interop.INPUT>());

        System.Threading.Thread.Sleep(60);
        Interop.SetCursorPos(original.X, original.Y);
    }

    private bool ActivateWindow(IntPtr hwnd, string? windowTitleForTaskbarFallback = null)
    {
        // SetForegroundWindow alone frequently can't un-minimize a window — it needs
        // an explicit restore first, otherwise it silently fails for a minimized target.
        if (Interop.IsIconic(hwnd))
        {
            Interop.ShowWindow(hwnd, Interop.SW_RESTORE);
        }

        // Windows blocks SetForegroundWindow calls that don't originate from user
        // input. Borrowing the current foreground thread's input state via
        // AttachThreadInput is the standard way to satisfy that check from a
        // background timer callback without injecting a stray keypress into
        // whatever the user is currently looking at (e.g. Alt would toggle a
        // browser's menu-bar focus).
        IntPtr foreground = Interop.GetForegroundWindow();
        uint foreThread = Interop.GetWindowThreadProcessId(foreground, out _);
        uint curThread = Interop.GetCurrentThreadId();

        bool attached = foreThread != 0 && foreThread != curThread && Interop.AttachThreadInput(curThread, foreThread, true);
        bool ok;
        try
        {
            ok = Interop.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Interop.AttachThreadInput(curThread, foreThread, false);
        }

        if (ok) return true;

        // Store/MSIX-packaged apps run in an AppContainer and specifically refuse a
        // plain SetForegroundWindow call from an ordinary process, regardless of any
        // of the above. As a last resort, simulate a genuine click on the app's
        // taskbar button — Explorer treats that as real user input and is allowed to
        // switch focus even when we directly are not. The caller re-verifies whether
        // this actually landed before typing anything.
        return TaskbarActivator.TryClickTaskbarButtonFor(windowTitleForTaskbarFallback ?? TargetWindowTitle);
    }

    private static Interop.INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = Interop.INPUT_KEYBOARD,
        U = new Interop.InputUnion
        {
            ki = new Interop.KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? Interop.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static Interop.INPUT UnicodeCharInput(char c, bool keyUp) => new()
    {
        type = Interop.INPUT_KEYBOARD,
        U = new Interop.InputUnion
        {
            ki = new Interop.KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = Interop.KEYEVENTF_UNICODE | (keyUp ? Interop.KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    public void Dispose()
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _feedback.Dispose();
        _mic?.Dispose();
    }
}
