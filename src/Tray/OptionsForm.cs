using System;
using System.Linq;
using System.Windows.Forms;

namespace BTChargeTrayWatcher.Tray
{
    internal sealed class OptionsForm : Form
    {
        public delegate DialogResult MessageBoxHandler(
            IWin32Window? owner, string text, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon);

        private readonly MessageBoxHandler _messageBox;

        private ThresholdSettings? _settings;
        private BluetoothBatteryMonitor? _monitor;
        private INotificationService? _notifier;

        private System.Windows.Forms.ListView? _deviceListView;
        private System.Windows.Forms.Button? _closeButton;
        private System.Windows.Forms.Button? _saveButton;
        private System.Windows.Forms.TextBox? _globalLowBox;
        private System.Windows.Forms.TextBox? _globalHighBox;
        private System.Windows.Forms.TextBox? _globalPollBox;
        private System.Windows.Forms.Label? _statusLabel;
        private System.Windows.Forms.CheckBox? _startupCheckBox;

        private bool _initialized;
        private bool _dirty;

        // Expose the internal ListView for testing
        internal System.Windows.Forms.ListView? DeviceListView => _deviceListView;

        public OptionsForm()
            : this((owner, text, caption, buttons, icon) =>
                MessageBox.Show(owner, text, caption, buttons, icon))
        {
        }

        internal OptionsForm(MessageBoxHandler messageBoxHandler)
        {
            _messageBox = messageBoxHandler;
            InitializeComponent();
        }

        internal void Initialize(ThresholdSettings settings, BluetoothBatteryMonitor monitor, INotificationService? notifier = null)
        {
            _settings  = settings;
            _monitor   = monitor;
            _notifier  = notifier;
            _initialized = true;

            PopulateGlobals();
            LoadDeviceRows();
            WireMonitorEvents();
            PopulateStartupCheckBox();
        }

        private void InitializeComponent()
        {
            Text            = "BTChargeTrayWatcher — Options";
            Width           = 780;
            Height          = 560;
            MinimumSize     = new System.Drawing.Size(600, 400);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterScreen;

            var mainLayout = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                RowCount    = 3,
                ColumnCount = 1,
                Padding     = new Padding(8),
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // ── Global settings row ──────────────────────────────────────
            var globalsPanel = new FlowLayoutPanel
            {
                AutoSize      = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                Padding       = new Padding(0, 0, 0, 6),
            };

            globalsPanel.Controls.Add(MakeLabel("Global low %:"));
            _globalLowBox = MakeTextBox(3);
            globalsPanel.Controls.Add(_globalLowBox);

            globalsPanel.Controls.Add(MakeLabel("  Global high %:"));
            _globalHighBox = MakeTextBox(3);
            globalsPanel.Controls.Add(_globalHighBox);

            globalsPanel.Controls.Add(MakeLabel("  Poll interval (s):"));
            _globalPollBox = MakeTextBox(4);
            globalsPanel.Controls.Add(_globalPollBox);

            _startupCheckBox = new CheckBox { Text = "  Start with Windows", AutoSize = true, Margin = new Padding(8, 3, 0, 3) };
            globalsPanel.Controls.Add(_startupCheckBox);

            mainLayout.Controls.Add(globalsPanel, 0, 0);

            // ── Device list ──────────────────────────────────────────────
            _deviceListView = new System.Windows.Forms.ListView
            {
                Dock          = DockStyle.Fill,
                View          = View.Details,
                FullRowSelect = true,
                GridLines     = true,
                LabelEdit     = false,
            };
            _deviceListView.Columns.Add("Device",          180);
            _deviceListView.Columns.Add("Display name",    150);
            _deviceListView.Columns.Add("Low %",            60);
            _deviceListView.Columns.Add("High %",           60);
            _deviceListView.Columns.Add("Poll (s)",         70);
            _deviceListView.Columns.Add("Ignored",          60);
            _deviceListView.Columns.Add("Device ID",       160);

            mainLayout.Controls.Add(_deviceListView, 0, 1);

            // ── Bottom bar ───────────────────────────────────────────────
            var bottomBar = new FlowLayoutPanel
            {
                AutoSize      = true,
                FlowDirection = FlowDirection.RightToLeft,
                Dock          = DockStyle.Fill,
                Padding       = new Padding(0, 6, 0, 0),
            };

            _closeButton = new Button { Text = "Close",  Width = 80, Height = 28 };
            _saveButton  = new Button { Text = "Save",   Width = 80, Height = 28 };
            _statusLabel = new Label  { Text = string.Empty, AutoSize = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };

            _closeButton.Click += OnCloseClicked;
            _saveButton.Click  += OnSaveClicked;

            bottomBar.Controls.Add(_closeButton);
            bottomBar.Controls.Add(_saveButton);
            bottomBar.Controls.Add(_statusLabel);

            mainLayout.Controls.Add(bottomBar, 0, 2);
            Controls.Add(mainLayout);

            FormClosing += OnFormClosing;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static Label MakeLabel(string text) =>
            new() { Text = text, AutoSize = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };

        private static TextBox MakeTextBox(int width) =>
            new() { Width = width * 14, Margin = new Padding(2, 3, 2, 3) };

        // ── Populate ─────────────────────────────────────────────────────────

        private void PopulateGlobals()
        {
            if (_settings is null) return;
            _globalLowBox!.Text  = _settings.Low.ToString();
            _globalHighBox!.Text = _settings.High.ToString();
            _globalPollBox!.Text = ((int)PollingDefaults.PollingInterval.TotalSeconds).ToString();

            _globalLowBox.TextChanged  += (_, _) => _dirty = true;
            _globalHighBox.TextChanged += (_, _) => _dirty = true;
            _globalPollBox.TextChanged += (_, _) => _dirty = true;
        }

        private void PopulateStartupCheckBox()
        {
            _startupCheckBox!.Checked = StartupHelper.IsRegistered();
            _startupCheckBox.CheckedChanged += (_, _) => _dirty = true;
        }

        private void LoadDeviceRows()
        {
            _deviceListView!.Items.Clear();
            if (_settings is null || _monitor is null) return;

            var devices = _monitor.LastKnownDevices;

            foreach (var d in devices.OrderBy(x => x.Name))
            {
                string displayName = _settings.GetAliasOrNull(d.DeviceId) ?? string.Empty;
                string low         = _settings.TryGetDeviceLow(d.DeviceId, d.Name, out int lo)  ? lo.ToString() : string.Empty;
                string high        = _settings.TryGetDeviceHigh(d.DeviceId, d.Name, out int hi) ? hi.ToString() : string.Empty;
                string poll        = _settings.GetPollIntervalForDevice(d.DeviceId, d.Name)?.ToString() ?? string.Empty;
                bool   ignored     = _settings.IsIgnored(d.DeviceId, d.Name);

                var item = new ListViewItem(d.Name);
                item.SubItems.Add(displayName);
                item.SubItems.Add(low);
                item.SubItems.Add(high);
                item.SubItems.Add(poll);
                item.SubItems.Add(ignored ? "Yes" : "No");
                item.SubItems.Add(d.DeviceId);
                item.Tag = d.DeviceId;

                _deviceListView.Items.Add(item);
            }
        }

        private void WireMonitorEvents()
        {
            if (_monitor is null) return;
            _monitor.DeviceBatteryRead   += OnDeviceBatteryRead;
            _monitor.ManualScanCompleted += OnManualScanCompleted;
        }

        private void UnwireMonitorEvents()
        {
            if (_monitor is null) return;
            _monitor.DeviceBatteryRead   -= OnDeviceBatteryRead;
            _monitor.ManualScanCompleted -= OnManualScanCompleted;
        }

        private void OnDeviceBatteryRead(string name, int? battery)
        {
            if (InvokeRequired) { Invoke(() => OnDeviceBatteryRead(name, battery)); return; }
            UpdateDeviceRow(name, battery);
        }

        private void OnManualScanCompleted(IReadOnlyList<DeviceBatteryInfo> devices)
        {
            if (InvokeRequired) { Invoke(() => OnManualScanCompleted(devices)); return; }
            LoadDeviceRows();
        }

        private void UpdateDeviceRow(string name, int? battery)
        {
            if (_deviceListView is null) return;
            foreach (ListViewItem item in _deviceListView.Items)
            {
                if (item.Text == name)
                {
                    // Battery column not currently shown — placeholder for future expansion.
                    _ = battery;
                    return;
                }
            }
        }

        // ── Editing ──────────────────────────────────────────────────────────

        private void OnCloseClicked(object? sender, EventArgs e)
        {
            if (_dirty)
            {
                var result = _messageBox(
                    this,
                    "You have unsaved changes. Save before closing?",
                    "Unsaved changes",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Cancel) return;
                if (result == DialogResult.Yes) Save();
            }

            Close();
        }

        private void OnSaveClicked(object? sender, EventArgs e) => Save();

        private void Save()
        {
            if (_settings is null) return;

            if (int.TryParse(_globalLowBox!.Text.Trim(),  out int low))  _settings.Low  = low;
            if (int.TryParse(_globalHighBox!.Text.Trim(), out int high)) _settings.High = high;

            SaveDeviceRows();
            ApplyStartupRegistration();

            _settings.Persist();
            _dirty = false;
            _statusLabel!.Text = "Saved.";
        }

        private void SaveDeviceRows()
        {
            if (_settings is null || _deviceListView is null) return;

            foreach (ListViewItem item in _deviceListView.Items)
            {
                string deviceId = item.Tag as string ?? string.Empty;
                if (string.IsNullOrEmpty(deviceId)) continue;

                string alias   = item.SubItems[1].Text.Trim();
                string lowText = item.SubItems[2].Text.Trim();
                string hiText  = item.SubItems[3].Text.Trim();
                string pollTxt = item.SubItems[4].Text.Trim();
                bool   ignored = string.Equals(item.SubItems[5].Text, "Yes", StringComparison.OrdinalIgnoreCase);

                _settings.SetAlias(deviceId, alias.Length > 0 ? alias : null);

                if (int.TryParse(lowText,  out int dLow))  _settings.SetDeviceLow(deviceId, dLow);
                else                                        _settings.ClearDeviceLow(deviceId);

                if (int.TryParse(hiText,   out int dHigh)) _settings.SetDeviceHigh(deviceId, dHigh);
                else                                        _settings.ClearDeviceHigh(deviceId);

                if (int.TryParse(pollTxt,  out int dPoll)) _settings.SetPollInterval(deviceId, dPoll);
                else                                        _settings.ClearPollInterval(deviceId);

                if (ignored) _settings.AddIgnored(deviceId, null);
                else         _settings.RemoveIgnored(deviceId, null);
            }
        }

        private void ApplyStartupRegistration()
        {
            if (_startupCheckBox is null) return;
            if (_startupCheckBox.Checked) StartupHelper.Register();
            else                          StartupHelper.Unregister();
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            UnwireMonitorEvents();
        }
    }
}
