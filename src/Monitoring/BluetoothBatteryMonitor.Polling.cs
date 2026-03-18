using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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

            bool thresholdsChanged = false;

            if (_thresholdsChanged)
            {
                _thresholdsChanged = false;
                thresholdsChanged = true;
                _alertStates.Clear();
            }

            var snapshot = new Dictionary<string, int>(_lastKnown, StringComparer.OrdinalIgnoreCase);
            var devices = await QuietReadAsync(cancellationToken).ConfigureAwait(false);

            var currentValid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, battery) in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (battery < 0) continue;

                currentValid.Add(name);

                snapshot.TryGetValue(name, out int prev);
                bool isNew = !snapshot.ContainsKey(name);

                _lastKnown[name] = battery;
                DeviceBatteryRead?.Invoke(name, battery);

                BatteryAlertState previousState = _alertStates.TryGetValue(name, out var existingState)
                    ? existingState
                    : ClassifyBatteryState(name, prev, BatteryAlertState.Normal);

                BatteryAlertState currentState = ClassifyBatteryState(name, battery, previousState);

                // Silently skip any alerts if device is ignored
                if (_settings.IgnoredDevices.Contains(name))
                {
                    _alertStates[name] = BatteryAlertState.Normal;
                    continue;
                }

                if (isNew || thresholdsChanged || !_alertStates.ContainsKey(name))
                {
                    if (currentState == BatteryAlertState.Low)
                        _notifier.NotifyLow(name, battery);
                    else if (currentState == BatteryAlertState.High)
                        _notifier.NotifyHigh(name, battery);

                    _alertStates[name] = currentState;
                    continue;
                }

                if (prev == battery)
                    continue;

                if (previousState != currentState)
                {
                    if (currentState == BatteryAlertState.Low)
                        _notifier.NotifyLow(name, battery);
                    else if (currentState == BatteryAlertState.High)
                        _notifier.NotifyHigh(name, battery);
                }

                _alertStates[name] = currentState;
            }

            foreach (var name in snapshot.Keys)
            {
                if (!currentValid.Contains(name))
                {
                    _lastKnown.TryRemove(name, out _);
                    _alertStates.TryRemove(name, out _);
                }
            }

            var activeList = new List<(string, int)>();
            foreach (var kvp in _lastKnown)
            {
                activeList.Add((kvp.Key, kvp.Value));
            }
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
