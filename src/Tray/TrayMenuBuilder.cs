namespace BTChargeTrayWatcher;

internal sealed class TrayMenuBuilder
{
    private readonly ThresholdSettings _settings;
    private static readonly int[] LowThresholdCandidates  = { 10, 15, 20, 25, 30 };
    private static readonly int[] HighThresholdCandidates = { 70, 75, 80, 85, 90 };

    public TrayMenuBuilder(ThresholdSettings settings)
    {
        _settings = settings;
    }

    public static ContextMenuStrip Build(
        ThresholdSettings settings,
        ToolStripMenuItem laptopMenuItem,
        Func<IReadOnlyList<DeviceBatteryInfo>> getDevices,
        ToolStripMenuItem scanMenuItem,
        ToolStripMenuItem lowMenu,
        ToolStripMenuItem highMenu,
        Action onExit,
        Action? onOptions = null)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(laptopMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        // Flat device list: name, battery, trend/charging
        try
        {
            var devices = getDevices();
            foreach (var dev in devices)
            {
                string name = settings.GetDisplayName(dev.DeviceId, dev.Name);
                string battery = dev.Battery.HasValue ? BatteryDisplay.FormatBattery(dev.Battery.Value, dev.IsCharging) : "N/A";
                string trend = string.Empty;
                if (dev.Battery.HasValue)
                {
                    trend = BatteryTrendHelper.GetArrow(null, dev.Battery.Value); // TODO: pass previous value if available
                }
                int poll = settings.GetPollIntervalForDevice(dev.DeviceId, dev.Name) ?? (int)PollingDefaults.PollingInterval.TotalSeconds;
                string pollText = $"({poll}s)";
                string text = trend.Length > 0 ? $"{name}  {battery} {trend} {pollText}" : $"{name}  {battery} {pollText}";
                var item = new ToolStripMenuItem(text) { Enabled = false };
                menu.Items.Add(item);
            }
        }
        catch { menu.Items.Add("(device error)"); }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(scanMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        var optionsMenuItem = new ToolStripMenuItem("Options…");
        optionsMenuItem.Click += (_, _) =>
        {
            if (onOptions != null)
                onOptions();
            else
                BTChargeTrayWatcher.Tray.OptionsFormManager.ShowOptionsForm();
        };
        menu.Items.Add(optionsMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        // Removed: Low threshold, High threshold, and Run on startup (now in Options dialog)
        menu.Items.Add("Exit", null, (_, _) => onExit());
        return menu;
    }

    // Minimal stub implementations to restore build
    public ToolStripMenuItem BuildLowMenu()
    {
        return new ToolStripMenuItem("Low threshold");
    }

    public ToolStripMenuItem BuildHighMenu()
    {
        return new ToolStripMenuItem("High threshold");
    }

    public ToolStripMenuItem BuildLaptopMenuItem()
    {
        return new ToolStripMenuItem("Laptop battery");
    }

    public ToolStripMenuItem BuildDevicesMenu(Func<IReadOnlyList<object>> getDevices)
    {
        var menu = new ToolStripMenuItem("Devices");
        try
        {
            var devices = getDevices();
            foreach (var dev in devices)
            {
                string name = dev?.ToString() ?? "(unknown)";
                var item = new ToolStripMenuItem(name) { Enabled = false };
                menu.DropDownItems.Add(item);
            }
        }
        catch { menu.DropDownItems.Add("(error)"); }
        return menu;
    }
}
