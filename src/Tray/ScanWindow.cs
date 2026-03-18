namespace BTChargeTrayWatcher;

public class ScanWindow : Form
{
    private readonly ListView _list;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Button _closeBtn;

    public ScanWindow()
    {
        Text = "BT Battery Scan";
        Size = new System.Drawing.Size(520, 400);
        MinimumSize = new System.Drawing.Size(400, 300);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;

        _status = new Label
        {
            Text = "Scanning for Bluetooth devices…",
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
            Enabled = false
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
        string barText = battery >= 0 ? BatteryDisplay.Bar(battery) : "—";

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

        _closeBtn.Enabled = true;

        // Snappy completion: overshoot then settle to avoid animation lag
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = 101; // forces immediate repaint
        _progress.Value = 100;

        _status.Text = count == 0
            ? "Scan complete — no devices with Battery Service found."
            : $"Scan complete — {count} device(s) found.";
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
