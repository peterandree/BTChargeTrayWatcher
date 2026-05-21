using System;
using System.Threading;
using System.Threading.Tasks;
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

        BluetoothBatteryMonitor? monitor       = null;
        LaptopBatteryMonitor?    laptopMonitor = null;

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

            // Cooperation stack: device watcher + GATT connection manager + capability cache.
            var capabilityCache       = new DeviceCapabilityCache();
            var gattConnectionManager = new GattConnectionManager();
            var classicReader         = new ClassicBatteryReader();
            var orchestrator          = new BatteryReaderOrchestrator(gattConnectionManager, classicReader, capabilityCache, settings);
            var deviceWatcher         = new DeviceWatcherService();

            monitor       = new BluetoothBatteryMonitor(
                settings, dispatcher, deviceWatcher, orchestrator, gattConnectionManager, capabilityCache);
            laptopMonitor = new LaptopBatteryMonitor(settings, dispatcher);

            deviceWatcher.Start();
            using var app = new TrayApp(settings, monitor, toastService, laptopMonitor);

            app.StartBackgroundScan();

            TrayApp.Run();
        }
        finally
        {
            // The WinForms message pump has stopped at this point — no active
            // SynchronizationContext exists on this thread. Task.Run moves the
            // await onto a pool thread where no WinForms context can be captured,
            // eliminating the deadlock risk that existed when disposal ran inside
            // the ApplicationExit handler.
            if (monitor is not null)
                Task.Run(async () => await monitor.DisposeAsync().ConfigureAwait(false))
                    .GetAwaiter().GetResult();

            if (laptopMonitor is not null)
                Task.Run(async () => await laptopMonitor.DisposeAsync().ConfigureAwait(false))
                    .GetAwaiter().GetResult();

            if (createdNew) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
