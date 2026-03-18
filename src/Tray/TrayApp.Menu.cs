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

        var dumpMenu = new ToolStripMenuItem("Dump device properties…");
        dumpMenu.Click += async (_, _) => await RunUiActionAsync(() => _dumper.DumpToDesktopAsync(), "Dump properties");
        menu.Items.Add(dumpMenu);

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
        return menu;
    }

    private void PopulateDevicesMenu(ToolStripMenuItem parent, IReadOnlyList<(string Name, int Battery)> results)
    {
        parent.DropDownItems.Clear();

        if (results.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("No devices found") { Enabled = false });
            return;
        }

        foreach (var (name, battery) in results)
        {
            string label = battery >= 0
                ? $"{name}   {battery}%  {BatteryDisplay.Bar(battery)}"
                : $"{name}   N/A";

            var item = new ToolStripMenuItem(label);

            var ignoreItem = new ToolStripMenuItem("Ignore device");
            ignoreItem.Click += (_, _) => _settings.ToggleIgnoreDevice(name);
            item.DropDownItems.Add(ignoreItem);

            parent.DropDownItems.Add(item);
        }
    }

    private ToolStripMenuItem BuildLowMenu() =>
        BuildThresholdMenu("Low threshold", _settings.Low, new[] { 10, 15, 20, 25, 30, 50, 60 }, val => _settings.Low = val);

    private ToolStripMenuItem BuildHighMenu() =>
        BuildThresholdMenu("High threshold", _settings.High, new[] { 70, 75, 80, 85, 90 }, val => _settings.High = val);

    private ToolStripMenuItem BuildThresholdMenu(
        string baseText,
        int currentValue,
        int[] candidates,
        Action<int> setter)
    {
        var root = new ToolStripMenuItem($"{baseText}: {currentValue}%");

        foreach (int val in candidates)
        {
            var item = new ToolStripMenuItem($"{val}%");
            if (val == currentValue)
                item.Checked = true;

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
                    System.Diagnostics.Debug.WriteLine($"[TrayApp] Threshold update fault: {ex}");
                }
            };
            root.DropDownItems.Add(item);
        }

        return root;
    }

    private void UpdateTooltip()
    {
        _trayIcon.Text = $"BT Battery Alert  ▼{_settings.Low}%  ▲{_settings.High}%";
    }
}
