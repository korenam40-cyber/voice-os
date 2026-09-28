using System.Windows.Automation;

namespace VoiceOS;

/// <summary>
/// Fallback for windows a plain SetForegroundWindow can't activate — notably
/// Store/MSIX-packaged apps, which run in an AppContainer and specifically block
/// programmatic foreground requests from ordinary processes. Explorer itself is
/// still allowed to switch focus in response to genuine input, so simulating an
/// actual click on the app's taskbar button can succeed where the direct API call
/// is refused outright.
/// </summary>
internal static class TaskbarActivator
{
    public static bool TryClickTaskbarButtonFor(string targetTitle)
    {
        IntPtr taskbarHwnd = Interop.FindWindow("Shell_TrayWnd", null);
        if (taskbarHwnd == IntPtr.Zero) return false;

        AutomationElement taskbar;
        try { taskbar = AutomationElement.FromHandle(taskbarHwnd); }
        catch { return false; }

        string normalizedTitle = TitleUtil.Normalize(targetTitle);
        if (normalizedTitle.Length == 0) return false;

        AutomationElement? button = FindTaskbarButton(taskbar, normalizedTitle);
        if (button == null) return false;

        System.Windows.Rect rect;
        try { rect = button.Current.BoundingRectangle; }
        catch { return false; }
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return false;

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

        System.Threading.Thread.Sleep(120);
        Interop.SetCursorPos(original.X, original.Y);

        return true; // caller re-checks whether the target actually became foreground
    }

    private static AutomationElement? FindTaskbarButton(AutomationElement taskbar, string normalizedTitle)
    {
        try
        {
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            var candidates = taskbar.FindAll(TreeScope.Descendants, condition);
            foreach (AutomationElement candidate in candidates)
            {
                string name;
                try { name = candidate.Current.Name; } catch { continue; }
                if (TitleUtil.IsSimilar(name, normalizedTitle))
                {
                    return candidate;
                }
            }
        }
        catch { }
        return null;
    }
}
