using System;
using System.Threading;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    static void Main()
    {
        // Enforce single instance globally across the user session
        const string appGuid = "BTChargeTrayWatcher-8A3F109C-4B9A-412E-921A-1D8A9F30C4D9";
        _mutex = new Mutex(true, appGuid, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running. Exit silently.
            return;
        }

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var settings = new ThresholdSettings();
            var notifier = new NotificationService();
            var monitor = new BluetoothBatteryMonitor(settings, notifier);

            using var app = new TrayApp(settings, monitor, notifier);

            // Launch silent background scan on startup to warm up the cache
            app.StartBackgroundScan();

            app.Run();
        }
        finally
        {
            if (_mutex is not null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }
    }
}
