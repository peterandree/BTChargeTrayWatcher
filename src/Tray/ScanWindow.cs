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

    // Feat 61: Auto-refresh timer
    private readonly System.Windows.Forms.Timer _autoRefreshTimer = new();
    private int _autoRefreshCountdown = AutoRefreshIntervalSeconds;
    private const int AutoRefreshIntervalSeconds = 30; // Default interval (can be adjusted)
    private readonly CheckBox _autoRefreshCheckBox = new() {
        Text = "Auto-refresh",
        Checked = true,
        AutoSize = true,
        Dock = DockStyle.Left,
        Margin = new Padding(0, 0, 8, 0)
    };

    // Event for ScanCoordinator to subscribe to
    public event EventHandler? AutoRefreshRequested;

    public ScanWindow(ThresholdSettings settings)
    {
    // (All duplicate/partial class and constructor fragments removed)
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
        buttonPanel.Controls.Add(_autoRefreshCheckBox);

        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(_progress, 0, 2);
        layout.Controls.Add(buttonPanel, 0, 3);

        Controls.Add(layout);

        Resize += (_, _) => AdjustColumns();


        // Feat 61: Start auto-refresh timer when window is shown
        Shown += (_, _) =>
        {
            _autoRefreshCountdown = AutoRefreshIntervalSeconds;
            _autoRefreshTimer.Interval = 1000; // 1 second tick
            _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
            if (_autoRefreshCheckBox.Checked)
                _autoRefreshTimer.Start();
        };

        _autoRefreshCheckBox.CheckedChanged += (_, _) =>
        {
            if (_autoRefreshCheckBox.Checked)
            {
                _autoRefreshCountdown = AutoRefreshIntervalSeconds;
                _autoRefreshTimer.Start();
                UpdateStatusWithCountdown();
            }
            else
            {
                _autoRefreshTimer.Stop();
                _status.Text = "Scan complete. Auto-refresh is off.";
            }
        };

        // Stop and dispose timer on window close
        FormClosed += (_, _) =>
        {
            _autoRefreshTimer.Stop();
            _autoRefreshTimer.Dispose();
        };
    }

    // Called every second by the timer
    private void AutoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            _autoRefreshTimer.Stop();
            return;
        }

        if (!_autoRefreshCheckBox.Checked)
        {
            _autoRefreshTimer.Stop();
            return;
        }

        if (_scanComplete)
        {
            _autoRefreshCountdown--;
            if (_autoRefreshCountdown <= 0)
            {
                // Trigger a rescan (simulate manual scan button)
                _autoRefreshCountdown = AutoRefreshIntervalSeconds;
                // Raise a custom event or call a callback here if needed
                // For now, raise a public event if subscribed
                AutoRefreshRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            // If a scan is running, reset countdown
            _autoRefreshCountdown = AutoRefreshIntervalSeconds;
        }

        // Update status label with countdown
        UpdateStatusWithCountdown();
    }

    private void UpdateStatusWithCountdown()
    {
        if (_scanComplete)
        {
            if (_autoRefreshCheckBox.Checked)
                _status.Text = $"Scan complete. Auto-refresh in {_autoRefreshCountdown}s.";
            else
                _status.Text = "Scan complete. Auto-refresh is off.";
        }
        // else: status is managed by scan events
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
                    string arrow = BatteryTrendHelper.GetArrow(_previousBattery.TryGetValue(deviceId, out var prev) ? prev : null, battery.Value);
                    Color arrowColor = arrow == "↑" ? Color.Green : arrow == "↓" ? Color.Red : SystemColors.WindowText;

                    string batteryText = arrow.Length > 0
                        ? $"{BatteryDisplay.FormatBattery(battery.Value, isCharging)} {arrow}"
                        : BatteryDisplay.FormatBattery(battery.Value, isCharging);

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
                ? $"{BatteryDisplay.FormatBattery(battery.Value, isCharging)} {arrow}"
                : BatteryDisplay.FormatBattery(battery.Value, isCharging);

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

    private void AdjustColumns()
    {
        if (_list.Columns.Count < 3) return;

        int padding = SystemInformation.VerticalScrollBarWidth + 16;
        int available = _list.ClientSize.Width - _list.Columns[1].Width - _list.Columns[2].Width - padding;
        if (available < 400) available = 400;

        _list.Columns[0].Width = available;
    }
}
