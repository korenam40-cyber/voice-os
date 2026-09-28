using System.Diagnostics;
using System.IO;

namespace VoiceOS;

/// <summary>
/// Carries out a recognized voice command. Commands act on the window the user dictates
/// into (the typing target), brought forward the same verified way typing is — the user
/// asked for that (2026-09-27), since the window in front is often Voiceitt's own.
/// </summary>
internal sealed class CommandExecutor
{
    private readonly Func<IntPtr> _target;
    private readonly Func<IntPtr, bool> _activate;
    private readonly Action<IntPtr, string> _setTarget;
    private readonly Action<string> _log;

    /// <summary>An app asked for by "open …" that is still starting: once its window appears,
    /// it becomes the dictation target.</summary>
    private (string Process, DateTime Until)? _awaitingApp;

    public CommandExecutor(Func<IntPtr> target, Func<IntPtr, bool> activate,
                           Action<IntPtr, string> setTarget, Action<string> log)
    {
        _target = target;
        _activate = activate;
        _setTarget = setTarget;
        _log = log;
    }

    /// <summary>Runs the action. Returns false, with a reason, when it could not.</summary>
    public bool Execute(CommandAction action, out string error)
    {
        error = "";
        switch (action.Type.ToLowerInvariant())
        {
            case "none":
                return true;
            case "keys":
                return SendKeys(action.Keys ?? "", action.Global, out error);
            case "scroll":
                return Scroll(action.Amount, out error);
            case "window":
                return WindowOp(action.Value ?? "", out error);
            case "app":
                return SwitchOrLaunch(action, out error);
            default:
                error = $"unknown action type \"{action.Type}\"";
                return false;
        }
    }

    /// <summary>Call on every poll: finishes an "open …" whose app took a moment to start.</summary>
    public void Tick()
    {
        if (_awaitingApp is not { } wait) return;
        if (DateTime.Now > wait.Until)
        {
            _awaitingApp = null;
            _log($"{wait.Process} didn't show a window in time — dictation stays where it was.");
            return;
        }
        var win = FindAppWindow(wait.Process);
        if (win == null) return;
        _awaitingApp = null;
        _setTarget(win.Handle, win.Title);
    }

    private bool SendKeys(string keys, bool global, out string error)
    {
        error = "";
        var chords = keys.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var inputs = new List<Interop.INPUT>();
        foreach (string chord in chords)
        {
            if (!KeyChord.TryParse(chord, out var vks, out error)) return false;
            foreach (var vk in vks) inputs.Add(Key(vk, up: false));
            for (int i = vks.Count - 1; i >= 0; i--) inputs.Add(Key(vks[i], up: true));
        }
        if (inputs.Count == 0) { error = "no keys given"; return false; }

        if (!global && !BringTargetForward(out error)) return false;
        return Send(inputs, out error);
    }

    private bool Scroll(int notches, out string error)
    {
        if (!BringTargetForward(out error)) return false;
        var input = new Interop.INPUT
        {
            type = Interop.INPUT_MOUSE,
            U = new Interop.InputUnion
            {
                mi = new Interop.MOUSEINPUT { mouseData = unchecked((uint)(notches * Interop.WHEEL_DELTA)), dwFlags = Interop.MOUSEEVENTF_WHEEL }
            }
        };
        return Send(new List<Interop.INPUT> { input }, out error);
    }

    private bool WindowOp(string op, out string error)
    {
        error = "";
        IntPtr hwnd = _target();
        if (hwnd == IntPtr.Zero || !Interop.IsWindow(hwnd))
        {
            error = "no dictation window is selected";
            return false;
        }
        switch (op.ToLowerInvariant())
        {
            case "minimize": Interop.ShowWindow(hwnd, Interop.SW_MINIMIZE); return true;
            case "maximize": Interop.ShowWindow(hwnd, Interop.SW_MAXIMIZE); _activate(hwnd); return true;
            case "restore": Interop.ShowWindow(hwnd, Interop.SW_RESTORE); _activate(hwnd); return true;
            case "close": Interop.PostMessage(hwnd, Interop.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); return true;
            default: error = $"unknown window action \"{op}\""; return false;
        }
    }

    private bool SwitchOrLaunch(CommandAction action, out string error)
    {
        error = "";
        string process = action.Process ?? "";
        var existing = process.Length > 0 ? FindAppWindow(process) : null;
        if (existing != null)
        {
            _setTarget(existing.Handle, existing.Title);
            if (!_activate(existing.Handle)) { error = $"couldn't bring {existing.Title} forward"; return false; }
            return true;
        }
        if (string.IsNullOrWhiteSpace(action.Launch)) { error = $"{process} isn't running"; return false; }

        string launch = Environment.ExpandEnvironmentVariables(action.Launch);
        try
        {
            Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            error = $"couldn't start {launch}: {ex.Message}";
            return false;
        }
        if (process.Length > 0) _awaitingApp = (process, DateTime.Now.AddSeconds(15));
        return true;
    }

    private static WindowChoice? FindAppWindow(string process)
    {
        string want = Path.GetFileNameWithoutExtension(process);
        return WindowPicker.EnumerateCandidateWindows(IntPtr.Zero)
            .FirstOrDefault(w => string.Equals(Path.GetFileNameWithoutExtension(w.ProcessName), want,
                                               StringComparison.OrdinalIgnoreCase));
    }

    private bool BringTargetForward(out string error)
    {
        error = "";
        IntPtr hwnd = _target();
        if (hwnd == IntPtr.Zero || !Interop.IsWindow(hwnd))
        {
            error = "no dictation window is selected — pick one under TYPE INTO";
            return false;
        }
        if (Interop.GetForegroundWindow() == hwnd) return true;
        if (!_activate(hwnd)) { error = "couldn't bring the dictation window forward"; return false; }
        Thread.Sleep(100);
        if (Interop.GetForegroundWindow() != hwnd) Thread.Sleep(150);
        if (Interop.GetForegroundWindow() != hwnd)
        {
            // Same rule as typing: never send keys to a window we didn't mean.
            error = "the dictation window didn't come forward, so nothing was sent";
            return false;
        }
        return true;
    }

    private static bool Send(List<Interop.INPUT> inputs, out string error)
    {
        error = "";
        var arr = inputs.ToArray();
        uint sent = Interop.SendInput((uint)arr.Length, arr, System.Runtime.InteropServices.Marshal.SizeOf<Interop.INPUT>());
        if (sent == arr.Length) return true;
        error = $"Windows accepted only {sent}/{arr.Length} inputs (the window may be running as Administrator)";
        return false;
    }

    private static Interop.INPUT Key(KeyChord.Vk vk, bool up) => new()
    {
        type = Interop.INPUT_KEYBOARD,
        U = new Interop.InputUnion
        {
            ki = new Interop.KEYBDINPUT
            {
                wVk = vk.Code,
                dwFlags = (up ? Interop.KEYEVENTF_KEYUP : 0) | (vk.Extended ? Interop.KEYEVENTF_EXTENDEDKEY : 0),
            }
        }
    };
}
