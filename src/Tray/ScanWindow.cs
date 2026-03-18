using System;
using System.Drawing;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public partial class ScanWindow : Form
{
    private ListView lstDevices;
    private ColumnHeader colDevice;
    private ColumnHeader colBattery;
    private ColumnHeader colLevel;
    private Label lblStatus;
    private Button btnClose;

    public ScanWindow()
    {
        InitializeUI();
        Icon = SystemIcons.Information;
    }

    private void InitializeUI()
    {
        Text = "BT Battery Scan";
        Size = new Size(500, 400);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        lblStatus = new Label
        {
            Text = "Scanning for Bluetooth devices...",
            AutoSize = true,
            Location = new Point(10, 10),
            Font = new Font("Segoe UI", 9F)
        };

        lstDevices = new ListView
        {
            View = View.Details,
            Location = new Point(10, 35),
            Size = new Size(465, 280),
            FullRowSelect = true,
            GridLines = true
        };

        colDevice = new ColumnHeader { Text = "Device", Width = 200 };
        colBattery = new ColumnHeader { Text = "Battery", Width = 60 };
        colLevel = new ColumnHeader { Text = "Level", Width = 150 };

        lstDevices.Columns.AddRange(new[] { colDevice, colBattery, colLevel });

        btnClose = new Button
        {
            Text = "Close",
            Location = new Point(200, 325),
            Size = new Size(100, 30)
        };
        btnClose.Click += btnClose_Click;

        Controls.Add(lblStatus);
        Controls.Add(lstDevices);
        Controls.Add(btnClose);
    }

    public void OnDeviceFound(string name, int battery)
    {
        if (IsDisposed) return;

        bool hasBattery = battery >= 0;
        string batteryText = hasBattery ? $"{battery}%" : "N/A";
        string levelText = hasBattery ? BatteryDisplay.Bar(battery) : "[- - - - - - - - - -]";

        var item = new ListViewItem(name);
        item.SubItems.Add(batteryText);
        item.SubItems.Add(levelText);

        if (!hasBattery)
        {
            item.ForeColor = Color.Gray;
        }

        // Avoid adding duplicates if multiple events fire for the same device
        foreach (ListViewItem existing in lstDevices.Items)
        {
            if (existing.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                existing.SubItems[1].Text = batteryText;
                existing.SubItems[2].Text = levelText;
                existing.ForeColor = hasBattery ? SystemColors.WindowText : Color.Gray;
                return;
            }
        }

        lstDevices.Items.Add(item);
        lstDevices.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
    }

    public void OnScanComplete(int count)
    {
        if (IsDisposed) return;

        lblStatus.Text = $"Scan complete. Found {count} device(s).";
    }

    private void btnClose_Click(object? sender, EventArgs e)
    {
        Close();
    }
}
