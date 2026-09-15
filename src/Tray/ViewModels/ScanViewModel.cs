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

    // ── Deep scan (ADR-019, issue #165) ──────────────────────────────────────

    /// <summary>
    /// Label of the diagnostic scan action. The wording is fixed by ADR-019 §1.
    /// </summary>
    public const string DeepScanButtonText = "Deep scan (diagnostic)";

    /// <summary>
    /// The one-line warning the action must present, verbatim from ADR-019 §1. Kept here (rather
    /// than inline in the form) so a test can assert it has not drifted from the ADR.
    /// </summary>
    public const string DeepScanWarningText =
        "This scan may temporarily increase Bluetooth activity; recommended for troubleshooting only.";

    /// <summary>True while a deep scan is running: the action is disabled and Cancel is available.</summary>
    public bool DeepScanActive { get; private set; }

    /// <summary>
    /// True while the status line is showing the last deep-scan result. The 1 s auto-refresh tick
    /// rewrites the status line, so the result has to outrank the countdown until the next scan
    /// starts — otherwise the summary is unreadable a second after it appears.
    /// </summary>
    internal bool IsDeepScanSummaryPending { get; private set; }

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

    /// <summary>Raised when a deep scan starts or ends, so the shell can toggle its controls.</summary>
    public event Action<bool>?                     DeepScanActiveChanged;

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
        IsDeepScanSummaryPending = false;
        _currentScanValues.Clear();
        ScanRestarted?.Invoke();

        // A manual scan raises this event too, so a deep run must keep its own status line: the
        // generic text would drop the budget and the cancel hint the user just confirmed.
        StatusChanged?.Invoke(DeepScanActive
            ? DeepScanRunningText()
            : "Scanning for Bluetooth devices...");
    }

    // ── Deep scan lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// A deep scan started. Called by the shell once the user has confirmed the warning, so the
    /// status line explains the run and its limit instead of leaving the previous result on screen.
    /// </summary>
    public void OnDeepScanStarted()
    {
        DeepScanActive           = true;
        IsDeepScanSummaryPending = false;
        DeepScanActiveChanged?.Invoke(true);
        StatusChanged?.Invoke(DeepScanRunningText());
    }

    private static string DeepScanRunningText() =>
        $"Deep scan running — up to {PollingDefaults.DeepScanTimeBudget.TotalSeconds:0} s. Press Cancel to stop early.";

    /// <summary>
    /// A deep scan ended; shows the outcome summary (ADR-019 §3) and releases the single-run slot.
    /// </summary>
    public void OnDeepScanCompleted(DeepScanResult result)
    {
        DeepScanActive           = false;
        IsDeepScanSummaryPending = true;
        DeepScanActiveChanged?.Invoke(false);
        StatusChanged?.Invoke(
            DeepScanPolicy.Describe(result.Outcome, result.DevicesFound, result.DevicesWithBattery));
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

        // A deep-scan summary outranks the countdown: it is the answer to an explicit user action.
        if (IsDeepScanSummaryPending) return;
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
