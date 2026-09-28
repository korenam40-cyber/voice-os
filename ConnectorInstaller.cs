using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace VoiceOS;

/// <summary>
/// Writes the Chrome connector extension to a plain folder so it can be loaded once via
/// chrome://extensions → "Load unpacked". The extension's source lives in the repo's
/// extension/ folder and is embedded in the exe; config.js (port + private token) is
/// generated per machine and is the only file not shipped as source.
/// </summary>
internal static class ConnectorInstaller
{
    private static readonly string[] SourceFiles = { "manifest.json", "background.js", "content.js" };

    // On the Desktop, not under AppData: Chrome's "Load unpacked" picker doesn't show
    // hidden folders, so a copy under %APPDATA% looks as if it doesn't exist.
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Voice OS Chrome Connector");

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>Writes/updates the files. Returns true if anything actually changed.</summary>
    public static bool Install(string token)
    {
        Directory.CreateDirectory(Folder);
        bool changed = false;

        foreach (var name in SourceFiles)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ext/" + name)
                ?? throw new InvalidOperationException($"Extension file {name} is missing from the app.");
            using var reader = new StreamReader(stream);
            changed |= WriteIfDifferent(Path.Combine(Folder, name), reader.ReadToEnd());
        }

        changed |= WriteIfDifferent(Path.Combine(Folder, "config.js"),
            $"const BRIDGE = {{ port: {ExtensionServer.Port}, token: \"{token}\" }};\n");
        return changed;
    }

    private static bool WriteIfDifferent(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;
        File.WriteAllText(path, content);
        return true;
    }
}
