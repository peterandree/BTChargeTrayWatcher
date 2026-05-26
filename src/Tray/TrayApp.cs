// src/Tray/TrayApp.cs  — thin orchestration shell; tooltip/alert logic in TrayViewModel.
using System.Diagnostics;
using BTChargeTrayWatcher.Tray.ViewModels;

namespace BTChargeTrayWatcher;

public sealed class TrayApp : IDisposable
{
    private readonly ThresholdSettings       _settings;
    private readonly BluetoothBatteryMonitor _monitor;
    private readonly LaptopBatteryMonitor    _laptopMonitor;
    private readonly Action                  _showOptions;
    private readonly Action                  _unsubscribeNotificationClicked;
    private readonly NotifyIcon              _trayIcon;
    private readonly TrayIconRenderer        _iconRenderer;
    private readonly ScanCoordinator         _scanner;
    private readonly SynchronizationContext  _uiContext;
    private readonly AliasSuggestionService  _aliasSuggestionService;
    private readonly TrayViewModel           _trayVm;

    private readonly ToolStripMenuItem _scanMenuItem;
    private readonly ToolStripMenuItem _lowMenu;
    private readonly ToolStripMenuItem _highMenu;
    private readonly ToolStripMenuItem _laptopMenuItem;

    private bool _disposed;
    private System.ComponentModel.CancelEventHandler? _contextMenuOpeningHandler;

    /// <param name="settings">Application threshold settings.</param>
    /// <param name="monitor">Bluetooth battery monitor.</param>
    /// <param name="laptopMonitor">Laptop battery monitor.</param>
    /// <param name="aliasSuggestionService">Alias suggestion service.</param>
    /// <param name="showOptions">Action that opens the Options form.</param>
    /// <param name="subscribeNotificationClicked">
    /// Receives the scan-window-open callback; returns an unsubscribe action.
    /// </param>
    internal TrayApp(
        ThresholdSettings       settings,
        BluetoothBatteryMonitor monitor,
        LaptopBatteryMonitor    laptopMonitor,
        AliasSuggestionService  aliasSuggestionService,
        Action                  showOptions,
        Func<Action, Action>    subscribeNotificationClicked)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayApp must be created on the UI thread.");
        _aliasSuggestionService = aliasSuggestionService;
        _settings      = settings;
        _monitor       = monitor;
        _laptopMonitor = laptopMonitor;
        _showOptions   = showOptions;
        _iconRenderer  = new TrayIconRenderer();
        _scanner       = new ScanCoordinator(monitor, settings, _uiContext);

        _trayVm = new TrayViewModel(settings, monitor, laptopMonitor);
        _trayVm.AlertChanged += hasAlert => UpdateTrayIcon(hasAlert);

        _trayIcon = new NotifyIcon { Visible = true };

        var menuBuilder = new TrayMenuBuilder(settings);
        _lowMenu        = menuBuilder.BuildLowMenu();
        _highMenu       = menuBuilder.BuildHighMenu();
        _laptopMenuItem = menuBuilder.BuildLaptopMenuItem();

        _scanMenuItem       = new ToolStripMenuItem("Scan devices\u2026");
        _scanMenuItem.Click += (_, _) => _scanner.OpenScanWindowAndTriggerScan();

        var contextMenu = TrayMenuBuilder.Build(
            _settings,
            _laptopMenuItem,
            () => monitor.LastKnownDevices,
            _scanMenuItem,
            _lowMenu,
            _highMenu,
            onExit:    () => _ = ExitAsync(),
            onOptions: _showOptions);

        _contextMenuOpeningHandler = (s, e) => { if (!_disposed) UpdateTooltip(); };
        contextMenu.Opening += _contextMenuOpeningHandler;
        _trayIcon.ContextMenuStrip = contextMenu;

        _trayIcon.MouseClick  += OnTrayMouseClick;
        _trayIcon.DoubleClick += (_, _) => _scanner.OpenScanWindowAndTriggerScan();

        _scanner.ScanStarted       += OnScanStarted;
        _scanner.ScanCompleted     += OnScanCompleted;
        _scanner.AlertStateChanged += alert => _trayVm.ApplyBluetoothAlert(alert);
        _scanner.ScanFaulted       += OnScanFaulted;

        _monitor.BackgroundRefreshCompleted += OnDevicesRefreshed;
        _monitor.ManualScanCompleted        += OnDevicesRefreshed;

        _laptopMonitor.BatteryUpdated += OnLaptopBatteryUpdated;

        _unsubscribeNotificationClicked = subscribeNotificationClicked(_scanner.RequestOpenScanWindow);
        _aliasSuggestionService.SuggestionQueued += OnAliasSuggestionQueued;

        InitializeLaptopUiFromCachedState();
        _settings.Changed += OnSettingsChanged;
        UpdateTooltip();
        RefreshTrayIcon();
    }

    private void OnAliasSuggestionQueued(AliasSuggestion suggestion)
    {
        _uiContext.Post(_ =>
        {
            if (_disposed) return;
            try
            {
                string message = $"Suggested alias for '{suggestion.DeviceName}'\n"
                               + $"Match: '{suggestion.MatchedAliasKey}' (score {suggestion.Score:P0})\n\n"
                               + "Yes = Apply alias, No = Ignore, Cancel = Suppress suggestions for this device.";
                var res = MessageBox.Show(message, "Alias suggestion",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (res == DialogResult.Yes)
                    _settings.AddAlias(suggestion.DeviceName, suggestion.CanonicalDeviceId);
                else if (res == DialogResult.Cancel)
                    _settings.SuppressAliasSuggestion(suggestion.DeviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayApp] Alias suggestion UI fault: {ex}");
            }
        }, null);
    }

    private void InitializeLaptopUiFromCachedState()
    {
        if (_laptopMonitor.LastKnownBattery is { } info)
        {
            UpdateLaptopMenuItem(info);
            _trayVm.ApplyLaptopBattery(info);
        }
    }

    public static void Run() => Application.Run();

    public void StartBackgroundScan() => _scanner.StartBackgroundScan();

    private void UpdateTrayIcon(bool hasAlert)
    {
        Icon? newIcon = _iconRenderer.Render(hasAlert);
        Icon? oldIcon = _trayIcon.Icon;
        _trayIcon.Icon = newIcon;
        oldIcon?.Dispose();
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            _trayIcon.ContextMenuStrip?.Show(Cursor.Position);
    }

    private void OnSettingsChanged()
    {
        _trayVm.ApplyLaptopBattery();
        UpdateTooltip();
    }

    private void RefreshTrayIcon() => UpdateTrayIcon(_trayVm.HasAlert);

    private void OnDevicesRefreshed(IReadOnlyList<DeviceBatteryInfo> _) =>
        _uiContext.Post(_ => { if (!_disposed) UpdateTooltip(); }, null);

    private void UpdateTooltip() => _trayIcon.Text = _trayVm.BuildTooltip();

    private void OnLaptopBatteryUpdated(LaptopBatteryInfo info)
    {
        _uiContext.Post(_ =>
        {
            if (_disposed) return;
            try
            {
                UpdateLaptopMenuItem(info);
                UpdateTooltip();
                _trayVm.ApplyLaptopBattery(info);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayApp] Laptop battery UI update fault: {ex}");
            }
        }, null);
    }

    private void UpdateLaptopMenuItem(LaptopBatteryInfo info) =>
        _laptopMenuItem.Text = _trayVm.BuildLaptopMenuText(info);

    private void OnScanStarted()
    {
        _scanMenuItem.Text    = "\u23f3 Scanning\u2026";
        _scanMenuItem.Enabled = false;
    }

    private void OnScanCompleted(IReadOnlyList<DeviceBatteryInfo> devices)
    {
        _scanMenuItem.Text    = "Scan devices\u2026";
        _scanMenuItem.Enabled = true;
        UpdateTooltip();
    }

    private static void OnScanFaulted(string operationName, Exception ex) =>
        Trace.TraceError($"[TrayApp] Background operation '{operationName}' faulted: {ex}");

    private async Task ExitAsync()
    {
        _scanMenuItem.Enabled = false;
        _lowMenu.Enabled      = false;
        _highMenu.Enabled     = false;
        try
        {
            Debug.WriteLine("[TrayApp] Exit started.");
            _trayIcon.Visible = false;
            await _monitor.DisposeAsync().ConfigureAwait(true);
            Debug.WriteLine("[TrayApp] Bluetooth monitor disposed.");
            await _laptopMonitor.DisposeAsync().ConfigureAwait(true);
            Debug.WriteLine("[TrayApp] Laptop monitor disposed.");
        }
        catch (Exception ex) { Debug.WriteLine($"[TrayApp] Exit fault: {ex}"); }
        finally { Application.Exit(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Debug.WriteLine("[TrayApp] Disposing.");

        _scanner.ScanStarted       -= OnScanStarted;
        _scanner.ScanCompleted     -= OnScanCompleted;
        _scanner.ScanFaulted       -= OnScanFaulted;
        _monitor.BackgroundRefreshCompleted -= OnDevicesRefreshed;
        _monitor.ManualScanCompleted        -= OnDevicesRefreshed;
        _laptopMonitor.BatteryUpdated -= OnLaptopBatteryUpdated;
        _settings.Changed             -= OnSettingsChanged;
        _unsubscribeNotificationClicked();
        _aliasSuggestionService.SuggestionQueued -= OnAliasSuggestionQueued;

        _scanner.Dispose();

        if (_trayIcon.ContextMenuStrip is not null && _contextMenuOpeningHandler is not null)
            _trayIcon.ContextMenuStrip.Opening -= _contextMenuOpeningHandler;

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _iconRenderer.Dispose();
        GC.SuppressFinalize(this);
    }
}
