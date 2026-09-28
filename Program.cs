using System.Diagnostics;
using System.Windows.Forms;

namespace VoiceOS;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Voice OS and the bridge both read Voiceitt and type what it hears. Running both
        // would type every word twice, so Voice OS refuses to start next to the bridge.
        if (Process.GetProcessesByName("VoiceittBridge").Length > 0)
        {
            MessageBox.Show(
                "Voiceitt Bridge is running.\n\n" +
                "Voice OS and the bridge both type what Voiceitt hears, so with both open every " +
                "word would be typed twice. Close Voiceitt Bridge, then start Voice OS again.",
                "Voice OS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.Run(new MainForm());
    }
}
