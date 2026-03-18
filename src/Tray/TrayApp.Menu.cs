using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public partial class TrayApp
{
    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(_devicesMenu);
        menu.Items.Add(_scanMenuItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_lowMenu);
        menu.Items.Add(_highMenu);
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
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        return menu;
    }

    private void ExitApp()
    {
        if (_exitStarted) return;
        _exitStarted = true;

        _trayIcon.Visible = false;
        Application.Exit();
    }

    private ToolStripMenuItem BuildDevicesMenu()
    {
        var menu = new ToolStripMenuItem("Connected devices");
        menu.DropDownItems.Add(new ToolStripMenuItem("⏳ Initializing…") { Enabled = false });

        menu.DropDownOpening += (_, _) => PopulateDevicesMenu(menu, _monitor.LastKnownDevices);

        return menu;
    }

    private void PopulateDevicesMenu(ToolStripMenuItem parent, IReadOnlyList<(string Name, int Battery)> results)
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

        foreach (var (name, battery) in results)
        {
            bool isIgnored = _settings.IgnoredDevices.Contains(name);

            string label;
            if (isIgnored)
            {
                label = $"{name}   [Ignored]";
            }
            else
            {
                label = battery >= 0
                    ? $"{name}   {battery}%  {BatteryDisplay.Bar(battery)}"
                    : $"{name}   N/A";
            }

            var item = new ToolStripMenuItem(label);

            if (!isIgnored)
            {
                var lowMenu = BuildDeviceThresholdMenu(
                    "Low limit",
                    _settings.Low,
                    _settings.HasCustomLow(name) ? _settings.GetLow(name) : null,
                    new[] { 10, 15, 20, 25, 30 },
                    val => _settings.SetLow(name, val));

                var highMenu = BuildDeviceThresholdMenu(
                    "High limit",
                    _settings.High,
                    _settings.HasCustomHigh(name) ? _settings.GetHigh(name) : null,
                    new[] { 70, 75, 80, 85, 90 },
                    val => _settings.SetHigh(name, val));

                item.DropDownItems.Add(lowMenu);
                item.DropDownItems.Add(highMenu);
                item.DropDownItems.Add(new ToolStripSeparator());
            }

            var ignoreItem = new ToolStripMenuItem(isIgnored ? "Stop ignoring device" : "Ignore device");
            ignoreItem.Click += (_, _) => _settings.ToggleIgnoreDevice(name);

            item.DropDownItems.Add(ignoreItem);

            parent.DropDownItems.Add(item);
        }
    }

    private ToolStripMenuItem BuildLowMenu() =>
        BuildGlobalThresholdMenu("Global Low threshold", _settings.Low, new[] { 10, 15, 20, 25, 30 }, val => _settings.Low = val);

    private ToolStripMenuItem BuildHighMenu() =>
        BuildGlobalThresholdMenu("Global High threshold", _settings.High, new[] { 70, 75, 80, 85, 90 }, val => _settings.High = val);

    private ToolStripMenuItem BuildGlobalThresholdMenu(
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
                        if (child is ToolStripMenuItem mi) mi.Checked = (mi.Text == $"{val}%");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrayApp] Global threshold update fault: {ex}");
                }
            };
            root.DropDownItems.Add(item);
        }
        return root;
    }

    private ToolStripMenuItem BuildDeviceThresholdMenu(
        string baseText,
        int globalValue,
        int? customValue,
        int[] candidates,
        Action<int?> setter)
    {
        string displayValue = customValue.HasValue ? $"{customValue.Value}%" : $"Global ({globalValue}%)";
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

    private void UpdateTooltip()
    {
        _trayIcon.Text = $"BT Battery Alert  ▼{_settings.Low}%  ▲{_settings.High}%";
    }
}
