// src/Tray/TrayApp.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public sealed class TrayApp : IDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly BluetoothBatteryMonitor _monitor;
    private readonly NotificationService _notifier;
    private readonly NotifyIcon _trayIcon;
    private readonly TrayIconRenderer _iconRenderer;
    private readonly ScanCoordinator _scanner;

    private readonly ToolStripMenuItem _scanMenuItem;
    private readonly ToolStripMenuItem _lowMenu;
    private readonly ToolStripMenuItem _highMenu;

    private bool _disposed;

    public TrayApp(
        ThresholdSettings settings,
        BluetoothBatteryMonitor monitor,
        NotificationService notifier)
    {
        var uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayApp must be created on the UI thread.");

        _settings = settings;
        _monitor = monitor;
        _notifier = notifier;
        _iconRenderer = new TrayIconRenderer();
        _scanner = new ScanCoordinator(monitor, settings, uiContext);

        _trayIcon = new NotifyIcon { Visible = true };

        var menuBuilder = new TrayMenuBuilder(settings);
        _lowMenu = menuBuilder.BuildLowMenu();
        _highMenu = menuBuilder.BuildHighMenu();

        _scanMenuItem = new ToolStripMenuItem("Scan devices…");
        _scanMenuItem.Click += (_, _) => _scanner.OpenScanWindowAndTriggerScan();

        var devicesMenu = menuBuilder.BuildDevicesMenu(() => monitor.LastKnownDevices);

        _trayIcon.ContextMenuStrip = menuBuilder.Build(
            devicesMenu,
            _scanMenuItem,
            _lowMenu,
            _highMenu,
            onExit: () => _ = ExitAsync());

        _trayIcon.DoubleClick += (_, _) => _scanner.OpenScanWindowAndTriggerScan();

        _scanner.ScanStarted += OnScanStarted;
        _scanner.ScanCompleted += OnScanCompleted;
        _scanner.AlertStateChanged += hasAlert => UpdateTrayIcon(hasAlert);

        _notifier.OnNotificationClicked += () => _ = _scanner.OpenScanWindowAsync();

        _settings.Changed += UpdateTooltip;
        UpdateTooltip();
        UpdateTrayIcon(false);
    }

    public void Run() => Application.Run();

    public void StartBackgroundScan() => _scanner.StartBackgroundScan();

    private void UpdateTrayIcon(bool hasAlert)
    {
        Icon? oldIcon = _trayIcon.Icon;
        _trayIcon.Icon = _iconRenderer.Render(hasAlert);

        if (oldIcon != null)
        {
            NativeMethods.DestroyIcon(oldIcon.Handle);
            oldIcon.Dispose();
        }
    }

    private void UpdateTooltip() =>
        _trayIcon.Text = $"BT Battery Alert  ▼{_settings.Low}%  ▲{_settings.High}%";

    private void OnScanStarted()
    {
        _scanMenuItem.Text = "⏳ Scanning…";
        _scanMenuItem.Enabled = false;
    }

    private void OnScanCompleted(IReadOnlyList<DeviceBatteryInfo> _)
    {
        _scanMenuItem.Text = "Scan devices…";
        _scanMenuItem.Enabled = true;
    }

    private async Task ExitAsync()
    {
        _scanMenuItem.Enabled = false;
        _lowMenu.Enabled = false;
        _highMenu.Enabled = false;

        try
        {
            Debug.WriteLine("[TrayApp] Exit started.");
            _trayIcon.Visible = false;
            await _monitor.DisposeAsync().ConfigureAwait(true);
            Debug.WriteLine("[TrayApp] Monitor disposed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayApp] Exit fault: {ex}");
        }
        finally
        {
            Application.Exit();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Debug.WriteLine("[TrayApp] Disposing.");

        _scanner.ScanStarted -= OnScanStarted;
        _scanner.ScanCompleted -= OnScanCompleted;
        _scanner.AlertStateChanged -= UpdateTrayIcon;
        _settings.Changed -= UpdateTooltip;

        _scanner.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        GC.SuppressFinalize(this);
    }
}
