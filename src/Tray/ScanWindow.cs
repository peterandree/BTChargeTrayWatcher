// src/Tray/ScanWindow.cs  — thin binding shell; all logic in ScanViewModel.
namespace BTChargeTrayWatcher;

public partial class ScanWindow : Form
{
    private readonly ScanViewModel _vm;
    private readonly ListView      _list;
    private readonly Label         _status;
    private readonly ProgressBar   _progress;
    private readonly Button        _closeBtn;
    private readonly CheckBox      _autoRefreshCheckBox;

    // ScanCoordinator subscribes to this to trigger a rescan.
    public event EventHandler? AutoRefreshRequested;

    public ScanWindow(ThresholdSettings settings)
    {
        _vm = new ScanViewModel(settings);

        Text            = "BT Battery Scan";
        ClientSize      = new Size(900, 600);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = true;
        MinimizeBox     = true;
        SizeGripStyle   = SizeGripStyle.Show;
        MinimumSize     = new Size(700, 450);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12),
            ColumnCount = 1, RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _status   = new Label   { Text = "Scanning for Bluetooth devices...", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        _progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Margin = new Padding(0, 0, 0, 8) };

        _list = new ListView
        {
            View = View.Details, Dock = DockStyle.Fill,
            FullRowSelect = true, GridLines = true,
            ShowItemToolTips = true, Margin = new Padding(0, 0, 0, 8)
        };
        _list.Columns.Add("Device",   420);
        _list.Columns.Add("Battery",  100);
        _list.Columns.Add("Poll (s)",  80);
        _list.Columns.Add("Level",    160);

        _closeBtn = new Button
        {
            Text = "Close", Size = new Size(140, 32),
            AutoSize = false, Margin = new Padding(0, 8, 0, 0),
            UseVisualStyleBackColor = true
        };
        _closeBtn.Click += (_, _) => Close();

        _autoRefreshCheckBox = new CheckBox
        {
            Text = "Auto-refresh", Checked = true, AutoSize = true,
            Dock = DockStyle.Left, Margin = new Padding(0, 0, 8, 0)
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            AutoSize = false, Height = 44, WrapContents = false, Padding = new Padding(0)
        };
        buttonPanel.Controls.Add(_closeBtn);
        buttonPanel.Controls.Add(_autoRefreshCheckBox);

        layout.Controls.Add(_status,      0, 0);
        layout.Controls.Add(_list,        0, 1);
        layout.Controls.Add(_progress,    0, 2);
        layout.Controls.Add(buttonPanel,  0, 3);
        Controls.Add(layout);

        Resize += (_, _) => AdjustColumns();

        // ── VM event bindings ─────────────────────────────────────────────────────

        _vm.StatusChanged  += text   => SafeInvoke(() => _status.Text = text);
        _vm.ScanRestarted  += ()     => SafeInvoke(() =>
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.Value = 0;
        });
        _vm.DeviceUpserted += item   => SafeInvoke(() => UpsertListItem(item));
        _vm.ScanCompleted  += extras => SafeInvoke(() =>
        {
            foreach (var item in extras)
                AppendListItem(item);
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Value = 100;
        });
        _vm.AutoRefreshTriggered += () =>
            SafeInvoke(() => AutoRefreshRequested?.Invoke(this, EventArgs.Empty));

        // ── UI → VM bindings ──────────────────────────────────────────────────────

        _autoRefreshCheckBox.CheckedChanged += (_, _) =>
        {
            _vm.AutoRefreshOn = _autoRefreshCheckBox.Checked;
            if (_vm.AutoRefreshOn)
                _vm.StartTimer();
            else
                _vm.StopTimer();
        };

        Shown      += (_, _) => _vm.StartTimer();
        FormClosed += (_, _) => { _vm.StopTimer(); _vm.Dispose(); };
    }

    // ── Public scan surface (called by ScanCoordinator) ───────────────────────────

    public void OnDeviceFound(string deviceId, string name, int? battery, bool? isCharging = null) =>
        _vm.OnDeviceFound(deviceId, name, battery, isCharging);

    internal void OnScanStarted() => _vm.OnScanStarted();

    internal void OnScanComplete(IReadOnlyList<WatchedDevice> trackedDevices)
    {
        // Snapshot current ListView state into the VM before it builds the extras list.
        _vm.SetShownItems(_list.Items
            .Cast<ListViewItem>()
            .Select(i => (id: i.Tag as string ?? string.Empty, name: i.Text)));

        _vm.OnScanComplete(trackedDevices);
    }

    // ── List view helpers ───────────────────────────────────────────────────────────

    private void UpsertListItem(ScanViewModel.DeviceItem item)
    {
        foreach (ListViewItem lvi in _list.Items)
        {
            if (lvi.Tag is string id &&
                string.Equals(id, item.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                ApplyToListItem(lvi, item);
                return;
            }
        }
        AppendListItem(item);
    }

    private void AppendListItem(ScanViewModel.DeviceItem item)
    {
        var lvi = new ListViewItem(item.Name) { Tag = item.DeviceId };
        lvi.SubItems.Add(item.BatteryText);
        lvi.SubItems.Add(item.PollText);
        lvi.SubItems.Add(item.Bar);
        lvi.ToolTipText = item.Tooltip;
        if (item.IsIgnored) lvi.ForeColor = Color.Gray;
        _list.Items.Add(lvi);
    }

    private static void ApplyToListItem(ListViewItem lvi, ScanViewModel.DeviceItem item)
    {
        lvi.SubItems[1].Text      = item.BatteryText;
        lvi.SubItems[1].ForeColor = item.TrendUp   ? Color.Green
                                  : item.TrendDown ? Color.Red
                                  : SystemColors.WindowText;
        if (lvi.SubItems.Count > 2) lvi.SubItems[2].Text = item.PollText;
        lvi.SubItems[3].Text = item.Bar;
        lvi.ToolTipText      = item.Tooltip;
        lvi.ForeColor        = item.IsIgnored ? Color.Gray : SystemColors.WindowText;
    }

    private void AdjustColumns()
    {
        if (_list.Columns.Count < 3) return;
        int padding   = SystemInformation.VerticalScrollBarWidth + 16;
        int available = _list.ClientSize.Width
                      - _list.Columns[1].Width
                      - _list.Columns[2].Width
                      - padding;
        if (available < 400) available = 400;
        _list.Columns[0].Width = available;
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(new Action(() => { if (!IsDisposed) action(); }));
        else action();
    }
}
