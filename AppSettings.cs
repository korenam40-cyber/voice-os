using System.IO;
using System.Text.Json;

namespace VoiceOS;

/// <summary>Everything needed to try restoring the last calibration and target
/// window automatically on the next launch, so the user isn't forced to
/// recalibrate every single time they open the tool.</summary>
internal sealed class AppSettings
{
    public string? CalibrationWindowTitle { get; set; }
    public string? CalibrationProcessExe { get; set; }
    public List<PathStep>? CalibrationPath { get; set; }

    public string? TargetWindowTitle { get; set; }
    public string? TargetProcessExe { get; set; }

    /// <summary>Microphones muted while the computer talks. Written before muting, cleared after
    /// unmuting, so a crash in between is undone at the next start.</summary>
    public List<string>? MutedMicIds { get; set; }

    /// <summary>How voice commands confirm: "sound" (default), "speech" or "off".</summary>
    public string? CommandFeedback { get; set; }

    /// <summary>Private key shared with the Chrome connector so only it can send text in.</summary>
    public string? ConnectorToken { get; set; }

    private static string Folder(string app) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), app);

    private static string FilePath
    {
        get
        {
            string dir = Folder("VoiceOS");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    /// <summary>
    /// First start of Voice OS on a machine that has the bridge: take the bridge's settings
    /// (last typing window, Voiceitt calibration) so nothing has to be set up twice. The
    /// connector key is NOT taken -- Voice OS has its own connector -- and neither is a
    /// microphone the bridge left muted, which is the bridge's to restore.
    /// </summary>
    private static AppSettings? ImportFromBridge()
    {
        try
        {
            string bridge = Path.Combine(Folder("VoiceittBridge"), "settings.json");
            if (!File.Exists(bridge)) return null;
            var imported = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(bridge));
            if (imported == null) return null;
            imported.ConnectorToken = null;
            imported.MutedMicIds = null;
            imported.Save();
            return imported;
        }
        catch
        {
            return null;
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
            else if (ImportFromBridge() is { } fromBridge)
            {
                return fromBridge;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file — just start fresh rather than crash.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort — failing to persist settings shouldn't interrupt dictation.
        }
    }
}
