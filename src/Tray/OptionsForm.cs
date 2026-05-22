using System;
using System.Linq;
using System.Windows.Forms;

namespace BTChargeTrayWatcher.Tray
{
    public sealed class OptionsForm : Form
    {
        public delegate DialogResult MessageBoxHandler(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon);
        private readonly MessageBoxHandler _messageBoxHandler;
        private readonly TabControl tabControl;
        private readonly TabPage devicesTab;
        private readonly TabPage notificationsTab;
        private readonly TabPage generalTab;
        // ntfy controls
        private readonly CheckBox ntfyEnabledCheck;
        private readonly TextBox ntfyTopicTextBox;
        private readonly Button regenerateTopicBtn;
        private readonly Button sendNtfyTestBtn;
        private readonly NumericUpDown lowNumeric;
        private readonly NumericUpDown highNumeric;
        private readonly NumericUpDown laptopLowNumeric;
        private readonly NumericUpDown laptopHighNumeric;
        private readonly CheckBox excludeLaptopOverlayCheck;
        private INotificationService? _notifier;

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

        public OptionsForm(MessageBoxHandler? messageBoxHandler = null)
        {
            _messageBoxHandler = messageBoxHandler ?? ((owner, text, caption, buttons, icon) => MessageBox.Show(owner, text, caption, buttons, icon));
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



            // Redesigned Notifications tab layout
            notificationsTab = new TabPage("Notifications");
            var notifLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
                RowCount = 4,
                ColumnCount = 1,
            };
            notifLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // ntfy controls
            notifLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8)); // spacing
            notifLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // help
            notifLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons

            // ntfy controls group
            var ntfyGroup = new GroupBox
            {
                Text = "Mobile notifications (ntfy.sh)",
                Dock = DockStyle.Top,
                Padding = new Padding(10),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            ntfyEnabledCheck = new CheckBox { Text = "Enable ntfy notifications", AutoSize = true, Dock = DockStyle.Top };
            var topicPanel = new Panel { Dock = DockStyle.Top, Height = 30 };
            var topicLabel = new Label { Text = "Topic:", AutoSize = true, Width = 40, Dock = DockStyle.Left };
            ntfyTopicTextBox = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
            topicPanel.Controls.Add(ntfyTopicTextBox);
            topicPanel.Controls.Add(topicLabel);

            regenerateTopicBtn = new Button { Text = "Regenerate topic", Width = 140, Height = 32, Margin = new Padding(0,0,8,0) };
            sendNtfyTestBtn = new Button { Text = "Send ntfy test", Width = 140, Height = 32, Margin = new Padding(0,0,0,0) };

            // Horizontal panel for ntfy action buttons
            var ntfyButtonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 8, 0, 0),
                Padding = new Padding(0)
            };
            ntfyButtonPanel.Controls.Add(sendNtfyTestBtn);
            ntfyButtonPanel.Controls.Add(regenerateTopicBtn);

            ntfyGroup.Controls.Add(ntfyButtonPanel);
            ntfyGroup.Controls.Add(topicPanel);
            ntfyGroup.Controls.Add(ntfyEnabledCheck);
            notifLayout.Controls.Add(ntfyGroup, 0, 0);

            // spacing
            notifLayout.Controls.Add(new Panel { Height = 8, Dock = DockStyle.Top }, 0, 1);

            // ntfy setup instructions/help (scrollable, clickable)
            var ntfyHelpBox = new GroupBox
            {
                Text = "ntfy mobile setup instructions",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            var ntfyHelpText = new RichTextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                BackColor = SystemColors.Control,
                DetectUrls = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Margin = new Padding(0),
                Text = "To receive push notifications on your phone:\n\n" +
                       "1. Install the ntfy app on your device:\n" +
                       "   • Android: https://docs.ntfy.sh/subscribe/phone/\n" +
                       "   • iPhone: https://docs.ntfy.sh/subscribe/phone/\n" +
                       "2. Open the app and subscribe to your topic (shown above) using server ntfy.sh.\n" +
                       "3. For more info, see: https://ntfy.sh"
            };
            ntfyHelpText.LinkClicked += (s, e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = e.LinkText, UseShellExecute = true });
            ntfyHelpBox.Controls.Add(ntfyHelpText);
            notifLayout.Controls.Add(ntfyHelpBox, 0, 2);

            notifLayout.Controls.Add(new Panel { Height = 8, Dock = DockStyle.Top }, 0, 3);

            notificationsTab.Controls.Add(notifLayout);


            // General tab
            generalTab = new TabPage("General");
            var generalPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                AutoSize = true,
                Padding = new Padding(16),
            };
            generalPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            generalPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

            // Global thresholds
            generalPanel.Controls.Add(new Label { Text = "Global Low %", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
            lowNumeric = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 60, Anchor = AnchorStyles.Left };
            generalPanel.Controls.Add(lowNumeric, 1, 0);
            generalPanel.Controls.Add(new Label { Text = "Global High %", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
            highNumeric = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 60, Anchor = AnchorStyles.Left };
            generalPanel.Controls.Add(highNumeric, 1, 1);

            // Laptop thresholds
            generalPanel.Controls.Add(new Label { Text = "Laptop Low %", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
            laptopLowNumeric = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 60, Anchor = AnchorStyles.Left };
            generalPanel.Controls.Add(laptopLowNumeric, 1, 2);
            generalPanel.Controls.Add(new Label { Text = "Laptop High %", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 3);
            laptopHighNumeric = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 60, Anchor = AnchorStyles.Left };
            generalPanel.Controls.Add(laptopHighNumeric, 1, 3);

            // Exclude laptop from tray icon overlay
            excludeLaptopOverlayCheck = new CheckBox { Text = "Exclude laptop from tray icon overlay", Anchor = AnchorStyles.Left, AutoSize = true };
            generalPanel.Controls.Add(excludeLaptopOverlayCheck, 0, 4);
            generalPanel.SetColumnSpan(excludeLaptopOverlayCheck, 2);

            generalTab.Controls.Add(generalPanel);

            tabControl.TabPages.Add(devicesTab);
            tabControl.TabPages.Add(notificationsTab);
            tabControl.TabPages.Add(generalTab);

            Controls.Add(tabControl);

            // Defer data wiring until settings/monitor are injected
        }

        public void Initialize(ThresholdSettings settings, BluetoothBatteryMonitor monitor, INotificationService? notifier = null)
        {
            _settings = settings;
            _monitor = monitor;
            _notifier = notifier;
            // Wire up general tab controls to settings
            lowNumeric.Value = settings.Low;
            highNumeric.Value = settings.High;
            laptopLowNumeric.Value = settings.LaptopLow;
            laptopHighNumeric.Value = settings.LaptopHigh;
            excludeLaptopOverlayCheck.Checked = settings.ExcludeLaptopFromTrayIconOverlay;

            lowNumeric.ValueChanged += (_, _) =>
            {
                try { settings.Low = (int)lowNumeric.Value; }
                catch (ArgumentOutOfRangeException ex) { _messageBoxHandler(this, ex.Message, "Invalid threshold", MessageBoxButtons.OK, MessageBoxIcon.Error); lowNumeric.Value = settings.Low; }
            };
            highNumeric.ValueChanged += (_, _) =>
            {
                try { settings.High = (int)highNumeric.Value; }
                catch (ArgumentOutOfRangeException ex) { _messageBoxHandler(this, ex.Message, "Invalid threshold", MessageBoxButtons.OK, MessageBoxIcon.Error); highNumeric.Value = settings.High; }
            };
            laptopLowNumeric.ValueChanged += (_, _) =>
            {
                try { settings.LaptopLow = (int)laptopLowNumeric.Value; }
                catch (ArgumentOutOfRangeException ex) { _messageBoxHandler(this, ex.Message, "Invalid threshold", MessageBoxButtons.OK, MessageBoxIcon.Error); laptopLowNumeric.Value = settings.LaptopLow; }
            };
            laptopHighNumeric.ValueChanged += (_, _) =>
            {
                try { settings.LaptopHigh = (int)laptopHighNumeric.Value; }
                catch (ArgumentOutOfRangeException ex) { _messageBoxHandler(this, ex.Message, "Invalid threshold", MessageBoxButtons.OK, MessageBoxIcon.Error); laptopHighNumeric.Value = settings.LaptopHigh; }
            };
            excludeLaptopOverlayCheck.CheckedChanged += (_, _) => settings.ExcludeLaptopFromTrayIconOverlay = excludeLaptopOverlayCheck.Checked;

            // ntfy controls wiring
            var ntfy = settings.GetNtfySettings();
            ntfyEnabledCheck.Checked = ntfy.IsEnabled;
            ntfyTopicTextBox.Text = ntfy.Topic ?? string.Empty;

            ntfyEnabledCheck.CheckedChanged += (_, _) =>
            {
                settings.UpdateNtfySettings(s => s.IsEnabled = ntfyEnabledCheck.Checked);
            };

            regenerateTopicBtn.Click += (_, _) =>
            {
                string topic = NtfyTopicGenerator.Generate();
                settings.UpdateNtfySettings(s =>
                {
                    s.Topic = topic;
                    s.IsEnabled = false; // require explicit re-enable after regen
                });
                _messageBoxHandler(this,
                    $"New topic generated:\n\n{topic}\n\nSubscribe to this topic in the ntfy app on your phone using server ntfy.sh, then enable the integration.",
                    "Mobile notifications — new topic",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                ntfyTopicTextBox.Text = topic;
                ntfyEnabledCheck.Checked = false;
            };

            sendNtfyTestBtn.Click += async (_, _) =>
            {
                var channel = new NtfyNotificationChannel(settings.GetNtfySettings());
                bool ok = await channel.SendTestNotificationAsync().ConfigureAwait(false);
                string msg = ok
                    ? "Test notification sent. Check your phone."
                    : "Failed to send test notification. Check your internet connection and verify the topic is correct.";
                if (this.InvokeRequired)
                {
                    this.Invoke(new Action(() =>
                        _messageBoxHandler(this, msg, "Mobile notifications — test", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning)));
                }
                else
                {
                    _messageBoxHandler(this, msg, "Mobile notifications — test", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
            };

            LoadDeviceRows();
            devicesGrid.CellValueChanged += DevicesGrid_CellValueChanged;
            devicesGrid.CellContentClick += DevicesGrid_CellContentClick;
            devicesGrid.DataError += (_, e) =>
            {
                // Show a friendly message when parsing/editing fails (e.g., non-numeric input),
                // then reload rows so the UI reflects persisted settings.
                try
                {
                    _messageBoxHandler(this, "Invalid value entered. Please enter a numeric value.", "Invalid input", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                LoadDeviceRows();
                e.ThrowException = false;
            };
            // Only commit edits immediately for checkbox cells (Excluded). Leave
            // text/numeric cells to commit on edit end so users can type values.
            devicesGrid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (!devicesGrid.IsCurrentCellDirty) return;
                try
                {
                    var col = devicesGrid.CurrentCell?.OwningColumn;
                    if (col is DataGridViewCheckBoxColumn)
                        devicesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
                catch { /* swallow UI inspection errors */ }
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
                    _messageBoxHandler(this, $"Failed to set alias: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    _messageBoxHandler(this, ex.Message, "Invalid threshold", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    _messageBoxHandler(this, ex.Message, "Invalid threshold", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    _messageBoxHandler(this, "Poll interval must be a positive integer.", "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            var confirm = _messageBoxHandler(this, "Reset all device thresholds and poll intervals to defaults?", "Confirm Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
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
