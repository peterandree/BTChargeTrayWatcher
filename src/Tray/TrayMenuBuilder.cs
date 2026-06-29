namespace BTChargeTrayWatcher;

/// <summary>
/// Pre-constructed <see cref="ToolStripMenuItem"/> instances required by
/// <see cref="TrayMenuBuilder.Build"/>. Extracted as a parameter object to
/// eliminate the 4 same-typed positional parameters (closes #121).
/// </summary>
internal sealed record TrayMenuItems(
    ToolStripMenuItem LaptopMenuItem,
    ToolStripMenuItem ScanMenuItem,
    ToolStripMenuItem LowMenu,
    ToolStripMenuItem HighMenu);

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
        TrayMenuItems items,
        Func<IReadOnlyList<DeviceBatteryInfo>> getDevices,
        Action onExit,
        Action? onOptions = null,
        Action? onStartupDiagnostics = null)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(items.LaptopMenuItem);
        var startDevicesSeparator = new ToolStripSeparator();
        menu.Items.Add(startDevicesSeparator);

        var endDevicesSeparator = new ToolStripSeparator();
        menu.Items.Add(endDevicesSeparator);

        menu.Opening += (_, _) =>
        {
            try
            {
                int startIndex = menu.Items.IndexOf(startDevicesSeparator);
                int endIndex   = menu.Items.IndexOf(endDevicesSeparator);
                while (startIndex + 1 < endIndex)
                {
                    menu.Items.RemoveAt(startIndex + 1);
                    endIndex--;
                }

                var devices = getDevices();
                int insertIndex = menu.Items.IndexOf(endDevicesSeparator);
                foreach (var dev in devices)
                {
                    string name    = settings.GetDisplayName(dev.DeviceId, dev.Name);
                    string battery = dev.Battery.HasValue
                        ? BatteryDisplay.FormatBattery(dev.Battery.Value, dev.IsCharging)
                        : "N/A";
                    string trend = dev.Battery.HasValue
                        ? BatteryTrendHelper.GetArrow(null, dev.Battery.Value)
                        : string.Empty;
                    string text = trend.Length > 0 ? $"{name}  {battery} {trend}" : $"{name}  {battery}";
                    menu.Items.Insert(insertIndex++, new ToolStripMenuItem(text) { Enabled = false });
                }
            }
            catch
            {
                int insertIndex = menu.Items.IndexOf(endDevicesSeparator);
                menu.Items.Insert(insertIndex, new ToolStripMenuItem("(device error)") { Enabled = false });
            }
        };

        menu.Items.Add(items.ScanMenuItem);
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
        if (onStartupDiagnostics is not null)
        {
            var startupDiagnosticsItem = new ToolStripMenuItem("Startup diagnostics…");
            startupDiagnosticsItem.Click += (_, _) => onStartupDiagnostics();
            menu.Items.Add(startupDiagnosticsItem);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => onExit());
        return menu;
    }

    public ToolStripMenuItem BuildLowMenu()    => new("Low threshold");
    public ToolStripMenuItem BuildHighMenu()   => new("High threshold");
    public ToolStripMenuItem BuildLaptopMenuItem() => new("Laptop battery");

    public ToolStripMenuItem BuildDevicesMenu(Func<IReadOnlyList<object>> getDevices)
    {
        var menu = new ToolStripMenuItem("Devices");
        try
        {
            foreach (var dev in getDevices())
                menu.DropDownItems.Add(new ToolStripMenuItem(dev?.ToString() ?? "(unknown)") { Enabled = false });
        }
        catch { menu.DropDownItems.Add("(error)"); }
        return menu;
    }
}
