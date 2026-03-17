using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public class TrayApp : IDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly BluetoothBatteryMonitor _monitor;
    private readonly NotificationService _notifier;
    private readonly DeviceDumper _dumper = new();
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _lowMenu;
    private readonly ToolStripMenuItem _highMenu;
    private readonly ToolStripMenuItem _devicesMenu;
    private ScanWindow? _scanWindow;

    public TrayApp(ThresholdSettings settings, BluetoothBatteryMonitor monitor, NotificationService notifier)
    {
        _settings = settings;
        _monitor = monitor;
        _notifier = notifier;

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true
        };

        _notifier.TrayIcon = _trayIcon;

        _lowMenu = BuildLowMenu();
        _highMenu = BuildHighMenu();
        _devicesMenu = BuildDevicesMenu();

        _trayIcon.ContextMenuStrip = BuildContextMenu();
        _trayIcon.DoubleClick += (_, _) => OpenScanWindow();

        // Keep devices menu in sync whenever a scan completes
        _monitor.ScanCompleted += results =>
            PopulateDevicesMenu(_devicesMenu, results);

        _settings.Changed += UpdateTooltip;
        UpdateTooltip();
    }

    public void Run() => Application.Run();

    private ToolStripMenuItem BuildLowMenu()
    {
        var item = new ToolStripMenuItem($"Low threshold: {_settings.Low}%");
        foreach (int v in new[] { 10, 15, 20, 25, 30 })
        {
            int val = v;
            item.DropDownItems.Add($"{val}%", null, (_, _) =>
            {
                _settings.SetLow(val);
                item.Text = $"Low threshold: {val}%";
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
                _settings.SetHigh(val);
                item.Text = $"High threshold: {val}%";
            });
        }
        return item;
    }

    private ToolStripMenuItem BuildDevicesMenu()
    {
        var item = new ToolStripMenuItem("Connected devices");

        // On open: show cache immediately or trigger scan
        item.DropDownOpening += (_, _) =>
        {
            if (_monitor.HasCachedResults)
            {
                PopulateDevicesMenu(item, _monitor.LastKnownDevices);
            }
            else
            {
                item.DropDownItems.Clear();
                item.DropDownItems.Add(
                    new ToolStripMenuItem("No scan yet — opening scan window…")
                    { Enabled = false });

                OpenScanWindow();
            }
        };

        return item;
    }

    private void PopulateDevicesMenu(
        ToolStripMenuItem parent,
        IReadOnlyList<(string Name, int Battery)> devices)
    {
        // Must run on UI thread — invoked from monitor event or directly
        if (_trayIcon.ContextMenuStrip?.InvokeRequired == true)
        {
            _trayIcon.ContextMenuStrip.Invoke(
                () => PopulateDevicesMenu(parent, devices));
            return;
        }

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

            parent.DropDownItems.Add(new ToolStripMenuItem(label) { Enabled = false });
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
        menu.Items.Add("Scan devices…", null, (_, _) => OpenScanWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Dump device properties…", null, async (_, _) =>
        {
            await _dumper.DumpToDesktopAsync();
            MessageBox.Show(
                "Dump written to Desktop\\BTBatteryDump.txt",
                "BT Battery",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _trayIcon.Visible = false;
            Application.Exit();
        });
        return menu;
    }

    public void OpenScanWindow()
    {
        if (_scanWindow is not null && !_scanWindow.IsDisposed)
        {
            _scanWindow.BringToFront();
            return;
        }

        _scanWindow = new ScanWindow();
        _scanWindow.Show();

        void OnDeviceFound(string name, int battery) =>
            _scanWindow.OnDeviceFound(name, battery);

        _monitor.DeviceFound += OnDeviceFound;

        _ = Task.Run(async () =>
        {
            try
            {
                var results = await _monitor.ScanNowAsync();
                _scanWindow.OnScanComplete(results.Count);
            }
            finally
            {
                _monitor.DeviceFound -= OnDeviceFound;
            }
        });
    }

    private void UpdateTooltip()
    {
        _trayIcon.Text = $"BT Battery Alert  ▼{_settings.Low}%  ▲{_settings.High}%";
    }

    public void Dispose()
    {
        _trayIcon.Dispose();
        _monitor.Dispose();
    }
}
