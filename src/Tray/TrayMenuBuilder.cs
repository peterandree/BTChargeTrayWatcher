using System;
using System.Collections.Generic;
using System.Windows.Forms;

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
        TrayMenuItems items,
        Func<IReadOnlyList<DeviceBatteryInfo>> getDevices,
        Action onExit,
        Action? onOptions = null)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(items.LaptopMenuItem);
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
        menu.Items.Add(items.ScanMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        var optionsMenuItem = new ToolStripMenuItem("Options…");
        optionsMenuItem.Click += (_, _) =>
        {
            if (onOptions != null)
                onOptions();
            else
                BTChargeTrayWatcher.Tray.OptionsFormManager.ShowOptionsForm(settings);
        };
        menu.Items.Add(optionsMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(items.LowMenu);
        menu.Items.Add(items.HighMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => onExit()));
        return menu;
    }

    public ToolStripMenuItem BuildLowMenu()
    {
        var lowMenu = new ToolStripMenuItem("Low threshold");
        foreach (var low in LowThresholdCandidates)
        {
            var item = new ToolStripMenuItem(low + "%") { Checked = _settings.Low == low };
            item.Click += (_, _) =>
            {
                _settings.Low = low;
                foreach (ToolStripMenuItem sibling in lowMenu.DropDownItems)
                    sibling.Checked = sibling == item;
            };
            lowMenu.DropDownItems.Add(item);
        }
        return lowMenu;
    }

    public ToolStripMenuItem BuildHighMenu()
    {
        var highMenu = new ToolStripMenuItem("High threshold");
        foreach (var high in HighThresholdCandidates)
        {
            var item = new ToolStripMenuItem(high + "%") { Checked = _settings.High == high };
            item.Click += (_, _) =>
            {
                _settings.High = high;
                foreach (ToolStripMenuItem sibling in highMenu.DropDownItems)
                    sibling.Checked = sibling == item;
            };
            highMenu.DropDownItems.Add(item);
        }
        return highMenu;
    }

    public ToolStripMenuItem BuildLaptopMenuItem() =>
        new("Laptop: N/A") { Enabled = false };
}

internal sealed record TrayMenuItems(
    ToolStripMenuItem LaptopMenuItem,
    ToolStripMenuItem ScanMenuItem,
    ToolStripMenuItem LowMenu,
    ToolStripMenuItem HighMenu);
