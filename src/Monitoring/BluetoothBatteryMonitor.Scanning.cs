using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

public partial class BluetoothBatteryMonitor
{
    public Task<List<DeviceBatteryInfo>> ScanNowAsync() =>
        ScanNowAsync(_shutdownCts.Token);

    public async Task<List<DeviceBatteryInfo>> ScanNowAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        List<DeviceBatteryInfo> results = [];

        try
        {
            _isScanning = true;
            ScanStarted?.Invoke();

            Task<List<DeviceBatteryInfo>> gattTask = _gattReader.ReadAllAsync(cancellationToken);
            Task<List<DeviceBatteryInfo>> classicTask = _classicReader.ReadAllAsync(cancellationToken);

            await Task.WhenAll(gattTask, classicTask).ConfigureAwait(false);

            results = MergeResults(gattTask.Result, classicTask.Result, raiseDeviceFound: true);

            await _pollLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var device in results)
                {
                    if (device.Battery < 0) continue;

                    _lastKnown[device.DeviceId] = device;

                    BatteryAlertState existingState = _alertStates.TryGetValue(device.DeviceId, out var s)
                        ? s
                        : BatteryAlertState.Normal;

                    _alertStates[device.DeviceId] = ClassifyBatteryState(device.DeviceId, device.Name, device.Battery, existingState);

                    DeviceBatteryRead?.Invoke(device.Name, device.Battery);
                }
            }
            finally
            {
                _pollLock.Release();
            }

            return results;
        }
        finally
        {
            _isScanning = false;
            _scanLock.Release();
            ScanCompleted?.Invoke(results);
        }
    }

    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync() =>
        StartTrackedScanAsync(_shutdownCts.Token);

    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        CancellationToken token = linkedCts.Token;

        var tcs = new TaskCompletionSource<List<DeviceBatteryInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_taskGate)
        {
            if (_disposeStarted || _isDisposed)
            {
                linkedCts.Dispose();
                throw new ObjectDisposedException(nameof(BluetoothBatteryMonitor));
            }

            _activeTasks.Add(tcs.Task);
        }

        _ = ScanNowAsync(token).ContinueWith(t =>
        {
            lock (_taskGate)
            {
                _activeTasks.Remove(tcs.Task);
            }

            linkedCts.Dispose();

            if (t.IsFaulted && t.Exception is not null)
                System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] Scan task fault: {t.Exception}");

            if (t.IsFaulted)
                tcs.TrySetException(t.Exception!.InnerExceptions);
            else if (t.IsCanceled)
                tcs.TrySetCanceled(token);
            else
                tcs.TrySetResult(t.Result);
        }, TaskScheduler.Default);

        return tcs.Task;
    }

    private async Task<List<DeviceBatteryInfo>> QuietReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<List<DeviceBatteryInfo>> gattTask = _gattReader.ReadAllAsync(cancellationToken);
        Task<List<DeviceBatteryInfo>> classicTask = _classicReader.ReadAllAsync(cancellationToken);

        await Task.WhenAll(gattTask, classicTask).ConfigureAwait(false);

        return MergeResults(gattTask.Result, classicTask.Result, raiseDeviceFound: false);
    }

    private List<DeviceBatteryInfo> MergeResults(
        IReadOnlyList<DeviceBatteryInfo> first,
        IReadOnlyList<DeviceBatteryInfo> second,
        bool raiseDeviceFound)
    {
        List<DeviceBatteryInfo> results = [];
        // Deduplicate by stable DeviceId — resolves issue #14 for cross-reader merge
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var device in first)
        {
            if (!seen.Add(device.DeviceId)) continue;

            if (raiseDeviceFound)
                DeviceFound?.Invoke(device.Name, device.Battery);
            results.Add(device);
        }

        foreach (var device in second)
        {
            if (!seen.Add(device.DeviceId)) continue;

            if (raiseDeviceFound)
                DeviceFound?.Invoke(device.Name, device.Battery);
            results.Add(device);
        }

        return results;
    }
}
