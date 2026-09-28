using System.Globalization;
using System.Text;

namespace VoiceOS;

internal static class TitleUtil
{
    /// <summary>Strips bidi direction-formatting marks (U+200E/200F, U+202A-U+202E, etc.)
    /// that show up embedded in RTL-locale window titles but aren't real text.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>True if two window titles are close enough to be considered the same window
    /// across a restart (one may be a truncated/prefixed variant of the other).</summary>
    public static bool IsSimilar(string a, string b)
    {
        string na = Normalize(a);
        string nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        return na.Contains(nb, StringComparison.OrdinalIgnoreCase) || nb.Contains(na, StringComparison.OrdinalIgnoreCase);
    }
}
