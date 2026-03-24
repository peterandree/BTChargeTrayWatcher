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
        const string appGuid = "BTChargeTrayWatcher-8A3F109C-4B9A-412E-921A-1D8A9F30C4D9";
        bool createdNew = false;
        _mutex = new Mutex(true, appGuid, out createdNew);

        if (!createdNew)
        {
            _mutex.Dispose();
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
            var laptopMonitor = new LaptopBatteryMonitor(settings, notifier);

            using var app = new TrayApp(settings, monitor, notifier, laptopMonitor);

            app.StartBackgroundScan();

            app.Run();
        }
        finally
        {
            if (createdNew) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
