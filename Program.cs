using System.Diagnostics;
using System.Windows.Forms;
using Velopack;

namespace SttApp;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        VelopackApp.Build()
            .WithFirstRun(v => ShowPostInstallInfo())
            .Run();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        using var trayApp = new TrayApp();
        Application.Run(trayApp);
    }

    private static void ShowPostInstallInfo()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var installDir = AppContext.BaseDirectory;
        var modelsDir = Path.Combine(installDir, "models");
        Directory.CreateDirectory(modelsDir);

        var message =
            "Prompto byl úspěšně nainstalován!\n\n" +
            $"Umístění aplikace:\n  {installDir}\n\n" +
            $"Složka pro model:\n  {modelsDir}\n\n" +
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
            "DŮLEŽITÉ – Whisper model (~3 GB):\n\n" +
            "Prompto potřebuje Whisper model pro přepis řeči.\n" +
            "Bez něj aplikace nebude fungovat.\n\n" +
            "  1.  Stáhněte soubor ggml-large-v3.bin z:\n" +
            "      huggingface.co/ggerganov/whisper.cpp\n\n" +
            "  2.  Zkopírujte ho do složky models\\\n" +
            $"      ({modelsDir})\n\n" +
            "  3.  Spusťte Prompto – model se načte při startu.\n" +
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n" +
            "Chcete nyní otevřít složku models?";

        var result = MessageBox.Show(message, "Prompto – instalace dokončena",
            MessageBoxButtons.YesNo, MessageBoxIcon.Information);

        if (result == DialogResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = modelsDir,
                UseShellExecute = true
            });
        }
    }
}
