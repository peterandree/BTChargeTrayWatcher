// src/Tray/ViewModels/ScanViewModel.cs
// Presentation logic for ScanWindow: scan state machine, countdown timer,
// device list model. No WinForms dependency — fully unit-testable.
namespace BTChargeTrayWatcher;

internal sealed class ScanViewModel : IDisposable
{
    private readonly ThresholdSettings _settings;
    private const int AutoRefreshIntervalSeconds = 30;

    // ── Scan state ───────────────────────────────────────────────────────────

    public bool ScanComplete   { get; private set; }
    public bool AutoRefreshOn  { get; set; } = true;
    public int  Countdown      { get; private set; } = AutoRefreshIntervalSeconds;

    // ── Device list model ─────────────────────────────────────────────────────

    public sealed class DeviceItem
    {
        public string  DeviceId    { get; init; } = string.Empty;
        public string  Name        { get; init; } = string.Empty;
        public string  BatteryText { get; init; } = string.Empty;
        public string  PollText    { get; init; } = string.Empty;
        public string  Bar         { get; init; } = string.Empty;
        public string  Tooltip     { get; init; } = string.Empty;
        public bool    IsIgnored   { get; init; }
        public bool    HasBattery  { get; init; }
        public string  TrendArrow  { get; init; } = string.Empty;
        public bool    TrendUp     { get; init; }
        public bool    TrendDown   { get; init; }
    }

    // Raised on the caller’s thread (the UI timer tick or scan event handler).
    public event Action<DeviceItem>?               DeviceUpserted;
    public event Action<IReadOnlyList<DeviceItem>>? ScanCompleted;
    public event Action<string>?                   StatusChanged;
    public event Action?                           AutoRefreshTriggered;
    public event Action?                           ScanRestarted;

    private readonly Dictionary<string, int> _previousBattery   = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _currentScanValues = new(StringComparer.OrdinalIgnoreCase);

    // Populated by SetShownItems() before OnScanComplete() so the dedup guard
    // knows which rows are already visible in the ListView.
    private HashSet<string> _shownIds   = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _shownNames = new(StringComparer.OrdinalIgnoreCase);

    // ── Timer ───────────────────────────────────────────────────────────────────

    private System.Threading.Timer? _timer;

    public void StartTimer()
    {
        _timer ??= new System.Threading.Timer(_ => Tick(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void StopTimer() => _timer?.Change(Timeout.Infinite, Timeout.Infinite);

    private void Tick()
    {
        if (!AutoRefreshOn) return;
        if (ScanComplete)
        {
            Countdown--;
            if (Countdown <= 0)
            {
                Countdown = AutoRefreshIntervalSeconds;
                AutoRefreshTriggered?.Invoke();
            }
        }
        else
        {
            Countdown = AutoRefreshIntervalSeconds;
        }
        EmitStatus();
    }

    // ── Scan lifecycle ─────────────────────────────────────────────────────────

    public void OnScanStarted()
    {
        ScanComplete = false;
        Countdown    = AutoRefreshIntervalSeconds;
        _currentScanValues.Clear();
        ScanRestarted?.Invoke();
        StatusChanged?.Invoke("Scanning for Bluetooth devices...");
    }

    public DeviceItem OnDeviceFound(string deviceId, string name, int? battery, bool? isCharging = null)
    {
        bool isIgnored = _settings.IsIgnored(deviceId, name);
        string arrow   = string.Empty;
        bool trendUp = false, trendDown = false;

        if (!isIgnored && battery.HasValue)
        {
            arrow = BatteryTrendHelper.GetArrow(
                _previousBattery.TryGetValue(deviceId, out var prev) ? prev : null,
                battery.Value);
            trendUp   = arrow == "\u2191";
            trendDown = arrow == "\u2193";
            _currentScanValues[deviceId] = battery.Value;
        }

        string batteryText = isIgnored ? "-"
            : battery.HasValue
                ? (arrow.Length > 0
                    ? $"{BatteryDisplay.FormatBattery(battery.Value, isCharging)} {arrow}"
                    : BatteryDisplay.FormatBattery(battery.Value, isCharging))
                : "N/A";

        int poll = _settings.GetPollIntervalForDevice(deviceId, name)
                   ?? (int)PollingDefaults.PollingInterval.TotalSeconds;

        var item = new DeviceItem
        {
            DeviceId    = deviceId,
            Name        = name,
            BatteryText = batteryText,
            PollText    = isIgnored ? "[Ignored]" : poll.ToString(),
            Bar         = (!isIgnored && battery.HasValue) ? BatteryDisplay.Bar(battery.Value) : string.Empty,
            Tooltip     = arrow.Length > 0 ? $"{arrow} {name}" : name,
            IsIgnored   = isIgnored,
            HasBattery  = battery.HasValue && !isIgnored,
            TrendArrow  = arrow,
            TrendUp     = trendUp,
            TrendDown   = trendDown
        };

        DeviceUpserted?.Invoke(item);
        return item;
    }

    /// <summary>
    /// Must be called by the shell immediately before <see cref="OnScanComplete"/>,
    /// passing the IDs and names of rows already visible in the ListView.
    /// OnScanComplete uses these sets to suppress duplicate “sleeping” rows.
    /// </summary>
    public void SetShownItems(IEnumerable<DeviceIdentifier> existingItems)
    {
        _shownIds.Clear();
        _shownNames.Clear();
        foreach (var info in existingItems)
        {
            _shownIds.Add(info.Id);
            _shownNames.Add(info.Name);
        }
    }

    public IReadOnlyList<DeviceItem> OnScanComplete(IReadOnlyList<WatchedDevice> trackedDevices)
    {
        ScanComplete = true;

        foreach (var kvp in _currentScanValues)
            _previousBattery[kvp.Key] = kvp.Value;
        _currentScanValues.Clear();

        var extras = new List<DeviceItem>();

        foreach (var device in trackedDevices)
        {
            if (_shownIds.Contains(device.DeviceId)) continue;
            if (_shownNames.Contains(device.Name))   continue;

            string reason = device.IsConnected ? "[No battery service]" : "[Sleeping / not connected]";
            extras.Add(new DeviceItem
            {
                DeviceId    = device.DeviceId,
                Name        = device.Name,
                BatteryText = "-",
                PollText    = reason,
                IsIgnored   = true
            });
            // Register in shown sets so duplicates within trackedDevices are also suppressed.
            _shownIds.Add(device.DeviceId);
            _shownNames.Add(device.Name);
        }

        ScanCompleted?.Invoke(extras);
        EmitStatus();
        return extras;
    }

    private void EmitStatus()
    {
        if (!ScanComplete) return;
        string text = AutoRefreshOn
            ? $"Scan complete. Auto-refresh in {Countdown}s."
            : "Scan complete. Auto-refresh is off.";
        StatusChanged?.Invoke(text);
    }

    public ScanViewModel(ThresholdSettings settings)
    {
        _settings = settings;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
