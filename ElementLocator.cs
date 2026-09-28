using System.Windows.Automation;

namespace VoiceOS;

/// <summary>
/// One step in the path from a top-level window's root UI Automation element
/// down to the calibrated transcript element. We re-walk this path on every
/// poll instead of holding a live AutomationElement, because single-page web
/// apps (like Voiceitt running in a browser tab) frequently replace DOM nodes
/// under the hood, which invalidates any previously captured element.
/// </summary>
internal sealed class PathStep
{
    public required string ControlType { get; init; }
    public required string ClassName { get; init; }
    public required int Index { get; init; } // ordinal among siblings with same ControlType+ClassName
}

internal sealed class CalibratedTarget
{
    public IntPtr WindowHandle { get; init; }
    public string WindowTitle { get; init; } = "";
    public string TextPreview { get; init; } = "";
    public List<PathStep> Path { get; init; } = new();

    /// <summary>
    /// The actual element captured at calibration time. Re-checked directly on every
    /// poll before falling back to walking Path — web apps often mutate a node's text
    /// in place without replacing it, so the original reference frequently still
    /// works even while sibling structure elsewhere on the page keeps changing.
    /// </summary>
    public AutomationElement? LiveElement { get; set; }
}

internal static class ElementLocator
{
    private static readonly TreeWalker Walker = TreeWalker.RawViewWalker;

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "msedgewebview2.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe"
    };

    /// <summary>True if the given window belongs to a known browser process (Voiceitt runs as a web app).</summary>
    public static bool IsBrowserWindow(IntPtr hwnd, out string processExeName)
    {
        processExeName = GetProcessExeName(hwnd);
        return BrowserProcessNames.Contains(processExeName);
    }

    /// <summary>Resolves the executable filename (e.g. "WINWORD.EXE") that owns the given window, or "" if it can't be determined.</summary>
    public static string GetProcessExeName(IntPtr hwnd) => System.IO.Path.GetFileName(GetProcessExePath(hwnd));

    /// <summary>The full path of the executable that owns the given window, or "".</summary>
    public static string GetProcessExePath(IntPtr hwnd)
    {
        Interop.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return "";

        IntPtr hProcess = Interop.OpenProcess(Interop.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return "";
        try
        {
            var sb = new System.Text.StringBuilder(260);
            int size = sb.Capacity;
            if (!Interop.QueryFullProcessImageName(hProcess, 0, sb, ref size)) return "";
            return sb.ToString();
        }
        finally
        {
            Interop.CloseHandle(hProcess);
        }
    }

    /// <summary>Capture the element under the given screen point and build a relocatable path for it.</summary>
    public static CalibratedTarget? Calibrate(Interop.POINT screenPoint)
    {
        AutomationElement? element;
        try
        {
            element = AutomationElement.FromPoint(new System.Windows.Point(screenPoint.X, screenPoint.Y));
        }
        catch
        {
            return null;
        }
        if (element == null) return null;

        IntPtr hwnd = Interop.GetAncestor(Interop.WindowFromPoint(screenPoint), Interop.GA_ROOT);
        if (hwnd == IntPtr.Zero) return null;

        AutomationElement? rootElement;
        try
        {
            rootElement = AutomationElement.FromHandle(hwnd);
        }
        catch
        {
            return null;
        }
        if (rootElement == null) return null;

        // Whatever sits under the pointer is often NOT the text field itself — an empty
        // box shows placeholder text or a padding container, and a slightly-off hover
        // lands on a neighbour. Calibrating on that captures an element that never
        // carries the transcript (its text reads as "" forever while Voiceitt has text).
        // If it isn't a real text field, snap to the transcript box instead.
        if (!IsTextBox(element))
        {
            var snapped = FindTranscriptBox(rootElement, new System.Windows.Point(screenPoint.X, screenPoint.Y));
            if (snapped != null) element = snapped;
        }

        return BuildTarget(hwnd, rootElement, element);
    }

    private static CalibratedTarget? BuildTarget(IntPtr hwnd, AutomationElement rootElement, AutomationElement element)
    {
        // Walk up from the element to the window root, recording each step,
        // then reverse so the path reads root -> ... -> target.
        var chain = new List<AutomationElement>();
        var current = element;
        int safety = 0;
        while (current != null && safety++ < 200)
        {
            chain.Add(current);
            if (Automation.Compare(current, rootElement)) break;
            AutomationElement? parent;
            try { parent = Walker.GetParent(current); }
            catch { parent = null; }
            current = parent;
        }
        chain.Reverse();

        if (chain.Count == 0 || !Automation.Compare(chain[0], rootElement))
        {
            // Didn't reach the window root within the safety limit; bail out rather
            // than saving a path that can't be trusted.
            return null;
        }

        var path = new List<PathStep>();
        for (int i = 1; i < chain.Count; i++)
        {
            var step = BuildStep(chain[i - 1], chain[i]);
            if (step == null) return null;
            path.Add(step);
        }

        var sb = new System.Text.StringBuilder(256);
        Interop.GetWindowText(hwnd, sb, sb.Capacity);

        string preview;
        try { preview = GetText(element); } catch { preview = ""; }

        return new CalibratedTarget
        {
            WindowHandle = hwnd,
            WindowTitle = sb.ToString(),
            TextPreview = preview,
            Path = path,
            LiveElement = element
        };
    }

    private static PathStep? BuildStep(AutomationElement parent, AutomationElement child)
    {
        string controlType = SafeControlTypeName(child);
        string className = SafeClassName(child);

        int index = 0;
        try
        {
            var sibling = Walker.GetFirstChild(parent);
            int count = 0;
            while (sibling != null)
            {
                if (SafeControlTypeName(sibling) == controlType && ClassNamesMatch(SafeClassName(sibling), className))
                {
                    if (Automation.Compare(sibling, child))
                    {
                        index = count;
                        break;
                    }
                    count++;
                }
                sibling = Walker.GetNextSibling(sibling);
            }
        }
        catch
        {
            return null;
        }

        return new PathStep { ControlType = controlType, ClassName = className, Index = index };
    }

    /// <summary>
    /// Re-finds the calibrated element: first by re-walking its saved path, and if the
    /// page's structure has shifted so the path no longer resolves, by locating the
    /// transcript text field directly. Returns null only if neither works.
    /// </summary>
    public static AutomationElement? Relocate(CalibratedTarget target)
    {
        var walked = WalkPath(target);
        if (walked != null) return walked;

        var leaf = target.Path.Count > 0 ? target.Path[^1] : null;
        if (leaf == null || leaf.ControlType != ControlType.Edit.ProgrammaticName) return null;
        if (!Interop.IsWindow(target.WindowHandle)) return null;
        try
        {
            var root = AutomationElement.FromHandle(target.WindowHandle);
            return root == null ? null : FindTranscriptBox(root, null, leaf.ClassName);
        }
        catch { return null; }
    }

    private static AutomationElement? WalkPath(CalibratedTarget target)
    {
        if (!Interop.IsWindow(target.WindowHandle)) return null;

        AutomationElement? node;
        try
        {
            node = AutomationElement.FromHandle(target.WindowHandle);
        }
        catch
        {
            return null;
        }
        if (node == null) return null;

        foreach (var step in target.Path)
        {
            AutomationElement? found = null;
            try
            {
                var sibling = Walker.GetFirstChild(node);
                int count = 0;
                while (sibling != null)
                {
                    if (SafeControlTypeName(sibling) == step.ControlType && ClassNamesMatch(SafeClassName(sibling), step.ClassName))
                    {
                        if (count == step.Index) { found = sibling; break; }
                        count++;
                    }
                    sibling = Walker.GetNextSibling(sibling);
                }
                // Deliberately no "closest available index" fallback here: guessing
                // wrong hands back an unrelated element that can be much bigger than
                // the real target, which then gets read as a huge, wrong text blob.
                // A clean failure (caller re-tries next poll) is far safer.
            }
            catch
            {
                return null;
            }

            if (found == null) return null;
            node = found;
        }

        return node;
    }

    /// <summary>Best-effort text extraction: TextPattern, then ValuePattern, then the Name property.</summary>
    public static string GetText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) && patternObj is TextPattern textPattern)
            {
                var text = textPattern.DocumentRange.GetText(-1);
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        catch { /* pattern not supported on this element right now */ }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valObj) && valObj is ValuePattern valuePattern)
            {
                var text = valuePattern.Current.Value;
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        catch { }

        try
        {
            return element.Current.Name ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string SafeControlTypeName(AutomationElement e)
    {
        try { return e.Current.ControlType.ProgrammaticName; } catch { return "?"; }
    }

    private static string SafeClassName(AutomationElement e)
    {
        try { return e.Current.ClassName ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// Web frameworks (Material-UI especially) toggle extra state classes on and off
    /// dynamically — e.g. "Mui-focused" appears only while a field has keyboard focus.
    /// An exact string match would treat the same element as "different" the moment
    /// its focus/hover/error state changes since calibration. Comparing as token sets
    /// and accepting containment either way tolerates those additions/removals while
    /// still requiring the stable classes to actually match.
    /// </summary>
    private static bool ClassNamesMatch(string a, string b)
    {
        if (a == b) return true;

        // Two kinds of token change on a live page without the element being a
        // different element, and must not take part in the comparison:
        //  - state classes ("Mui-focused", "Mui-error", ...) toggle with focus/hover;
        //  - emotion's generated "css-<hash>" class is derived from the element's
        //    computed styles, so it changes whenever the style props do — Voiceitt's
        //    transcript container gets a new hash while the mic is recording, which
        //    made the saved path fail to resolve exactly while the user was speaking.
        var stableA = StableTokens(a);
        var stableB = StableTokens(b);
        if (stableA.Count == 0 && stableB.Count == 0) return true; // anonymous on both sides: index decides
        if (stableA.Count == 0 || stableB.Count == 0) return false;

        return stableA.IsSubsetOf(stableB) || stableB.IsSubsetOf(stableA);
    }

    private static HashSet<string> StableTokens(string className)
    {
        var set = new HashSet<string>();
        foreach (var t in className.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.StartsWith("css-", StringComparison.Ordinal)) continue;
            if (t.StartsWith("Mui-", StringComparison.Ordinal)) continue;
            set.Add(t);
        }
        return set;
    }

    /// <summary>True for a real text field: an Edit control that exposes its text.</summary>
    public static bool IsTextBox(AutomationElement e)
    {
        try
        {
            if (e.Current.ControlType != ControlType.Edit) return false;
            return e.TryGetCurrentPattern(ValuePattern.Pattern, out _) || e.TryGetCurrentPattern(TextPattern.Pattern, out _);
        }
        catch { return false; }
    }

    /// <summary>
    /// Finds Voiceitt's transcript box by what it is — a large, on-screen text field
    /// built from Material-UI, preferring a multiline one — rather than by its position
    /// in a deep tree. <paramref name="near"/> (a screen point) strongly prefers the
    /// field under it; <paramref name="leafClass"/> prefers a field whose stable classes
    /// match a previously calibrated one.
    /// </summary>
    public static AutomationElement? FindTranscriptBox(AutomationElement root, System.Windows.Point? near = null, string? leafClass = null)
    {
        AutomationElementCollection all;
        try
        {
            all = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        }
        catch { return null; }

        AutomationElement? best = null;
        double bestScore = double.MinValue;
        foreach (AutomationElement e in all)
        {
            System.Windows.Rect r;
            try { r = e.Current.BoundingRectangle; } catch { continue; }
            if (r.IsEmpty || r.Width <= 1 || r.Height <= 1) continue;
            if (!IsTextBox(e)) continue;

            string cn = SafeClassName(e);
            if (!cn.Contains("Mui", StringComparison.Ordinal)) continue; // skips the browser's own address field

            double score = Math.Min(r.Width * r.Height / 10000.0, 50);
            if (cn.Contains("inputMultiline", StringComparison.Ordinal)) score += 100;
            if (leafClass != null && ClassNamesMatch(cn, leafClass)) score += 200;
            if (near is System.Windows.Point p && r.Contains(p)) score += 1000;

            if (score > bestScore) { best = e; bestScore = score; }
        }
        return best;
    }

    /// <summary>
    /// Locates the transcript box in the given browser window on its own and builds a
    /// calibration for it — no hovering or F8 needed.
    /// </summary>
    public static CalibratedTarget? AutoCalibrate(IntPtr hwnd)
    {
        if (!Interop.IsWindow(hwnd)) return null;
        AutomationElement root;
        try { root = AutomationElement.FromHandle(hwnd); }
        catch { return null; }

        var box = FindTranscriptBox(root);
        return box == null ? null : BuildTarget(hwnd, root, box);
    }
}
