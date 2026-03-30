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

    public void OnDeviceFound(string deviceId, string name, int? battery)
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
                }
                else if (battery.HasValue)
                {
                    item.SubItems[1].Text = $"{battery.Value}%";
                    item.SubItems[2].Text = BatteryDisplay.Bar(battery.Value);
                    item.ForeColor = SystemColors.WindowText;
                }
                return;
            }
        }

        string pct = isIgnored ? "-" : (battery.HasValue ? $"{battery.Value}%" : "N/A");
        string bar = isIgnored ? "[Ignored]" : (battery.HasValue ? BatteryDisplay.Bar(battery.Value) : "");
        var newItem = new ListViewItem(name);
        newItem.Tag = deviceId;
        newItem.SubItems.Add(pct);
        newItem.SubItems.Add(bar);

        if (isIgnored)
        {
            newItem.ForeColor = Color.Gray;
        }

        _list.Items.Add(newItem);
    }

    public void OnScanComplete(int count)
    {
        if (_scanComplete || IsDisposed) return;
        _scanComplete = true;

        _status.Text = $"Scan complete. {count} device{(count == 1 ? "" : "s")} found.";
        _progress.Style = ProgressBarStyle.Blocks;
        _progress.Value = 100;
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
