using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

internal sealed class PollingOrchestrator : IDisposable
{
    internal enum BatteryAlertState { Normal = 0, Low = 1, High = 2 }

    private readonly ThresholdSettings _settings;
    private readonly NotificationService _notifier;
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown;
    private readonly TaskTracker _tracker;
    private readonly Func<CancellationToken, Task<List<DeviceBatteryInfo>>> _readDevices;
    private readonly CancellationToken _shutdownToken;
    private readonly Action<string, int> _onBatteryRead;
    private readonly Action<IReadOnlyList<DeviceBatteryInfo>> _onScanCompleted;

    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly ConcurrentDictionary<string, BatteryAlertState> _alertStates =
        new(StringComparer.OrdinalIgnoreCase);

    private const int MissCountThreshold = 3;
    private readonly ConcurrentDictionary<string, int> _missCount =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile int _thresholdsChanged;
    private volatile bool _disposed;

    internal SemaphoreSlim PollLock => _pollLock;

    public PollingOrchestrator(
        ThresholdSettings settings,
        NotificationService notifier,
        ConcurrentDictionary<string, DeviceBatteryInfo> lastKnown,
        TaskTracker tracker,
        Func<CancellationToken, Task<List<DeviceBatteryInfo>>> readDevices,
        CancellationToken shutdownToken,
        Action<string, int> onBatteryRead,
        Action<IReadOnlyList<DeviceBatteryInfo>> onScanCompleted)
    {
        _settings = settings;
        _notifier = notifier;
        _lastKnown = lastKnown;
        _tracker = tracker;
        _readDevices = readDevices;
        _shutdownToken = shutdownToken;
        _onBatteryRead = onBatteryRead;
        _onScanCompleted = onScanCompleted;
    }

    public void OnTimerTick()
    {
        if (_disposed || _shutdownToken.IsCancellationRequested) return;
        _tracker.Start(ct => SafePollAsync(ct), _shutdownToken);
    }

    public void SignalThresholdsChanged()
    {
        if (_disposed || _shutdownToken.IsCancellationRequested) return;
        Interlocked.Exchange(ref _thresholdsChanged, 1);
        _tracker.Start(ct => SafePollAsync(ct), _shutdownToken);
    }

    private async Task SafePollAsync(CancellationToken ct)
    {
        try { await PollAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] PollAsync fault: {ex}");
        }
    }

    public Task PollAsync() => PollAsync(_shutdownToken);

    public async Task PollAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _pollLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            bool thresholdsChanged = Interlocked.Exchange(ref _thresholdsChanged, 0) == 1;
            if (thresholdsChanged)
            {
                _alertStates.Clear();
                _missCount.Clear();
            }

            var snapshot = new Dictionary<string, DeviceBatteryInfo>(
                _lastKnown, StringComparer.OrdinalIgnoreCase);

            var devices = await _readDevices(ct).ConfigureAwait(false);
            var currentValid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                ct.ThrowIfCancellationRequested();
                if (device.Battery < 0) continue;

                currentValid.Add(device.DeviceId);
                snapshot.TryGetValue(device.DeviceId, out var prevInfo);
                int prev = prevInfo?.Battery ?? 0;
                bool isNew = prevInfo is null;

                _lastKnown[device.DeviceId] = device;
                _missCount[device.DeviceId] = 0;
                _onBatteryRead(device.Name, device.Battery);

                BatteryAlertState previousState = _alertStates.TryGetValue(device.DeviceId, out var es)
                    ? es
                    : ClassifyBatteryState(device.DeviceId, device.Name, prev, BatteryAlertState.Normal);

                BatteryAlertState currentState =
                    ClassifyBatteryState(device.DeviceId, device.Name, device.Battery, previousState);

                if (_settings.IgnoredDevices.Contains(device.Name))
                {
                    _alertStates[device.DeviceId] = BatteryAlertState.Normal;
                    continue;
                }

                if (isNew || thresholdsChanged || !_alertStates.ContainsKey(device.DeviceId))
                {
                    if (currentState == BatteryAlertState.Low)
                        _notifier.NotifyLow(device.Name, device.Battery);
                    else if (currentState == BatteryAlertState.High)
                        _notifier.NotifyHigh(device.Name, device.Battery);

                    _alertStates[device.DeviceId] = currentState;
                    continue;
                }

                if (prev == device.Battery) continue;

                if (previousState != currentState)
                {
                    if (currentState == BatteryAlertState.Low)
                        _notifier.NotifyLow(device.Name, device.Battery);
                    else if (currentState == BatteryAlertState.High)
                        _notifier.NotifyHigh(device.Name, device.Battery);
                }

                _alertStates[device.DeviceId] = currentState;
            }

            foreach (var id in snapshot.Keys)
            {
                if (!currentValid.Contains(id))
                {
                    int misses = _missCount.AddOrUpdate(id, 1, (_, prev) => prev + 1);
                    if (misses >= MissCountThreshold)
                    {
                        _lastKnown.TryRemove(id, out _);
                        _alertStates.TryRemove(id, out _);
                        _missCount.TryRemove(id, out _);
                    }
                }
            }

            _onScanCompleted(_lastKnown.Values.ToList());
        }
        finally
        {
            _pollLock.Release();
        }
    }

    internal void UpdateAlertState(string deviceId, string name, int battery)
    {
        BatteryAlertState existing = _alertStates.TryGetValue(deviceId, out var s)
            ? s : BatteryAlertState.Normal;
        _alertStates[deviceId] = ClassifyBatteryState(deviceId, name, battery, existing);
    }

    internal BatteryAlertState ClassifyBatteryState(
        string deviceId, string name, int battery, BatteryAlertState previousState)
    {
        if (battery < 0 || _settings.IgnoredDevices.Contains(name))
            return BatteryAlertState.Normal;

        int low = _settings.GetLow(name);
        int high = _settings.GetHigh(name);
        const int hysteresis = 2;

        if (battery <= low) return BatteryAlertState.Low;
        if (battery >= high) return BatteryAlertState.High;

        if (previousState == BatteryAlertState.Low && battery <= low + hysteresis)
            return BatteryAlertState.Low;
        if (previousState == BatteryAlertState.High && battery >= high - hysteresis)
            return BatteryAlertState.High;

        return BatteryAlertState.Normal;
    }

    public void Dispose()
    {
        _disposed = true;
        _pollLock.Dispose();
    }
}
