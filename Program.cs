using DualSenseBTTriggers.Tray;

namespace DualSenseBTTriggers;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\DualSenseBT-Triggers-SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running in the System Tray.
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new TrayApplicationContext();
        Application.Run(context);
    }
}
