using BTChargeTrayWatcher;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

var settings = new ThresholdSettings();
var notifier = new NotificationService();
var monitor = new BluetoothBatteryMonitor(settings, notifier);
var app = new TrayApp(settings, monitor, notifier);

// Trigger scan immediately on startup — scan window opens automatically
app.OpenScanWindow();

app.Run();
