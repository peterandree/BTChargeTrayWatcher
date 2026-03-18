using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public partial class TrayApp : IDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly BluetoothBatteryMonitor _monitor;
    private readonly DeviceDumper _dumper = new();
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _lowMenu;
    private readonly ToolStripMenuItem _highMenu;
    private readonly ToolStripMenuItem _devicesMenu;
    private readonly ToolStripMenuItem _scanMenuItem;
    private readonly SynchronizationContext _uiContext;

    private ScanWindow? _scanWindow;
    private bool _disposed;
    private bool _exitStarted;

    public TrayApp(
        ThresholdSettings settings,
        BluetoothBatteryMonitor monitor)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayApp must be created on the UI thread.");

        _settings = settings;
        _monitor = monitor;

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true
        };

        _lowMenu = BuildLowMenu();
        _highMenu = BuildHighMenu();
        _devicesMenu = BuildDevicesMenu();
        _scanMenuItem = new ToolStripMenuItem("Scan devices…");
        _scanMenuItem.Click += ScanMenuItem_Click;

        _trayIcon.ContextMenuStrip = BuildContextMenu();
        _trayIcon.DoubleClick += TrayIcon_DoubleClick;

        _monitor.ScanStarted += Monitor_ScanStarted;
        _monitor.ScanCompleted += Monitor_ScanCompleted;

        _settings.Changed += UpdateTooltip;
        UpdateTooltip();
    }

    public void Run() => Application.Run();

    public Task StartBackgroundScanAsync() => RunStartupScanAsync();

    public void StartBackgroundScan() =>
        FireAndForget(RunStartupScanAsync(), "Startup scan");

    private void PostToUi(Action action)
    {
        if (_disposed)
            return;

        _uiContext.Post(_ =>
        {
            if (_disposed)
                return;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayApp] UI dispatch fault: {ex}");
            }
        }, null);
    }

    private void FireAndForget(Task task, string operationName)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
                Debug.WriteLine($"[TrayApp] {operationName} fault: {t.Exception}");
        }, TaskScheduler.Default);
    }

    private async Task RunUiActionAsync(Func<Task> action, string operationName)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayApp] {operationName} fault: {ex}");
        }
    }
}
