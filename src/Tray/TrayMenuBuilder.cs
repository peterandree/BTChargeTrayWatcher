// src/Tray/TrayMenuBuilder.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

internal sealed class TrayMenuBuilder
{
    private readonly ThresholdSettings _settings;

    // Centralized policy: low thresholds (10–30)
    private static readonly int[] LowThresholdCandidates = { 10, 15, 20, 25, 30 };

    // Centralized policy: high thresholds (70–90)
    private static readonly int[] HighThresholdCandidates = { 70, 75, 80, 85, 90 };

    public TrayMenuBuilder(ThresholdSettings settings)
    {
        _settings = settings;
    }

    public ContextMenuStrip Build(
        ToolStripMenuItem laptopMenuItem,
        ToolStripMenuItem devicesMenu,
        ToolStripMenuItem scanMenuItem,
        ToolStripMenuItem lowMenu,
        ToolStripMenuItem highMenu,
        Action onExit)
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(laptopMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(devicesMenu);
        menu.Items.Add(scanMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(lowMenu);
        menu.Items.Add(highMenu);
        menu.Items.Add(new ToolStripSeparator());

        var autostartItem = new ToolStripMenuItem("Run on startup")
        {
            Checked = _settings.RunOnStartup
        };
        autostartItem.Click += (_, _) =>
        {
            _settings.RunOnStartup = !_settings.RunOnStartup;
            autostartItem.Checked = _settings.RunOnStartup;
        };
        menu.Items.Add(autostartItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => onExit());

        return menu;
    }


    public ToolStripMenuItem BuildDevicesMenu(
        Func<IReadOnlyList<DeviceBatteryInfo>> getDevices)
    {
        var menu = new ToolStripMenuItem("Connected devices");
        menu.DropDownItems.Add(new ToolStripMenuItem("⏳ Initializing…") { Enabled = false });
        menu.DropDownOpening += (_, _) => PopulateDevicesMenu(menu, getDevices());
        return menu;
    }

    public ToolStripMenuItem BuildLowMenu() =>
        BuildGlobalThresholdMenu(
            "Global Low threshold",
            _settings.Low,
            LowThresholdCandidates,
            val => _settings.Low = val);

    public ToolStripMenuItem BuildHighMenu() =>
        BuildGlobalThresholdMenu(
            "Global High threshold",
            _settings.High,
            HighThresholdCandidates,
            val => _settings.High = val);

    public ToolStripMenuItem BuildLaptopMenuItem()
    {
        var root = new ToolStripMenuItem("💻 Laptop: reading…");

        var lowMenu = BuildGlobalThresholdMenu(
            "Low threshold",
            _settings.LaptopLow,
            LowThresholdCandidates,
            val => _settings.LaptopLow = val);

        var highMenu = BuildGlobalThresholdMenu(
            "High threshold",
            _settings.LaptopHigh,
            HighThresholdCandidates,
            val => _settings.LaptopHigh = val);

        var overlayItem = new ToolStripMenuItem(
            _settings.ExcludeLaptopFromTrayIconOverlay
                ? "Include in tray icon alert"
                : "Exclude from tray icon alert");
        overlayItem.Click += (_, _) =>
        {
            _settings.ExcludeLaptopFromTrayIconOverlay = !_settings.ExcludeLaptopFromTrayIconOverlay;
            overlayItem.Text = _settings.ExcludeLaptopFromTrayIconOverlay
                ? "Include in tray icon alert"
                : "Exclude from tray icon alert";
        };

        root.DropDownItems.Add(lowMenu);
        root.DropDownItems.Add(highMenu);
        root.DropDownItems.Add(overlayItem);

        return root;
    }

    public void PopulateDevicesMenu(
        ToolStripMenuItem parent,
        IReadOnlyList<DeviceBatteryInfo> results)
    {
        while (parent.DropDownItems.Count > 0)
        {
            var item = parent.DropDownItems[0];
            parent.DropDownItems.RemoveAt(0);
            item.Dispose();
        }

        if (results.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("No devices found") { Enabled = false });
            return;
        }

        foreach (var device in results)
        {
            bool isIgnored = _settings.IgnoredDevices.Contains(device.Name);
            bool isOverlayExcluded = _settings.TrayIconOverlayExcludedDevices.Contains(device.Name);

            string statusTag = isIgnored
                ? " [Ignored]"
                : isOverlayExcluded
                    ? " [No icon alert]"
                    : string.Empty;

            string label = device.Battery.HasValue
                ? $"{device.Name}   {device.Battery.Value}%  {BatteryDisplay.Bar(device.Battery.Value)}{statusTag}"
                : $"{device.Name}   N/A{statusTag}";

            var item = new ToolStripMenuItem(label);

            if (!isIgnored)
            {
                var lowMenu = BuildDeviceThresholdMenu(
                    "Low limit",
                    _settings.Low,
                    _settings.HasCustomLow(device.Name) ? _settings.GetLow(device.Name) : null,
                    LowThresholdCandidates,
                    val => _settings.SetLow(device.Name, val));

                var highMenu = BuildDeviceThresholdMenu(
                    "High limit",
                    _settings.High,
                    _settings.HasCustomHigh(device.Name) ? _settings.GetHigh(device.Name) : null,
                    HighThresholdCandidates,
                    val => _settings.SetHigh(device.Name, val));

                item.DropDownItems.Add(lowMenu);
                item.DropDownItems.Add(highMenu);
                item.DropDownItems.Add(new ToolStripSeparator());
            }

            var overlayItem = new ToolStripMenuItem(
                isOverlayExcluded
                    ? "Include in tray icon alert"
                    : "Exclude from tray icon alert");
            overlayItem.Click += (_, _) => _settings.ToggleExcludeFromTrayIconOverlay(device.Name);
            item.DropDownItems.Add(overlayItem);

            var ignoreItem = new ToolStripMenuItem(
                isIgnored ? "Stop ignoring device" : "Ignore device");
            ignoreItem.Click += (_, _) => _settings.ToggleIgnoreDevice(device.Name);
            item.DropDownItems.Add(ignoreItem);

            parent.DropDownItems.Add(item);
        }
    }

    private static ToolStripMenuItem BuildGlobalThresholdMenu(
        string baseText,
        int currentValue,
        int[] candidates,
        Action<int> setter)
    {
        var root = new ToolStripMenuItem($"{baseText}: {currentValue}%");

        foreach (int val in candidates)
        {
            var item = new ToolStripMenuItem($"{val}%");
            if (val == currentValue) item.Checked = true;

            item.Click += (_, _) =>
            {
                try
                {
                    setter(val);
                    root.Text = $"{baseText}: {val}%";
                    foreach (ToolStripItem child in root.DropDownItems)
                    {
                        if (child is ToolStripMenuItem mi)
                            mi.Checked = (mi.Text == $"{val}%");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TrayMenuBuilder] Global threshold update fault: {ex}");
                }
            };
            root.DropDownItems.Add(item);
        }

        return root;
    }

    private static ToolStripMenuItem BuildDeviceThresholdMenu(
        string baseText,
        int globalValue,
        int? customValue,
        int[] candidates,
        Action<int?> setter)
    {
        string displayValue = customValue.HasValue
            ? $"{customValue.Value}%"
            : $"Global ({globalValue}%)";

        var root = new ToolStripMenuItem($"{baseText}: {displayValue}");

        var globalItem = new ToolStripMenuItem($"Global ({globalValue}%)");
        globalItem.Checked = !customValue.HasValue;
        globalItem.Click += (_, _) => setter(null);
        root.DropDownItems.Add(globalItem);
        root.DropDownItems.Add(new ToolStripSeparator());

        foreach (int val in candidates)
        {
            var item = new ToolStripMenuItem($"{val}%");
            item.Checked = customValue.HasValue && customValue.Value == val;
            item.Click += (_, _) => setter(val);
            root.DropDownItems.Add(item);
        }

        return root;
    }
}
