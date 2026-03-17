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
    private readonly ToolStripMenuItem _scanMenuItem;
    private readonly SynchronizationContext _uiContext;
    private ScanWindow? _scanWindow;

    public TrayApp(ThresholdSettings settings, BluetoothBatteryMonitor monitor, NotificationService notifier)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayApp must be created on the UI thread.");

        _settings = settings;
        _monitor = monitor;
        _notifier = notifier;

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true
        };

        _lowMenu = BuildLowMenu();
        _highMenu = BuildHighMenu();
        _devicesMenu = BuildDevicesMenu();
        _scanMenuItem = new ToolStripMenuItem("Scan devices…");
        _scanMenuItem.Click += (_, _) => OpenScanWindow();

        _trayIcon.ContextMenuStrip = BuildContextMenu();
        _trayIcon.DoubleClick += (_, _) => OpenScanWindow();

        _monitor.ScanStarted += () => PostToUi(OnScanStarted);
        _monitor.ScanCompleted += results => PostToUi(() =>
        {
            OnScanCompleted();
            PopulateDevicesMenu(_devicesMenu, results);
        });

        _settings.Changed += UpdateTooltip;
        UpdateTooltip();
    }

    public void Run() => Application.Run();

    private void PostToUi(Action action) =>
        _uiContext.Post(_ => action(), null);

    // Called on UI thread when scan starts
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

    // Called on UI thread when scan ends
    private void OnScanCompleted()
    {
        _scanMenuItem.Text = "Scan devices…";
        _scanMenuItem.Enabled = true;
        _lowMenu.Enabled = true;
        _highMenu.Enabled = true;
    }

    public void StartBackgroundScan()
    {
        _ = Task.Run(async () =>
        {
            void OnDeviceFound(string name, int battery) =>
                _notifier.NotifyDeviceFound(name, battery);

            _monitor.DeviceFound += OnDeviceFound;
            try
            {
                await _monitor.ScanNowAsync();
            }
            finally
            {
                _monitor.DeviceFound -= OnDeviceFound;
            }
        });
    }

    public void OpenScanWindow()
    {
        if (_monitor.IsScanning)
        {
            // Scan already running — just open window to show live progress
            if (_scanWindow is null || _scanWindow.IsDisposed)
            {
                _scanWindow = new ScanWindow();
                _scanWindow.Show();

                void OnDeviceFound(string name, int battery) =>
                    PostToUi(() => _scanWindow.OnDeviceFound(name, battery));

                void OnCompleted(IReadOnlyList<(string, int)> results) =>
                    PostToUi(() => _scanWindow.OnScanComplete(results.Count));

                _monitor.DeviceFound += OnDeviceFound;
                _monitor.ScanCompleted += OnCompleted;

                // Detach when scan completes
                _monitor.ScanCompleted += _ =>
                {
                    _monitor.DeviceFound -= OnDeviceFound;
                    _monitor.ScanCompleted -= OnCompleted;
                };
            }
            else
            {
                _scanWindow.BringToFront();
            }
            return;
        }

        // No scan running — open window and start fresh scan
        if (_scanWindow is not null && !_scanWindow.IsDisposed)
        {
            _scanWindow.BringToFront();
            return;
        }

        _scanWindow = new ScanWindow();
        _scanWindow.Show();

        void OnFound(string name, int battery) =>
            PostToUi(() => _scanWindow.OnDeviceFound(name, battery));

        _monitor.DeviceFound += OnFound;

        _ = Task.Run(async () =>
        {
            try
            {
                var results = await _monitor.ScanNowAsync();
                PostToUi(() => _scanWindow.OnScanComplete(results.Count));
            }
            finally
            {
                _monitor.DeviceFound -= OnFound;
            }
        });
    }

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
        item.DropDownOpening += (_, _) =>
        {
            if (_monitor.IsScanning) return;  // Already showing scan-in-progress state

            if (_monitor.HasCachedResults)
                PopulateDevicesMenu(item, _monitor.LastKnownDevices);
            else
            {
                item.DropDownItems.Clear();
                item.DropDownItems.Add(
                    new ToolStripMenuItem("No data yet — use Scan devices…")
                    { Enabled = false });
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
        menu.Items.Add(_scanMenuItem);
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
