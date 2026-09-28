namespace VoiceOS;

/// <summary>Turns "ctrl+shift+tab" into virtual-key codes. Pure, so it is tested with the matcher.</summary>
internal static class KeyChord
{
    internal readonly record struct Vk(ushort Code, bool Extended);

    private static readonly Dictionary<string, Vk> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = new(0x11, false), ["control"] = new(0x11, false),
        ["shift"] = new(0x10, false), ["alt"] = new(0x12, false), ["win"] = new(0x5B, true),
        ["enter"] = new(0x0D, false), ["escape"] = new(0x1B, false), ["esc"] = new(0x1B, false),
        ["tab"] = new(0x09, false), ["space"] = new(0x20, false), ["backspace"] = new(0x08, false),
        ["delete"] = new(0x2E, true), ["insert"] = new(0x2D, true),
        ["home"] = new(0x24, true), ["end"] = new(0x23, true),
        ["pageup"] = new(0x21, true), ["pagedown"] = new(0x22, true),
        ["left"] = new(0x25, true), ["up"] = new(0x26, true), ["right"] = new(0x27, true), ["down"] = new(0x28, true),
        ["plus"] = new(0xBB, false), ["minus"] = new(0xBD, false),
        ["playpause"] = new(0xB3, true), ["nexttrack"] = new(0xB0, true), ["prevtrack"] = new(0xB1, true),
        ["volumeup"] = new(0xAF, true), ["volumedown"] = new(0xAE, true), ["volumemute"] = new(0xAD, true),
    };

    public static bool TryParse(string chord, out List<Vk> keys, out string error)
    {
        keys = new List<Vk>();
        error = "";
        foreach (string part in chord.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim();
            if (Named.TryGetValue(p, out var named)) { keys.Add(named); continue; }
            if (p.Length == 1 && char.IsLetterOrDigit(p[0])) { keys.Add(new((ushort)char.ToUpperInvariant(p[0]), false)); continue; }
            if (p.Length >= 2 && (p[0] == 'f' || p[0] == 'F') && int.TryParse(p[1..], out int n) && n is >= 1 and <= 24)
            {
                keys.Add(new((ushort)(0x70 + n - 1), false));
                continue;
            }
            error = $"unknown key \"{p}\" in \"{chord}\"";
            return false;
        }
        if (keys.Count == 0) error = $"empty key chord \"{chord}\"";
        return keys.Count > 0;
    }
}
