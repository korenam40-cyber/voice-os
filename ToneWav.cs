using System.IO;

namespace VoiceOS;

/// <summary>Builds the confirmation tones in memory. Pure, so it is tested with the matcher.</summary>
internal static class ToneWav
{
    /// <summary>A mono 16-bit WAV of the given notes (frequency 0 is a rest), with short fades
    /// so the notes don't click.</summary>
    public static byte[] Make(params (int Hz, double Seconds)[] notes)
    {
        const int rate = 22050;
        var samples = new List<short>();
        foreach (var (hz, seconds) in notes)
        {
            int n = (int)(rate * seconds);
            int fade = Math.Min(n / 4, rate / 100);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)Math.Max(fade, 1));
                double v = hz == 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / rate) * env * 0.35;
                samples.Add((short)(v * short.MaxValue));
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples.Count * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataBytes);
        foreach (short s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
