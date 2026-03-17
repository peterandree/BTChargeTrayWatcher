using System.Windows.Forms;
using BTChargeTrayWatcher;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

// Install WinForms sync context before creating TrayApp so
// SynchronizationContext.Current is non-null on the UI thread
SynchronizationContext.SetSynchronizationContext(
    new WindowsFormsSynchronizationContext());

var settings = new ThresholdSettings();
var notifier = new NotificationService();
var monitor = new BluetoothBatteryMonitor(settings, notifier);
var app = new TrayApp(settings, monitor, notifier);

app.StartBackgroundScan();

app.Run();
