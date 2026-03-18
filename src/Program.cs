namespace BTChargeTrayWatcher;

internal static class Program
{
    private const string AppMutexName = @"Local\BTChargeTrayWatcher.SingleInstance";

    [STAThread]
    private static void Main()
    {
        bool createdNew;
        using Mutex mutex = new(initiallyOwned: true, name: AppMutexName, createdNew: out createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "BT Charge Tray Watcher is already running.",
                "BT Charge Tray Watcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        GC.KeepAlive(mutex);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        SynchronizationContext.SetSynchronizationContext(
            new WindowsFormsSynchronizationContext());

        var settings = new ThresholdSettings();
        var notifier = new NotificationService();
        var monitor = new BluetoothBatteryMonitor(settings, notifier);
        var app = new TrayApp(settings, monitor, notifier);

        app.StartBackgroundScan();
        app.Run();
    }
}
