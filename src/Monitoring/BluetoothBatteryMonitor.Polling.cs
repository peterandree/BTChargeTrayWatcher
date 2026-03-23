using System.Collections.Concurrent;
using System.Threading;

namespace BTChargeTrayWatcher;

public partial class BluetoothBatteryMonitor
{
    private enum BatteryAlertState
    {
        Normal = 0,
        Low = 1,
        High = 2
    }

    private readonly ConcurrentDictionary<string, BatteryAlertState> _alertStates =
        new(StringComparer.OrdinalIgnoreCase);

    private void OnTimerTick()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested)
            return;

        StartTrackedTask(ct => SafePollAsync(ct));
    }

    private async Task SafePollAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PollAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposeStarted || _isDisposed) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] PollAsync fault: {ex}");
        }
    }

    public Task PollAsync() => PollAsync(_shutdownCts.Token);

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _pollLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool thresholdsChanged = Interlocked.Exchange(ref _thresholdsChanged, 0) == 1;
            if (thresholdsChanged)
            {
                _alertStates.Clear();
            }

            // Snapshot keyed by DeviceId
            var snapshot = new Dictionary<string, DeviceBatteryInfo>(_lastKnown, StringComparer.OrdinalIgnoreCase);
            var devices = await QuietReadAsync(cancellationToken).ConfigureAwait(false);

            var currentValid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (device.Battery < 0) continue;

                currentValid.Add(device.DeviceId);

                snapshot.TryGetValue(device.DeviceId, out var prevInfo);
                int prev = prevInfo?.Battery ?? 0;
                bool isNew = prevInfo is null;

                _lastKnown[device.DeviceId] = device;
                DeviceBatteryRead?.Invoke(device.Name, device.Battery);

                BatteryAlertState previousState = _alertStates.TryGetValue(device.DeviceId, out var existingState)
                    ? existingState
                    : ClassifyBatteryState(device.DeviceId, device.Name, prev, BatteryAlertState.Normal);

                BatteryAlertState currentState = ClassifyBatteryState(device.DeviceId, device.Name, device.Battery, previousState);

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

                if (prev == device.Battery)
                    continue;

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
                    _lastKnown.TryRemove(id, out _);
                    _alertStates.TryRemove(id, out _);
                }
            }

            ScanCompleted?.Invoke(_lastKnown.Values.ToList());
        }
        finally
        {
            _pollLock.Release();
        }
    }

    // name is passed separately since ThresholdSettings and IgnoredDevices are keyed by display name
    private BatteryAlertState ClassifyBatteryState(string deviceId, string name, int battery, BatteryAlertState previousState)
    {
        if (battery < 0 || _settings.IgnoredDevices.Contains(name))
            return BatteryAlertState.Normal;

        int low = _settings.GetLow(name);
        int high = _settings.GetHigh(name);

        const int hysteresis = 2;

        if (battery <= low)
            return BatteryAlertState.Low;

        if (battery >= high)
            return BatteryAlertState.High;

        if (previousState == BatteryAlertState.Low && battery <= low + hysteresis)
            return BatteryAlertState.Low;

        if (previousState == BatteryAlertState.High && battery >= high - hysteresis)
            return BatteryAlertState.High;

        return BatteryAlertState.Normal;
    }
}
