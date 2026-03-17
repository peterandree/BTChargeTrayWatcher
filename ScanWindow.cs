using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public class ScanWindow : Form
{
    private readonly ListView _list;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Button _closeBtn;
    private bool _scanComplete;

    public ScanWindow()
    {
        Text = "BT Battery Scan";
        Size = new System.Drawing.Size(520, 400);
        MinimumSize = new System.Drawing.Size(400, 300);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;

        _status = new Label
        {
            Text = "Scanning for Bluetooth devices\u2026",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0)
        };

        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 14,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new System.Drawing.Font("Consolas", 10)
        };
        _list.Columns.Add("Device", 260);
        _list.Columns.Add("Battery", 80);
        _list.Columns.Add("Level", 130);

        _closeBtn = new Button
        {
            Text = "Close",
            Dock = DockStyle.Bottom,
            Height = 32,
            Enabled = false  // disabled until scan completes
        };
        _closeBtn.Click += (_, _) => Close();

        Controls.Add(_list);
        Controls.Add(_progress);
        Controls.Add(_status);
        Controls.Add(_closeBtn);
    }

    public void OnDeviceFound(string name, int battery)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnDeviceFound(name, battery));
            return;
        }

        string batteryText = battery >= 0 ? $"{battery}%" : "n/a";
        string barText = battery >= 0 ? BluetoothBatteryMonitor.BatteryBar(battery) : "\u2014";

        foreach (ListViewItem existing in _list.Items)
        {
            if (existing.Text == name)
            {
                existing.SubItems[1].Text = batteryText;
                existing.SubItems[2].Text = barText;
                existing.ForeColor = BatteryColor(battery);
                return;
            }
        }

        var item = new ListViewItem(name);
        item.SubItems.Add(batteryText);
        item.SubItems.Add(barText);
        item.ForeColor = BatteryColor(battery);
        _list.Items.Add(item);
    }

    public void OnScanComplete(int count)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnScanComplete(count));
            return;
        }

        _scanComplete = true;
        _closeBtn.Enabled = true;

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = 100;
        _status.Text = count == 0
            ? "Scan complete \u2014 no devices with Battery Service found."
            : $"Scan complete \u2014 {count} device(s) found.";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Allow OS-level close (Alt+F4, title bar X) even mid-scan,
        // but block the Close button while scan is running.
        if (!_scanComplete && e.CloseReason == CloseReason.UserClosing
            && !_closeBtn.Enabled)
        {
            // Only the Close button is guarded; Alt+F4 / X still works.
        }
        base.OnFormClosing(e);
    }

    private static System.Drawing.Color BatteryColor(int pct) => pct switch
    {
        < 0 => System.Drawing.Color.Gray,
        <= 20 => System.Drawing.Color.Red,
        <= 40 => System.Drawing.Color.OrangeRed,
        >= 80 => System.Drawing.Color.Green,
        _ => System.Drawing.SystemColors.WindowText
    };
}
