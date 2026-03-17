namespace BTChargeTrayWatcher;

public class BluetoothBatteryMonitor : IDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly NotificationService _notifier;
    private readonly GattBatteryReader _gattReader = new();
    private readonly ClassicBatteryReader _classicReader = new();
    private readonly Dictionary<string, int> _lastKnown = new();
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    public event Action<string, int>? DeviceBatteryRead;
    public event Action<string, int>? DeviceFound;
    public event Action<IReadOnlyList<(string, int)>>? ScanCompleted;

    // Snapshot of last known state — empty until first poll/scan completes
    public IReadOnlyList<(string Name, int Battery)> LastKnownDevices =>
        _lastKnown.Select(kv => (kv.Key, kv.Value)).ToList();

    public bool HasCachedResults => _lastKnown.Count > 0;

    public BluetoothBatteryMonitor(ThresholdSettings settings, NotificationService notifier)
    {
        _settings = settings;
        _notifier = notifier;

        _timer = new System.Threading.Timer(
            async _ => await PollAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(60));
    }

    public async Task<List<(string Name, int Battery)>> ScanNowAsync()
    {
        var results = new List<(string, int)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, battery) in await _gattReader.ReadAllAsync())
        {
            if (!seen.Add(name)) continue;
            DeviceFound?.Invoke(name, battery);
            results.Add((name, battery));
        }

        foreach (var (name, battery) in await _classicReader.ReadAllAsync())
        {
            if (!seen.Add(name)) continue;
            DeviceFound?.Invoke(name, battery);
            results.Add((name, battery));
        }

        // Update cache with fresh results
        foreach (var (name, battery) in results)
            if (battery >= 0) _lastKnown[name] = battery;

        ScanCompleted?.Invoke(results);
        return results;
    }

    public async Task PollAsync()
    {
        if (!await _pollLock.WaitAsync(0)) return;
        try
        {
            var devices = await ScanNowAsync();
            foreach (var (name, battery) in devices)
            {
                if (battery < 0) continue;

                bool known = _lastKnown.TryGetValue(name, out int prev);
                if (!known || prev != battery)
                {
                    _lastKnown[name] = battery;
                    DeviceBatteryRead?.Invoke(name, battery);

                    if (battery <= _settings.Low)
                        _notifier.NotifyLow(name, battery);
                    else if (battery >= _settings.High)
                        _notifier.NotifyHigh(name, battery);
                }
            }
        }
        finally { _pollLock.Release(); }
    }

    public static string BatteryBar(int pct)
    {
        int filled = (int)Math.Round(pct / 10.0);
        return "[" + new string('█', filled) + new string('░', 10 - filled) + "]";
    }

    public void Dispose()
    {
        _timer.Dispose();
        _pollLock.Dispose();
    }
}
