using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

public partial class BluetoothBatteryMonitor
{
    public Task<List<(string Name, int Battery)>> ScanNowAsync() =>
        ScanNowAsync(_shutdownCts.Token);

    public async Task<List<(string Name, int Battery)>> ScanNowAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        List<(string, int)> results = [];

        try
        {
            _isScanning = true;
            ScanStarted?.Invoke();

            Task<List<(string Name, int Battery)>> gattTask = _gattReader.ReadAllAsync(cancellationToken);
            Task<List<(string Name, int Battery)>> classicTask = _classicReader.ReadAllAsync(cancellationToken);

            await Task.WhenAll(gattTask, classicTask).ConfigureAwait(false);

            results = MergeResults(gattTask.Result, classicTask.Result, raiseDeviceFound: true);

            foreach (var (name, battery) in results)
            {
                if (battery < 0) continue;

                _lastKnown[name] = battery;
                _alertStates[name] = ClassifyBatteryState(name, battery);
                DeviceBatteryRead?.Invoke(name, battery);
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

    public Task<List<(string Name, int Battery)>> StartTrackedScanAsync() =>
        StartTrackedScanAsync(_shutdownCts.Token);

    public Task<List<(string Name, int Battery)>> StartTrackedScanAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        CancellationToken token = linkedCts.Token;

        Task<List<(string Name, int Battery)>> task = ScanNowAsync(token);

        lock (_taskGate)
        {
            if (_disposeStarted || _isDisposed)
            {
                linkedCts.Dispose();
                throw new ObjectDisposedException(nameof(BluetoothBatteryMonitor));
            }

            _activeTasks.Add(task);
        }

        _ = task.ContinueWith(t =>
        {
            lock (_taskGate)
            {
                _activeTasks.Remove(t);
            }

            linkedCts.Dispose();

            if (t.IsFaulted && t.Exception is not null)
                System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] Scan task fault: {t.Exception}");
        }, TaskScheduler.Default);

        return task;
    }

    private async Task<List<(string Name, int Battery)>> QuietReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<List<(string Name, int Battery)>> gattTask = _gattReader.ReadAllAsync(cancellationToken);
        Task<List<(string Name, int Battery)>> classicTask = _classicReader.ReadAllAsync(cancellationToken);

        await Task.WhenAll(gattTask, classicTask).ConfigureAwait(false);

        return MergeResults(gattTask.Result, classicTask.Result, raiseDeviceFound: false);
    }

    private List<(string Name, int Battery)> MergeResults(
        IReadOnlyList<(string Name, int Battery)> first,
        IReadOnlyList<(string Name, int Battery)> second,
        bool raiseDeviceFound)
    {
        List<(string Name, int Battery)> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, battery) in first)
        {
            if (_settings.IgnoredDevices.Contains(name)) continue;
            if (!seen.Add(name)) continue;

            if (raiseDeviceFound)
                DeviceFound?.Invoke(name, battery);
            results.Add((name, battery));
        }

        foreach (var (name, battery) in second)
        {
            if (_settings.IgnoredDevices.Contains(name)) continue;
            if (!seen.Add(name)) continue;

            if (raiseDeviceFound)
                DeviceFound?.Invoke(name, battery);
            results.Add((name, battery));
        }

        return results;
    }
}
