using System.Windows.Forms;
using Velopack;

namespace SttApp;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        VelopackApp.Build().Run();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        using var trayApp = new TrayApp();
        Application.Run(trayApp);
    }
}
