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
            var orchestrator          = new BatteryReaderOrchestrator(gattConnectionManager, classicReader, capabilityCache);
            var deviceWatcher         = new DeviceWatcherService();

            var monitor = new BluetoothBatteryMonitor(
                settings, dispatcher, deviceWatcher, orchestrator, gattConnectionManager, capabilityCache);
            var laptopMonitor = new LaptopBatteryMonitor(settings, dispatcher);

            // Register disposal on ApplicationExit so we never block the STA message-pump
            // thread on an async operation (deadlock risk when continuations capture the
            // WinForms SynchronizationContext). Task.Run moves the await onto a pool thread.
            Application.ApplicationExit += (_, _) =>
            {
                Task.Run(async () => await monitor.DisposeAsync()).GetAwaiter().GetResult();
                Task.Run(async () => await laptopMonitor.DisposeAsync()).GetAwaiter().GetResult();
            };

            deviceWatcher.Start();
            using var app = new TrayApp(settings, monitor, toastService, laptopMonitor);

            app.StartBackgroundScan();

            TrayApp.Run();
        }
        finally
        {
            if (createdNew) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
