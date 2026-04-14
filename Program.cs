using System.Windows.Forms;

namespace SttApp;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        using var trayApp = new TrayApp();
        Application.Run(trayApp);
    }
}
