using System;
using System.Linq;
using System.Windows.Forms;

namespace BTChargeTrayWatcher.Tray
{
    public sealed class OptionsForm : Form
    {
        private readonly TabControl tabControl;
        private readonly TabPage devicesTab;
        private readonly TabPage notificationsTab;
        private readonly TabPage generalTab;

        private class DeviceRow
        {
            public string DeviceId { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public int? Low { get; set; }
            public int? High { get; set; }
            public int? PollInterval { get; set; }
            public bool Excluded { get; set; }
        }

        private readonly DataGridView devicesGrid;
        private readonly Button resetAllBtn;
        private List<DeviceRow> deviceRows = new();
        private ThresholdSettings? _settings;
        private BluetoothBatteryMonitor? _monitor;

        public OptionsForm()
        {
            Text = "Options";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            Width = 600;
            Height = 400;

            tabControl = new TabControl
            {
                Dock = DockStyle.Fill
            };

            // Devices tab
            devicesTab = new TabPage("Devices");
            devicesGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoGenerateColumns = false
            };
            devicesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Display Name", DataPropertyName = "DisplayName", Width = 160 });
            devicesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Low %", DataPropertyName = "Low", Width = 60, ValueType = typeof(int) });
            devicesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "High %", DataPropertyName = "High", Width = 60, ValueType = typeof(int) });
            devicesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Poll (s)", DataPropertyName = "PollInterval", Width = 80, ValueType = typeof(int) });
            devicesGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Excluded", DataPropertyName = "Excluded", Width = 70 });
            var resetCol = new DataGridViewButtonColumn { HeaderText = "⟲", Text = "Reset", UseColumnTextForButtonValue = true, Width = 60 };
            devicesGrid.Columns.Add(resetCol);
            devicesTab.Controls.Add(devicesGrid);

            resetAllBtn = new Button { Text = "Reset All Devices", Dock = DockStyle.Bottom, Height = 32 };
            devicesTab.Controls.Add(resetAllBtn);

            notificationsTab = new TabPage("Notifications");
            generalTab = new TabPage("General");

            tabControl.TabPages.Add(devicesTab);
            tabControl.TabPages.Add(notificationsTab);
            tabControl.TabPages.Add(generalTab);

            Controls.Add(tabControl);

            // Defer data wiring until settings/monitor are injected
        }

        public void Initialize(ThresholdSettings settings, BluetoothBatteryMonitor monitor)
        {
            _settings = settings;
            _monitor = monitor;
            LoadDeviceRows();
            devicesGrid.CellValueChanged += DevicesGrid_CellValueChanged;
            devicesGrid.CellContentClick += DevicesGrid_CellContentClick;
            devicesGrid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (devicesGrid.IsCurrentCellDirty)
                    devicesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            resetAllBtn.Click += ResetAllBtn_Click;
        }

        private void LoadDeviceRows()
        {
            if (_settings == null || _monitor == null) return;
            deviceRows = new List<DeviceRow>();
            foreach (var d in _monitor.LastKnownDevices)
            {
                deviceRows.Add(new DeviceRow
                {
                    DeviceId = d.DeviceId,
                    DisplayName = d.Name,
                    Low = _settings.GetLowForDevice(d.DeviceId, d.Name),
                    High = _settings.GetHighForDevice(d.DeviceId, d.Name),
                    PollInterval = _settings.GetPollIntervalForDevice(d.DeviceId, d.Name) ?? (int)PollingDefaults.PollingInterval.TotalSeconds,
                    Excluded = _settings.IsIgnored(d.DeviceId, d.Name)
                });
            }
            devicesGrid.DataSource = null;
            devicesGrid.DataSource = deviceRows;
        }

        private void DevicesGrid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
        {
            if (_settings == null || e.RowIndex < 0 || e.RowIndex >= deviceRows.Count) return;
            var row = deviceRows[e.RowIndex];
            var col = devicesGrid.Columns[e.ColumnIndex].DataPropertyName;
            if (col == "DisplayName")
            {
                try
                {
                    string defaultName = row.DisplayName;
                    if (_monitor != null)
                    {
                        var tracked = _monitor.TrackedDevices.FirstOrDefault(t => t.DeviceId == row.DeviceId);
                        if (tracked != null) defaultName = tracked.Name;
                    }

                    if (string.Equals(row.DisplayName, defaultName, StringComparison.OrdinalIgnoreCase))
                        _settings.SetDisplayNameAlias(row.DeviceId, null);
                    else
                        _settings.SetDisplayNameAlias(row.DeviceId, row.DisplayName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Failed to set alias: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    LoadDeviceRows();
                }
            }
            else if (col == "Low")
            {
                try
                {
                    _settings.SetLowForDevice(row.DeviceId, row.Low);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    MessageBox.Show(this, ex.Message, "Invalid threshold", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    LoadDeviceRows();
                }
            }
            else if (col == "High")
            {
                try
                {
                    _settings.SetHighForDevice(row.DeviceId, row.High);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    MessageBox.Show(this, ex.Message, "Invalid threshold", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    LoadDeviceRows();
                }
            }
            else if (col == "Excluded")
            {
                if (row.Excluded)
                    _settings.SetIgnoredDevicesByIds(_settings.IgnoredDevices.Union(new[] { row.DeviceId }));
                else
                    _settings.SetIgnoredDevicesByIds(_settings.IgnoredDevices.Except(new[] { row.DeviceId }));
            }
            else if (col == "PollInterval")
            {
                if (row.PollInterval.HasValue && row.PollInterval.Value <= 0)
                {
                    MessageBox.Show(this, "Poll interval must be a positive integer.", "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    LoadDeviceRows();
                }
                else
                {
                    _settings.SetPollIntervalForDevice(row.DeviceId, row.PollInterval);
                }
            }
        }

        private void DevicesGrid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (devicesGrid.Columns[e.ColumnIndex] is DataGridViewButtonColumn)
            {
                // Reset button clicked
                var row = deviceRows[e.RowIndex];
                if (_settings != null)
                {
                    _settings.SetLowForDevice(row.DeviceId, null);
                    _settings.SetHighForDevice(row.DeviceId, null);
                    _settings.SetPollIntervalForDevice(row.DeviceId, null);
                    LoadDeviceRows();
                }
            }
        }

        private void ResetAllBtn_Click(object? sender, EventArgs e)
        {
            if (_settings == null) return;
            var confirm = MessageBox.Show(this, "Reset all device thresholds and poll intervals to defaults?", "Confirm Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm == DialogResult.Yes)
            {
                foreach (var row in deviceRows)
                {
                    _settings.SetLowForDevice(row.DeviceId, null);
                    _settings.SetHighForDevice(row.DeviceId, null);
                    _settings.SetPollIntervalForDevice(row.DeviceId, null);
                }
                LoadDeviceRows();
            }
        }
    }
}
