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

            var snapshot = new Dictionary<string, int>(_lastKnown, StringComparer.OrdinalIgnoreCase);
            var devices = await QuietReadAsync(cancellationToken).ConfigureAwait(false);

            var currentValid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (device.Battery < 0) continue;

                currentValid.Add(device.Name);

                snapshot.TryGetValue(device.Name, out int prev);
                bool isNew = !snapshot.ContainsKey(device.Name);

                _lastKnown[device.Name] = device.Battery;
                DeviceBatteryRead?.Invoke(device.Name, device.Battery);

                BatteryAlertState previousState = _alertStates.TryGetValue(device.Name, out var existingState)
                    ? existingState
                    : ClassifyBatteryState(device.Name, prev, BatteryAlertState.Normal);

                BatteryAlertState currentState = ClassifyBatteryState(device.Name, device.Battery, previousState);

                if (_settings.IgnoredDevices.Contains(device.Name))
                {
                    _alertStates[device.Name] = BatteryAlertState.Normal;
                    continue;
                }

                if (isNew || thresholdsChanged || !_alertStates.ContainsKey(device.Name))
                {
                    if (currentState == BatteryAlertState.Low)
                        _notifier.NotifyLow(device.Name, device.Battery);
                    else if (currentState == BatteryAlertState.High)
                        _notifier.NotifyHigh(device.Name, device.Battery);

                    _alertStates[device.Name] = currentState;
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

                _alertStates[device.Name] = currentState;
            }

            foreach (var name in snapshot.Keys)
            {
                if (!currentValid.Contains(name))
                {
                    _lastKnown.TryRemove(name, out _);
                    _alertStates.TryRemove(name, out _);
                }
            }

            var activeList = new List<DeviceBatteryInfo>();
            foreach (var kvp in _lastKnown)
                activeList.Add(new DeviceBatteryInfo(kvp.Key, kvp.Value));

            ScanCompleted?.Invoke(activeList);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private BatteryAlertState ClassifyBatteryState(string name, int battery, BatteryAlertState previousState)
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
