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
        Size = new Size(400, 300);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _status = new Label
        {
            Text = "Scanning for Bluetooth devices...",
            Location = new Point(10, 10),
            AutoSize = true
        };

        _list = new ListView
        {
            View = View.Details,
            Location = new Point(10, 35),
            Size = new Size(360, 180),
            FullRowSelect = true,
            GridLines = true
        };

        _list.Columns.Add("Device", 200);
        _list.Columns.Add("Battery", 60);
        _list.Columns.Add("Level", 90);

        _progress = new ProgressBar
        {
            Location = new Point(10, 225),
            Size = new Size(270, 25),
            Style = ProgressBarStyle.Marquee
        };

        _closeBtn = new Button
        {
            Text = "Close",
            Location = new Point(290, 225),
            Size = new Size(80, 25)
        };
        _closeBtn.Click += (_, _) => Close();

        Controls.Add(_status);
        Controls.Add(_list);
        Controls.Add(_progress);
        Controls.Add(_closeBtn);
    }

    public void OnDeviceFound(string name, int battery)
    {
        bool isIgnored = _settings.IgnoredDevices.Contains(name);

        foreach (ListViewItem item in _list.Items)
        {
            if (item.Text == name)
            {
                if (isIgnored)
                {
                    item.SubItems[1].Text = "-";
                    item.SubItems[2].Text = "[Ignored]";
                    item.ForeColor = Color.Gray;
                }
                else if (battery >= 0)
                {
                    item.SubItems[1].Text = $"{battery}%";
                    item.SubItems[2].Text = BatteryDisplay.Bar(battery);
                    item.ForeColor = SystemColors.WindowText;
                }
                return;
            }
        }

        string pct = isIgnored ? "-" : (battery >= 0 ? $"{battery}%" : "N/A");
        string bar = isIgnored ? "[Ignored]" : (battery >= 0 ? BatteryDisplay.Bar(battery) : "");

        var newItem = new ListViewItem(name);
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
}
