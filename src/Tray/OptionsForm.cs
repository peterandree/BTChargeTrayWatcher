// src/Tray/OptionsForm.cs  — thin binding shell; all logic in ViewModels.
namespace BTChargeTrayWatcher;

public sealed class OptionsForm : Form
{
    public delegate DialogResult MessageBoxHandler(
        IWin32Window? owner, string text, string caption,
        MessageBoxButtons buttons, MessageBoxIcon icon);

    private readonly MessageBoxHandler _messageBoxHandler;

    // ── Controls ──────────────────────────────────────────────────────────────
    private readonly TabControl   tabControl;
    private readonly TabPage      devicesTab;
    private readonly TabPage      notificationsTab;
    private readonly TabPage      generalTab;

    // ntfy controls
    private readonly CheckBox      ntfyEnabledCheck;
    private readonly TextBox       ntfyTopicTextBox;
    private readonly Button        regenerateTopicBtn;
    private readonly Button        sendNtfyTestBtn;

    // general controls
    private readonly NumericUpDown lowNumeric;
    private readonly NumericUpDown highNumeric;
    private readonly NumericUpDown laptopLowNumeric;
    private readonly NumericUpDown laptopHighNumeric;
    private readonly CheckBox      excludeLaptopOverlayCheck;
    private readonly CheckBox      autoStartCheck;

    // devices controls
    private readonly DataGridView devicesGrid;
    private readonly Button       resetAllBtn;

    // ── View models ─────────────────────────────────────────────────────────
    private OptionsViewModel? _optionsVm;
    private DevicesViewModel? _devicesVm;
    // Backing list (underscore) and a non-underscored alias `deviceRows` kept
    // for legacy reflection access in tests and external callers.
    private readonly List<DevicesViewModel.DeviceRow> _deviceRows = [];
    private readonly List<DevicesViewModel.DeviceRow> deviceRows;

    public OptionsForm(MessageBoxHandler? messageBoxHandler = null)
    {
        _messageBoxHandler = messageBoxHandler
            ?? ((owner, text, caption, buttons, icon) =>
                MessageBox.Show(owner, text, caption, buttons, icon));

        Text              = "Options";
        FormBorderStyle   = FormBorderStyle.FixedDialog;
        MaximizeBox       = false;
        MinimizeBox       = false;
        StartPosition     = FormStartPosition.CenterScreen;
        ShowInTaskbar     = false;
        Width             = 600;
        Height            = 400;

        tabControl = new TabControl { Dock = DockStyle.Fill };

        // ── Devices tab ─────────────────────────────────────────────────────────
        devicesTab  = new TabPage("Devices");
        devicesGrid = new DataGridView
        {
            Dock                  = DockStyle.Fill,
            AllowUserToAddRows    = false,
            AllowUserToDeleteRows = false,
            ReadOnly              = false,
            SelectionMode         = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns   = false
        };
        devicesGrid.Columns.Add(new DataGridViewTextBoxColumn  { HeaderText = "Display Name", DataPropertyName = "DisplayName",  Width = 160 });
        devicesGrid.Columns.Add(new DataGridViewTextBoxColumn  { HeaderText = "Low %",        DataPropertyName = "Low",          Width = 60,  ValueType = typeof(int) });
        devicesGrid.Columns.Add(new DataGridViewTextBoxColumn  { HeaderText = "High %",       DataPropertyName = "High",         Width = 60,  ValueType = typeof(int) });
        devicesGrid.Columns.Add(new DataGridViewTextBoxColumn  { HeaderText = "Poll (s)",     DataPropertyName = "PollInterval", Width = 80,  ValueType = typeof(int) });
        devicesGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Excluded",     DataPropertyName = "Excluded",     Width = 70 });
        devicesGrid.Columns.Add(new DataGridViewButtonColumn   { HeaderText = "\u21ba",       Text = "Reset", UseColumnTextForButtonValue = true, Width = 60 });
        devicesTab.Controls.Add(devicesGrid);
        resetAllBtn = new Button { Text = "Reset All Devices", Dock = DockStyle.Bottom, Height = 32 };
        devicesTab.Controls.Add(resetAllBtn);

        // ── Notifications tab ──────────────────────────────────────────────────
        notificationsTab = new TabPage("Notifications");
        var notifLayout  = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = false,
            Padding = new Padding(12), RowCount = 3, ColumnCount = 1
        };
        notifLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        notifLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // ntfy group
        notifLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8)); // spacer
        notifLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // help box

        var ntfyGroup = new GroupBox
        {
            Text    = "Mobile notifications (ntfy.sh)",
            Dock    = DockStyle.Top,
            Padding = new Padding(10),
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        ntfyEnabledCheck = new CheckBox
            { Text = "Enable ntfy notifications", AutoSize = true, Dock = DockStyle.Top };

        // Topic row: label + read-only textbox
        var topicPanel   = new Panel { Dock = DockStyle.Top, Height = 30 };
        var topicLabel   = new Label { Text = "Topic:", AutoSize = true, Width = 40, Dock = DockStyle.Left };
        ntfyTopicTextBox = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        topicPanel.Controls.Add(ntfyTopicTextBox);
        topicPanel.Controls.Add(topicLabel);

        // Button row: fixed-height panel, buttons left-aligned, equal size, 8px gap
        sendNtfyTestBtn    = new Button { Text = "Send ntfy test",   Width = 140, Height = 26, Margin = new Padding(0, 0, 8, 0) };
        regenerateTopicBtn = new Button { Text = "Regenerate topic", Width = 140, Height = 26, Margin = new Padding(0) };
        var ntfyButtonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoSize      = false,
            Height        = 34,                      // explicit height: button (26) + top margin (8)
            Dock          = DockStyle.Top,
            Padding       = new Padding(0, 8, 0, 0), // 8px top gap below topic row
            Margin        = new Padding(0)
        };
        ntfyButtonPanel.Controls.Add(sendNtfyTestBtn);
        ntfyButtonPanel.Controls.Add(regenerateTopicBtn);

        // GroupBox stacks children bottom-up via Dock.Top — add in reverse visual order
        ntfyGroup.Controls.Add(ntfyButtonPanel);
        ntfyGroup.Controls.Add(topicPanel);
        ntfyGroup.Controls.Add(ntfyEnabledCheck);

        notifLayout.Controls.Add(ntfyGroup,             0, 0);
        notifLayout.Controls.Add(new Panel { Height = 8 }, 0, 1);

        var ntfyHelpBox = new GroupBox
        {
            Text    = "ntfy mobile setup instructions",
            Dock    = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var ntfyHelpText = new RichTextBox
        {
            ReadOnly   = true,
            BorderStyle = BorderStyle.None,
            Dock       = DockStyle.Fill,
            BackColor  = SystemColors.Control,
            DetectUrls = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Margin     = new Padding(0),
            Text       = "To receive push notifications on your phone:\n\n"
                       + "1. Install the ntfy app on your device:\n"
                       + "   \u2022 Android: https://docs.ntfy.sh/subscribe/phone/\n"
                       + "   \u2022 iPhone: https://docs.ntfy.sh/subscribe/phone/\n"
                       + "2. Open the app and subscribe to your topic (shown above) using server ntfy.sh.\n"
                       + "3. For more info, see: https://ntfy.sh"
        };
        ntfyHelpText.LinkClicked += (s, e) =>
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = e.LinkText, UseShellExecute = true });
        ntfyHelpBox.Controls.Add(ntfyHelpText);
        notifLayout.Controls.Add(ntfyHelpBox, 0, 2);

        notificationsTab.Controls.Add(notifLayout);

        // ── General tab ─────────────────────────────────────────────────────────
        generalTab = new TabPage("General");
        var generalPanel = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 5,
            AutoSize    = true,
            Padding     = new Padding(16)
        };
        generalPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        generalPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        generalPanel.Controls.Add(new Label { Text = "Global Low %",  Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        lowNumeric = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 60, Anchor = AnchorStyles.Left };
        generalPanel.Controls.Add(lowNumeric, 1, 0);
        generalPanel.Controls.Add(new Label { Text = "Global High %", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
        highNumeric = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 60, Anchor = AnchorStyles.Left };
        generalPanel.Controls.Add(highNumeric, 1, 1);
        generalPanel.Controls.Add(new Label { Text = "Laptop Low %",  Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
        laptopLowNumeric = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 60, Anchor = AnchorStyles.Left };
        generalPanel.Controls.Add(laptopLowNumeric, 1, 2);
        generalPanel.Controls.Add(new Label { Text = "Laptop High %", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 3);
        laptopHighNumeric = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 60, Anchor = AnchorStyles.Left };
        generalPanel.Controls.Add(laptopHighNumeric, 1, 3);
        excludeLaptopOverlayCheck = new CheckBox
            { Text = "Exclude laptop from tray icon overlay", Anchor = AnchorStyles.Left, AutoSize = true };
        autoStartCheck = new CheckBox
            { Text = "Start automatically with Windows", Anchor = AnchorStyles.Left, AutoSize = true };
        generalPanel.Controls.Add(autoStartCheck, 0, 4);
        generalPanel.SetColumnSpan(autoStartCheck, 2);
        generalPanel.Controls.Add(excludeLaptopOverlayCheck, 0, 5);
        generalPanel.SetColumnSpan(excludeLaptopOverlayCheck, 2);
        generalTab.Controls.Add(generalPanel);

        tabControl.TabPages.Add(devicesTab);
        tabControl.TabPages.Add(notificationsTab);
        tabControl.TabPages.Add(generalTab);
        Controls.Add(tabControl);
        // Keep the legacy `deviceRows` field referencing the same list instance
        // so reflection-based callers (tests) can find and modify rows.
        deviceRows = _deviceRows;
    }

    // ── Initialize: inject VMs, bind controls ─────────────────────────────────

    public void Initialize(
        ThresholdSettings settings,
        BluetoothBatteryMonitor monitor,
        INotificationService? notifier = null)
    {
        _optionsVm = new OptionsViewModel(settings, notifier);
        _devicesVm = new DevicesViewModel(settings, monitor);

        BindGeneralTab();
        BindNotificationsTab();
        BindDevicesTab();
    }

    // ── General tab binding ────────────────────────────────────────────────────

    private void BindGeneralTab()
    {
        var vm = _optionsVm!;
        lowNumeric.Value          = vm.GlobalLow;
        highNumeric.Value         = vm.GlobalHigh;
        laptopLowNumeric.Value    = vm.LaptopLow;
        laptopHighNumeric.Value   = vm.LaptopHigh;
        excludeLaptopOverlayCheck.Checked = vm.ExcludeLaptopFromTrayIconOverlay;
        autoStartCheck.Checked = vm.AutoStartEnabled;

        lowNumeric.ValueChanged += (_, _) =>
        {
            try { vm.GlobalLow = (int)lowNumeric.Value; }
            catch (ArgumentOutOfRangeException ex)
            {
                ShowError(ex.Message, "Invalid threshold");
                lowNumeric.Value = vm.GlobalLow;
            }
        };
        highNumeric.ValueChanged += (_, _) =>
        {
            try { vm.GlobalHigh = (int)highNumeric.Value; }
            catch (ArgumentOutOfRangeException ex)
            {
                ShowError(ex.Message, "Invalid threshold");
                highNumeric.Value = vm.GlobalHigh;
            }
        };
        laptopLowNumeric.ValueChanged += (_, _) =>
        {
            try { vm.LaptopLow = (int)laptopLowNumeric.Value; }
            catch (ArgumentOutOfRangeException ex)
            {
                ShowError(ex.Message, "Invalid threshold");
                laptopLowNumeric.Value = vm.LaptopLow;
            }
        };
        laptopHighNumeric.ValueChanged += (_, _) =>
        {
            try { vm.LaptopHigh = (int)laptopHighNumeric.Value; }
            catch (ArgumentOutOfRangeException ex)
            {
                ShowError(ex.Message, "Invalid threshold");
                laptopHighNumeric.Value = vm.LaptopHigh;
            }
        };
        excludeLaptopOverlayCheck.CheckedChanged += (_, _) =>
            vm.ExcludeLaptopFromTrayIconOverlay = excludeLaptopOverlayCheck.Checked;
        autoStartCheck.CheckedChanged += (_, _) =>
            vm.AutoStartEnabled = autoStartCheck.Checked;
    }

    // ── Notifications tab binding ───────────────────────────────────────────────

    private void BindNotificationsTab()
    {
        var vm = _optionsVm!;
        ntfyEnabledCheck.Checked = vm.NtfyEnabled;
        ntfyTopicTextBox.Text    = vm.NtfyTopic;

        ntfyEnabledCheck.CheckedChanged += (_, _) =>
            vm.NtfyEnabled = ntfyEnabledCheck.Checked;

        regenerateTopicBtn.Click += (_, _) =>
        {
            string topic = vm.RegenerateTopic();
            ntfyTopicTextBox.Text    = topic;
            ntfyEnabledCheck.Checked = false;
            _messageBoxHandler(this,
                $"New topic generated:\n\n{topic}\n\nSubscribe to this topic in the ntfy app on your phone using server ntfy.sh, then enable the integration.",
                "Mobile notifications \u2014 new topic",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        };

        sendNtfyTestBtn.Click += async (_, _) =>
        {
            bool ok = await vm.SendTestAsync().ConfigureAwait(false);
            string msg = ok
                ? "Test notification sent. Check your phone."
                : "Failed to send test notification. Check your internet connection and verify the topic is correct.";
            void Show() => _messageBoxHandler(this, msg,
                "Mobile notifications \u2014 test",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            if (InvokeRequired) Invoke((Action)Show); else Show();
        };
    }

    // ── Devices tab binding ─────────────────────────────────────────────────────────

    private void BindDevicesTab()
    {
        ReloadDeviceRows();

        devicesGrid.CellValueChanged   += DevicesGrid_CellValueChanged;
        devicesGrid.CellContentClick   += DevicesGrid_CellContentClick;
        devicesGrid.DataError          += (_, e) =>
        {
            try { ShowError("Invalid value entered. Please enter a numeric value.", "Invalid input"); }
            catch { /* ignore secondary fault */ }
            ReloadDeviceRows();
            e.ThrowException = false;
        };
        devicesGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (!devicesGrid.IsCurrentCellDirty) return;
            try
            {
                if (devicesGrid.CurrentCell?.OwningColumn is DataGridViewCheckBoxColumn)
                    devicesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
            catch { /* swallow UI inspection errors */ }
        };
        resetAllBtn.Click += ResetAllBtn_Click;
    }

    private void ReloadDeviceRows()
    {
        var newRows = _devicesVm!.BuildRows().ToList();
        _deviceRows.Clear();
        _deviceRows.AddRange(newRows);
        devicesGrid.DataSource = null;
        devicesGrid.DataSource = _deviceRows;
    }

    private void DevicesGrid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        var vm = _devicesVm;
        if (vm is null || e.RowIndex < 0 || e.RowIndex >= _deviceRows.Count) return;
        var row = _deviceRows[e.RowIndex];
        var col = devicesGrid.Columns[e.ColumnIndex].DataPropertyName;

        string? err = col switch
        {
            "DisplayName"  => vm.SetDisplayName(row, row.DisplayName),
            "Low"          => vm.SetLow(row, row.Low),
            "High"         => vm.SetHigh(row, row.High),
            "PollInterval" => vm.SetPollInterval(row, row.PollInterval),
            _              => null
        };

        if (col == "Excluded")
            vm.SetExcluded(row, row.Excluded);

        if (err is not null)
        {
            ShowError(err, "Invalid threshold");
            ReloadDeviceRows();
        }
    }

    private void DevicesGrid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (devicesGrid.Columns[e.ColumnIndex] is not DataGridViewButtonColumn) return;
        _devicesVm?.ResetDevice(_deviceRows[e.RowIndex]);
        ReloadDeviceRows();
    }

    private void ResetAllBtn_Click(object? sender, EventArgs e)
    {
        var vm = _devicesVm;
        if (vm is null) return;
        var confirm = _messageBoxHandler(this,
            "Reset all device thresholds and poll intervals to defaults?",
            "Confirm Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;
        vm.ResetAll(_deviceRows);
        ReloadDeviceRows();
    }

    private void ShowError(string message, string caption) =>
        _messageBoxHandler(this, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
