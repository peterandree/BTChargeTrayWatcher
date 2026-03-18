using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public partial class TrayApp
{
    private static readonly int[] LowThresholdValues = [10, 15, 20, 25, 30];
    private static readonly int[] HighThresholdValues = [70, 75, 80, 85, 90];

    private ToolStripMenuItem BuildLowMenu() =>
        BuildThresholdMenu(
            prefix: "Low threshold",
            currentValue: _settings.Low,
            values: LowThresholdValues,
            apply: _settings.SetLow,
            successLogLabel: "Low");

    private ToolStripMenuItem BuildHighMenu() =>
        BuildThresholdMenu(
            prefix: "High threshold",
            currentValue: _settings.High,
            values: HighThresholdValues,
            apply: _settings.SetHigh,
            successLogLabel: "High");

    private static ToolStripMenuItem BuildThresholdMenu(
        string prefix,
        int currentValue,
        IEnumerable<int> values,
        Action<int> apply,
        string successLogLabel)
    {
        var item = new ToolStripMenuItem($"{prefix}: {currentValue}%");

        foreach (int v in values)
        {
            int val = v;
            item.DropDownItems.Add($"{val}%", null, (_, _) =>
            {
                try
                {
                    apply(val);
                    item.Text = $"{prefix}: {val}%";
                    Debug.WriteLine($"[TrayApp] {successLogLabel} threshold set to {val}%.");
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    Debug.WriteLine($"[TrayApp] Invalid {successLogLabel.ToLowerInvariant()} threshold: {ex.Message}");
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
            {
                return;
            }

            if (_monitor.IsScanning)
            {
                return;
            }

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
                ? $"{name}   {battery}%  {BatteryDisplay.Bar(battery)}"
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
            await RunUiActionAsync(DumpDevicePropertiesAsync, "Device dump"));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) =>
            await RunUiActionAsync(ExitAsync, "Exit handler"));

        return menu;
    }

    private async Task DumpDevicePropertiesAsync()
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

    private void UpdateTooltip()
    {
        if (_disposed)
        {
            return;
        }

        _trayIcon.Text = $"BT Battery Alert  ▼{_settings.Low}%  ▲{_settings.High}%";
    }
}
