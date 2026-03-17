namespace BTChargeTrayWatcher;

public partial class BluetoothBatteryMonitor : IDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly NotificationService _notifier;
    private readonly GattBatteryReader _gattReader = new();
    private readonly ClassicBatteryReader _classicReader = new();
    private readonly Dictionary<string, int> _lastKnown = [];
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    public event Action<string, int>? DeviceBatteryRead;
    public event Action<string, int>? DeviceFound;
    public event Action<IReadOnlyList<(string, int)>>? ScanCompleted;
    public event Action? ScanStarted;

    public bool IsScanning { get; private set; }

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

    // User-initiated scan: sets IsScanning, fires ScanStarted / DeviceFound / ScanCompleted.
    public async Task<List<(string Name, int Battery)>> ScanNowAsync()
    {
        IsScanning = true;
        ScanStarted?.Invoke();

        List<(string, int)> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        try
        {
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
        }
        finally
        {
            IsScanning = false;
            ScanCompleted?.Invoke(results);
        }

        return results;
    }

    // Silent read used by the background timer: does NOT touch IsScanning or any UI events.
    private async Task<List<(string Name, int Battery)>> QuietReadAsync()
    {
        List<(string, int)> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, battery) in await _gattReader.ReadAllAsync())
        {
            if (!seen.Add(name)) continue;
            results.Add((name, battery));
        }

        foreach (var (name, battery) in await _classicReader.ReadAllAsync())
        {
            if (!seen.Add(name)) continue;
            results.Add((name, battery));
        }

        return results;
    }

    public async Task PollAsync()
    {
        if (!await _pollLock.WaitAsync(0)) return;
        try
        {
            var snapshot = new Dictionary<string, int>(_lastKnown);
            var devices = await QuietReadAsync();

            foreach (var (name, battery) in devices)
            {
                if (battery < 0) continue;

                snapshot.TryGetValue(name, out int prev);
                bool isNew = !snapshot.ContainsKey(name);

                _lastKnown[name] = battery;
                DeviceBatteryRead?.Invoke(name, battery);

                if (isNew) continue;
                if (prev == battery) continue;

                if (battery <= _settings.Low && prev > _settings.Low)
                    _notifier.NotifyLow(name, battery);
                else if (battery >= _settings.High && prev < _settings.High)
                    _notifier.NotifyHigh(name, battery);
            }
        }
        finally { _pollLock.Release(); }
    }

    public static string BatteryBar(int pct)
    {
        int filled = (int)Math.Round(pct / 10.0);
        return "[" + new string('\u2588', filled) + new string('\u2591', 10 - filled) + "]";
    }

    public void Dispose()
    {
        _timer.Dispose();
        _pollLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
