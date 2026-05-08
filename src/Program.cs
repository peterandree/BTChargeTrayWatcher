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
        _mutex = new Mutex(true, appGuid, out bool createdNew);

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

            var settings    = new ThresholdSettings();
            var persistence = new SettingsPersistence(settings);
            persistence.Load();

            var toastService = new NotificationService();
            var ntfyChannel  = new NtfyNotificationChannel(settings.GetNtfySettings());

            // Build dispatcher: toast always on, ntfy gated by its own IsEnabled check.
            var dispatcher = new NotificationDispatcher(
            [
                new WindowsToastNotificationChannel(toastService),
                ntfyChannel
            ]);

            var monitor       = new BluetoothBatteryMonitor(settings, dispatcher);
            var laptopMonitor = new LaptopBatteryMonitor(settings, dispatcher);

            try
            {
                using var app = new TrayApp(settings, monitor, toastService, laptopMonitor, ntfyChannel);

                app.StartBackgroundScan();

                TrayApp.Run();
            }
            finally
            {
                monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
                laptopMonitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            if (createdNew) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
