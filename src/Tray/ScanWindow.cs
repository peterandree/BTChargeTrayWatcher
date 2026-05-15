using System;
using System.Drawing;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public partial class ScanWindow : Form
{
    private readonly ThresholdSettings _settings;
    private readonly ListView _list;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Button _closeBtn;
    private bool _scanComplete;
    private readonly Dictionary<string, int> _previousBattery = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _currentScanValues = new(StringComparer.OrdinalIgnoreCase);

    public ScanWindow(ThresholdSettings settings)
    {
        _settings = settings;

        Text = "BT Battery Scan";
        ClientSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        SizeGripStyle = SizeGripStyle.Show;
        MinimumSize = new Size(700, 450);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _status = new Label
        {
            Text = "Scanning for Bluetooth devices...",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8)
        };

        _list = new ListView
        {
            View = View.Details,
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = true,
            ShowItemToolTips = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        _list.Columns.Add("Device", 500);
        _list.Columns.Add("Battery", 120);
        _list.Columns.Add("Level", 160);

        _progress = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Style = ProgressBarStyle.Marquee,
            Margin = new Padding(0, 0, 0, 8)
        };

        _closeBtn = new Button
        {
            Text = "Close",
            Dock = DockStyle.Right
        };
        _closeBtn.Click += (_, _) => Close();

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        buttonPanel.Controls.Add(_closeBtn);

        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(_progress, 0, 2);
        layout.Controls.Add(buttonPanel, 0, 3);

        Controls.Add(layout);

        Resize += (_, _) => AdjustColumns();
    }

    public void OnDeviceFound(string deviceId, string name, int? battery, bool? isCharging = null)
    {
        bool isIgnored = _settings.IgnoredDevices.Contains(name);

        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is string id && string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                if (isIgnored)
                {
                    item.SubItems[1].Text = "-";
                    item.SubItems[2].Text = "[Ignored]";
                    item.ForeColor = Color.Gray;
                    item.ToolTipText = name;
                }
                else if (battery.HasValue)
                {
                    // Compute trend arrow using previous snapshot (only show ↑ / ↓)
                    string arrow = "";
                    Color arrowColor = SystemColors.WindowText;
                    if (_previousBattery.TryGetValue(deviceId, out var prev))
                    {
                        if (battery.Value > prev) { arrow = "↑"; arrowColor = Color.Green; }
                        else if (battery.Value < prev) { arrow = "↓"; arrowColor = Color.Red; }
                    }

                    string batteryText = arrow.Length > 0
                        ? $"{FormatBattery(battery.Value, isCharging)} {arrow}"
                        : FormatBattery(battery.Value, isCharging);

                    item.SubItems[1].Text = batteryText;
                    item.SubItems[1].ForeColor = arrowColor;
                    item.SubItems[2].Text = BatteryDisplay.Bar(battery.Value);
                    item.ForeColor = SystemColors.WindowText;
                    item.ToolTipText = arrow.Length > 0 ? $"{arrow} {name}" : name;

                    _currentScanValues[deviceId] = battery.Value;
                }
                return;
            }
        }

        string pct;
        string bar;
        string tooltip;
        if (isIgnored)
        {
            pct = "-";
            bar = "[Ignored]";
            tooltip = name;
        }
        else if (battery.HasValue)
        {
            string arrow = "";
            Color arrowColor = SystemColors.WindowText;
            if (_previousBattery.TryGetValue(deviceId, out var prev))
            {
                if (battery.Value > prev) { arrow = "↑"; arrowColor = Color.Green; }
                else if (battery.Value < prev) { arrow = "↓"; arrowColor = Color.Red; }
            }

            pct = arrow.Length > 0
                ? $"{FormatBattery(battery.Value, isCharging)} {arrow}"
                : FormatBattery(battery.Value, isCharging);

            bar = BatteryDisplay.Bar(battery.Value);
            tooltip = arrow.Length > 0 ? $"{arrow} {name}" : name;
        }
        else
        {
            pct = "N/A";
            bar = "";
            tooltip = name;
        }

        var newItem = new ListViewItem(name);
        newItem.Tag = deviceId;
        newItem.SubItems.Add(pct);
        newItem.SubItems.Add(bar);
        newItem.ToolTipText = tooltip;

        if (isIgnored)
            newItem.ForeColor = Color.Gray;

        _list.Items.Add(newItem);

        if (battery.HasValue)
            _currentScanValues[deviceId] = battery.Value;
    }

    internal void OnScanStarted()
    {
        if (IsDisposed) return;
        _scanComplete = false;
        _currentScanValues.Clear();
        _status.Text = "Scanning for Bluetooth devices...";
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Value = 0;
    }

    internal void OnScanComplete(int batteryDeviceCount, IReadOnlyList<WatchedDevice> trackedDevices)
    {
        if (_scanComplete || IsDisposed) return;
        _scanComplete = true;

        // Add all paired tracked devices that weren't found by the battery readers.
        // This ensures sleeping BLE devices and devices without battery service are visible.
        // Dedup by both ID and name: the same physical device can appear in the BLE watcher,
        // Classic watcher, and battery readers with completely different device IDs.
        int noBatteryCount = 0;
        var shownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is string id)
                shownIds.Add(id);
            shownNames.Add(item.Text);
        }

        foreach (var device in trackedDevices)
        {
            if (shownIds.Contains(device.DeviceId)) continue;
            if (shownNames.Contains(device.Name)) continue;

            string reason = device.IsConnected
                ? "[No battery service]"
                : "[Sleeping / not connected]";

            var newItem = new ListViewItem(device.Name) { Tag = device.DeviceId };
            newItem.SubItems.Add("-");
            newItem.SubItems.Add(reason);
            newItem.ForeColor = Color.Gray;
            _list.Items.Add(newItem);
            shownNames.Add(device.Name);
            noBatteryCount++;
        }

        int totalShown = _list.Items.Count;
        string statusText = noBatteryCount > 0
            ? $"Scan complete. {totalShown} device{(totalShown == 1 ? "" : "s")} found ({noBatteryCount} without battery data)."
            : $"Scan complete. {totalShown} device{(totalShown == 1 ? "" : "s")} found.";

        _status.Text = statusText;
        _progress.Style = ProgressBarStyle.Blocks;
        _progress.Value = 100;

        // After rendering the scan results, promote current readings to previous snapshot
        foreach (var kvp in _currentScanValues)
            _previousBattery[kvp.Key] = kvp.Value;
        _currentScanValues.Clear();
    }

    /// <summary>
    /// Formats the battery percentage cell text.
    /// Appends " \u26a1" (⚡) only when charging is confirmed true.
    /// Unknown (null) renders as plain percentage — absence of data is not shown as a state.
    /// </summary>
    private static string FormatBattery(int battery, bool? isCharging) =>
        isCharging == true ? $"{battery}% \u26a1" : $"{battery}%";

    private void AdjustColumns()
    {
        if (_list.Columns.Count < 3) return;

        int padding = SystemInformation.VerticalScrollBarWidth + 16;
        int available = _list.ClientSize.Width - _list.Columns[1].Width - _list.Columns[2].Width - padding;
        if (available < 400) available = 400;

        _list.Columns[0].Width = available;
    }
}
