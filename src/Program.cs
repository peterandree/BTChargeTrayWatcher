namespace BTChargeTrayWatcher;

internal static class Program
{
    private const string AppGuid = "BTChargeTrayWatcher_8A9B1C2D-3E4F-5G6H";
    private static Mutex? _mutex;
    private static EventWaitHandle? _showInstanceEvent;

    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 1. Attempt to claim the global mutex
        _mutex = new Mutex(true, $"Global\\{AppGuid}_Mutex", out bool createdNew);

        if (!createdNew)
        {
            // 2. We are the second instance. Signal the first instance to show its window.
            try
            {
                if (EventWaitHandle.TryOpenExisting($"Global\\{AppGuid}_Event", out var evt))
                {
                    evt.Set();
                }
            }
            catch
            {
                // Ignore IPC faults on secondary exit
            }

            return; // Terminate this duplicate instance immediately
        }

        // 3. We are the primary instance. Create the event listener.
        _showInstanceEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Global\\{AppGuid}_Event");

        var settings = new ThresholdSettings();
        var notifier = new NotificationService();
        var monitor = new BluetoothBatteryMonitor(settings, notifier);

        var app = new TrayApp(settings, monitor, notifier);

        // 4. Start a background thread to listen for secondary instance launches
        Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    _showInstanceEvent.WaitOne();

                    // OpenScanWindowAsync internally marshals to the UI thread via TrayApp's PostToUi
                    _ = app.OpenScanWindowAsync();
                }
                catch
                {
                    // Handle disposal/shutdown safely
                    break;
                }
            }
        });

        app.StartBackgroundScan();
        app.Run(); // Begins the WinForms message loop

        // 5. Cleanup on application exit
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _showInstanceEvent.Dispose();
    }
}
