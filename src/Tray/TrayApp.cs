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
    private readonly LaptopBatteryMonitor _laptopMonitor;
    private readonly NotificationService _notifier;
    private readonly NotifyIcon _trayIcon;
    private readonly TrayIconRenderer _iconRenderer;
    private readonly ScanCoordinator _scanner;
    private readonly SynchronizationContext _uiContext;

    private readonly ToolStripMenuItem _scanMenuItem;
    private readonly ToolStripMenuItem _lowMenu;
    private readonly ToolStripMenuItem _highMenu;
    private readonly ToolStripMenuItem _laptopMenuItem;

    private bool _disposed;
    private bool _hasBluetoothAlert;
    private bool _hasLaptopAlert;

    public TrayApp(
        ThresholdSettings settings,
        BluetoothBatteryMonitor monitor,
        NotificationService notifier,
        LaptopBatteryMonitor laptopMonitor)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayApp must be created on the UI thread.");

        _settings = settings;
        _monitor = monitor;
        _laptopMonitor = laptopMonitor;
        _notifier = notifier;
        _iconRenderer = new TrayIconRenderer();
        _scanner = new ScanCoordinator(monitor, settings, _uiContext);

        _trayIcon = new NotifyIcon { Visible = true };

        var menuBuilder = new TrayMenuBuilder(settings);
        _lowMenu = menuBuilder.BuildLowMenu();
        _highMenu = menuBuilder.BuildHighMenu();
        _laptopMenuItem = menuBuilder.BuildLaptopMenuItem();

        _scanMenuItem = new ToolStripMenuItem("Scan devices…");
        _scanMenuItem.Click += (_, _) => _scanner.OpenScanWindowAndTriggerScan();

        var devicesMenu = menuBuilder.BuildDevicesMenu(() => monitor.LastKnownDevices);

        _trayIcon.ContextMenuStrip = menuBuilder.Build(
            _laptopMenuItem,
            devicesMenu,
            _scanMenuItem,
            _lowMenu,
            _highMenu,
            onExit: () => _ = ExitAsync());

        _trayIcon.DoubleClick += (_, _) => _scanner.OpenScanWindowAndTriggerScan();

        _scanner.ScanStarted += OnScanStarted;
        _scanner.ScanCompleted += OnScanCompleted;
        _scanner.AlertStateChanged += OnBluetoothAlertStateChanged;
        _scanner.ScanFaulted += OnScanFaulted;

        _laptopMonitor.BatteryUpdated += OnLaptopBatteryUpdated;
        _laptopMonitor.AlertStateChanged += OnLaptopAlertStateChanged;

        _notifier.OnNotificationClicked += _scanner.RequestOpenScanWindow;

        InitializeLaptopUiFromCachedState();
        _settings.Changed += UpdateTooltip;
        UpdateTooltip();
        RefreshTrayIcon();
    }

    private void InitializeLaptopUiFromCachedState()
    {
        if (_laptopMonitor.LastKnownBattery is { } info)
            UpdateLaptopMenuItem(info);
    }

    public void Run() => Application.Run();

    public void StartBackgroundScan() => _scanner.StartBackgroundScan();

    private void UpdateTrayIcon(bool hasAlert)
    {
        Icon? newIcon = _iconRenderer.Render(hasAlert);
        Icon? oldIcon = _trayIcon.Icon;
        _trayIcon.Icon = newIcon;
        oldIcon?.Dispose();
    }

    private void OnBluetoothAlertStateChanged(bool hasAlert)
    {
        _hasBluetoothAlert = hasAlert;
        RefreshTrayIcon();
    }

    private void OnLaptopAlertStateChanged(bool hasAlert)
    {
        _hasLaptopAlert = hasAlert;
        RefreshTrayIcon();
    }

    private void RefreshTrayIcon()
    {
        UpdateTrayIcon(_hasBluetoothAlert || _hasLaptopAlert);
    }

    private void UpdateTooltip()
    {
        string laptopPart = _laptopMonitor.LastKnownBattery is { } info && info.HasBattery
            ? $"  💻 {info.BatteryPercent}%{(info.IsCharging ? " ⚡" : "")}"
            : string.Empty;

        string text = $"BT Battery Alert ▼{_settings.Low}% ▲{_settings.High}%{laptopPart}";

        // NotifyIcon.Text is capped at 127 chars by Win32
        _trayIcon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void OnLaptopBatteryUpdated(LaptopBatteryInfo info)
    {
        _uiContext.Post(_ =>
        {
            if (_disposed) return;
            try
            {
                UpdateLaptopMenuItem(info);
                UpdateTooltip();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayApp] Laptop battery UI update fault: {ex}");
            }
        }, null);
    }

    private void UpdateLaptopMenuItem(LaptopBatteryInfo info)
    {
        if (!info.HasBattery)
        {
            _laptopMenuItem.Text = "💻 Laptop: No battery";
            return;
        }

        string charge = info.IsCharging ? " ⚡ Charging"
            : info.IsOnAcPower ? " 🔌 Plugged in"
            : " On battery";

        _laptopMenuItem.Text = $"💻 Laptop: {info.BatteryPercent}%{charge}";
    }

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

    private static void OnScanFaulted(string operationName, Exception ex)
    {
        Trace.TraceError($"[TrayApp] Background operation '{operationName}' faulted: {ex}");
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
            Debug.WriteLine("[TrayApp] Bluetooth monitor disposed.");
            await _laptopMonitor.DisposeAsync().ConfigureAwait(true);
            Debug.WriteLine("[TrayApp] Laptop monitor disposed.");
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
        _scanner.AlertStateChanged -= OnBluetoothAlertStateChanged;
        _scanner.ScanFaulted -= OnScanFaulted;
        _laptopMonitor.BatteryUpdated -= OnLaptopBatteryUpdated;
        _laptopMonitor.AlertStateChanged -= OnLaptopAlertStateChanged;
        _settings.Changed -= UpdateTooltip;
        _notifier.OnNotificationClicked -= _scanner.RequestOpenScanWindow;

        _scanner.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        GC.SuppressFinalize(this);
    }
}
