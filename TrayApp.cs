using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public class TrayApp : IDisposable
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
        _ = RunStartupScanAsync();

    private async Task RunStartupScanAsync()
    {
        if (_disposed || _exitStarted)
            return;

        try
        {
            Debug.WriteLine("[TrayApp] Startup scan started.");
            var results = await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
            Debug.WriteLine($"[TrayApp] Startup scan completed. Devices found: {results.Count}.");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[TrayApp] Startup scan cancelled.");
        }
        catch (ObjectDisposedException)
        {
            Debug.WriteLine("[TrayApp] Startup scan aborted because monitor was disposed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayApp] Startup scan fault: {ex}");
        }
    }

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

    private void Monitor_ScanStarted() =>
        PostToUi(OnScanStarted);

    private void Monitor_ScanCompleted(IReadOnlyList<(string, int)> results) =>
        PostToUi(() =>
        {
            OnScanCompleted();
            PopulateDevicesMenu(_devicesMenu, results);
        });

    private async void ScanMenuItem_Click(object? sender, EventArgs e)
    {
        try
        {
            await OpenScanWindowAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayApp] Scan menu fault: {ex}");
        }
    }

    private async void TrayIcon_DoubleClick(object? sender, EventArgs e)
    {
        try
        {
            await OpenScanWindowAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayApp] Tray double-click fault: {ex}");
        }
    }

    private void OnScanStarted()
    {
        _devicesMenu.DropDownItems.Clear();
        _devicesMenu.DropDownItems.Add(
            new ToolStripMenuItem("⏳ Scan in progress…") { Enabled = false });

        _scanMenuItem.Text = "⏳ Scanning…";
        _scanMenuItem.Enabled = false;
        _lowMenu.Enabled = false;
        _highMenu.Enabled = false;
    }

    private void OnScanCompleted()
    {
        _scanMenuItem.Text = "Scan devices…";
        _scanMenuItem.Enabled = true;
        _lowMenu.Enabled = true;
        _highMenu.Enabled = true;
    }

    public Task OpenScanWindowAsync()
    {
        if (_disposed || _exitStarted)
            return Task.CompletedTask;

        return OpenScanWindowCoreAsync();
    }

    private async Task OpenScanWindowCoreAsync()
    {
        PostToUi(() =>
        {
            if (_scanWindow is not null && !_scanWindow.IsDisposed)
            {
                _scanWindow.BringToFront();
                _scanWindow.Activate();
                return;
            }

            var window = new ScanWindow();
            window.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_scanWindow, window))
                    _scanWindow = null;
            };

            _scanWindow = window;
            window.Show();
        });

        ScanWindow? win = _scanWindow;
        if (win is null || win.IsDisposed)
            return;

        void OnFound(string name, int battery)
        {
            PostToUi(() =>
            {
                if (!win.IsDisposed)
                    win.OnDeviceFound(name, battery);
            });
        }

        void OnCompleted(IReadOnlyList<(string, int)> results)
        {
            PostToUi(() =>
            {
                if (!win.IsDisposed)
                    win.OnScanComplete(results.Count);
            });
        }

        _monitor.DeviceFound += OnFound;
        _monitor.ScanCompleted += OnCompleted;

        try
        {
            if (_monitor.IsScanning)
            {
                Debug.WriteLine("[TrayApp] Scan window opened while scan already in progress.");
                return;
            }

            Debug.WriteLine("[TrayApp] Manual scan started.");
            var results = await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
            Debug.WriteLine($"[TrayApp] Manual scan completed. Devices found: {results.Count}.");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[TrayApp] Manual scan cancelled.");
        }
        catch (ObjectDisposedException)
        {
            Debug.WriteLine("[TrayApp] Manual scan aborted because monitor was disposed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayApp] Manual scan fault: {ex}");
            throw;
        }
        finally
        {
            _monitor.DeviceFound -= OnFound;
            _monitor.ScanCompleted -= OnCompleted;
        }
    }

    private ToolStripMenuItem BuildLowMenu()
    {
        var item = new ToolStripMenuItem($"Low threshold: {_settings.Low}%");

        foreach (int v in new[] { 10, 15, 20, 25, 30 })
        {
            int val = v;
            item.DropDownItems.Add($"{val}%", null, (_, _) =>
            {
                try
                {
                    _settings.SetLow(val);
                    item.Text = $"Low threshold: {val}%";
                    Debug.WriteLine($"[TrayApp] Low threshold set to {val}%.");
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    Debug.WriteLine($"[TrayApp] Invalid low threshold: {ex.Message}");
                    MessageBox.Show(
                        ex.Message,
                        "Invalid Threshold",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            });
        }

        return item;
    }

    private ToolStripMenuItem BuildHighMenu()
    {
        var item = new ToolStripMenuItem($"High threshold: {_settings.High}%");

        foreach (int v in new[] { 70, 75, 80, 85, 90 })
        {
            int val = v;
            item.DropDownItems.Add($"{val}%", null, (_, _) =>
            {
                try
                {
                    _settings.SetHigh(val);
                    item.Text = $"High threshold: {val}%";
                    Debug.WriteLine($"[TrayApp] High threshold set to {val}%.");
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    Debug.WriteLine($"[TrayApp] Invalid high threshold: {ex.Message}");
                    MessageBox.Show(
                        ex.Message,
                        "Invalid Threshold",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            });
        }

        return item;
    }

    private ToolStripMenuItem BuildDevicesMenu()
    {
        var item = new ToolStripMenuItem("Connected devices");
        item.DropDownOpening += (_, _) =>
        {
            if (_disposed || _exitStarted)
                return;

            if (_monitor.IsScanning)
                return;

            if (_monitor.HasCachedResults)
            {
                PopulateDevicesMenu(item, _monitor.LastKnownDevices);
            }
            else
            {
                item.DropDownItems.Clear();
                item.DropDownItems.Add(
                    new ToolStripMenuItem("No data yet — use Scan devices…")
                    {
                        Enabled = false
                    });
            }
        };

        return item;
    }

    private static void PopulateDevicesMenu(
        ToolStripMenuItem parent,
        IReadOnlyList<(string Name, int Battery)> devices)
    {
        parent.DropDownItems.Clear();

        if (devices.Count == 0)
        {
            parent.DropDownItems.Add(
                new ToolStripMenuItem("No devices found") { Enabled = false });
            return;
        }

        foreach (var (name, battery) in devices)
        {
            string label = battery >= 0
                ? $"{name}   {battery}%  {BluetoothBatteryMonitor.BatteryBar(battery)}"
                : $"{name}   battery n/a";

            parent.DropDownItems.Add(
                new ToolStripMenuItem(label) { Enabled = false });
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(_devicesMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_lowMenu);
        menu.Items.Add(_highMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_scanMenuItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Dump device properties…", null, async (_, _) =>
        {
            try
            {
                Debug.WriteLine("[TrayApp] Device dump started.");
                await _dumper.DumpToDesktopAsync().ConfigureAwait(true);
                Debug.WriteLine("[TrayApp] Device dump completed.");

                MessageBox.Show(
                    "Dump written to Desktop\\BTBatteryDump.txt",
                    "BT Battery",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayApp] Device dump fault: {ex}");
                MessageBox.Show(
                    $"Dump failed: {ex.Message}",
                    "BT Battery",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) =>
        {
            try
            {
                await ExitAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayApp] Exit handler fault: {ex}");
            }
        });

        return menu;
    }

    private void UpdateTooltip()
    {
        if (_disposed)
            return;

        _trayIcon.Text = $"BT Battery Alert  ▼{_settings.Low}%  ▲{_settings.High}%";
    }

    private async Task ExitAsync()
    {
        if (_exitStarted)
            return;

        _exitStarted = true;

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
        if (_disposed)
            return;

        _disposed = true;

        Debug.WriteLine("[TrayApp] Disposing tray app.");

        _monitor.ScanStarted -= Monitor_ScanStarted;
        _monitor.ScanCompleted -= Monitor_ScanCompleted;
        _settings.Changed -= UpdateTooltip;

        _scanMenuItem.Click -= ScanMenuItem_Click;
        _trayIcon.DoubleClick -= TrayIcon_DoubleClick;

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        if (_scanWindow is not null && !_scanWindow.IsDisposed)
            _scanWindow.Dispose();

        GC.SuppressFinalize(this);
    }
}
