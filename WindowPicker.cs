namespace VoiceOS;

internal sealed record WindowChoice(IntPtr Handle, string Title, string ProcessName)
{
    public override string ToString() => string.IsNullOrEmpty(ProcessName) ? Title : $"{Title}  —  {ProcessName}";
}

/// <summary>Enumerates real, user-visible top-level windows for the "type into" picker.</summary>
internal static class WindowPicker
{
    public static List<WindowChoice> EnumerateCandidateWindows(IntPtr excludeHwnd)
    {
        uint ownProcessId = (uint)Environment.ProcessId;
        var results = new List<WindowChoice>();

        Interop.EnumWindows((hwnd, _) =>
        {
            if (hwnd == excludeHwnd) return true;
            if (!Interop.IsWindowVisible(hwnd)) return true;
            if (Interop.GetWindowTextLength(hwnd) == 0) return true;
            if (Interop.GetWindow(hwnd, Interop.GW_OWNER) != IntPtr.Zero) return true; // skip owned popups/dialogs
            if ((Interop.GetWindowLong(hwnd, Interop.GWL_EXSTYLE) & Interop.WS_EX_TOOLWINDOW) != 0) return true;

            Interop.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownProcessId) return true; // never list our own window(s)

            var sb = new System.Text.StringBuilder(256);
            Interop.GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            string exeName = ElementLocator.GetProcessExeName(hwnd);
            results.Add(new WindowChoice(hwnd, title, exeName));
            return true;
        }, IntPtr.Zero);

        return results.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
