using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

internal sealed class Scanner
{
    private readonly IBatteryReader _gattReader;
    private readonly IBatteryReader _classicReader;
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown;
    private readonly PollingOrchestrator _poller;
    private readonly TaskTracker _tracker;
    private readonly CancellationToken _shutdownToken;
    private readonly Action<string, int> _onDeviceFound;
    private readonly Action<string, int> _onBatteryRead;
    private readonly Action _onScanStarted;
    private readonly Action<IReadOnlyList<DeviceBatteryInfo>> _onScanCompleted;

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private volatile bool _isScanning;
    private volatile bool _disposed;

    public bool IsScanning => _isScanning;

    public Scanner(
        IBatteryReader gattReader,
        IBatteryReader classicReader,
        ConcurrentDictionary<string, DeviceBatteryInfo> lastKnown,
        PollingOrchestrator poller,
        TaskTracker tracker,
        CancellationToken shutdownToken,
        Action<string, int> onDeviceFound,
        Action<string, int> onBatteryRead,
        Action onScanStarted,
        Action<IReadOnlyList<DeviceBatteryInfo>> onScanCompleted)
    {
        _gattReader = gattReader;
        _classicReader = classicReader;
        _lastKnown = lastKnown;
        _poller = poller;
        _tracker = tracker;
        _shutdownToken = shutdownToken;
        _onDeviceFound = onDeviceFound;
        _onBatteryRead = onBatteryRead;
        _onScanStarted = onScanStarted;
        _onScanCompleted = onScanCompleted;
    }

    public Task<List<DeviceBatteryInfo>> ScanNowAsync() =>
        ScanNowAsync(_shutdownToken);

    public async Task<List<DeviceBatteryInfo>> ScanNowAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);

        List<DeviceBatteryInfo> results = [];
        try
        {
            _isScanning = true;
            _onScanStarted();

            var gattTask = _gattReader.ReadAllAsync(ct);
            var classicTask = _classicReader.ReadAllAsync(ct);
            await Task.WhenAll(gattTask, classicTask).ConfigureAwait(false);

            results = MergeResults(gattTask.Result, classicTask.Result, raiseDeviceFound: true);

            await _poller.PollLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var device in results)
                {
                    if (device.Battery < 0) continue;
                    _lastKnown[device.DeviceId] = device;
                    _poller.UpdateAlertState(device.DeviceId, device.Name, device.Battery);
                    _onBatteryRead(device.Name, device.Battery);
                }
            }
            finally
            {
                _poller.PollLock.Release();
            }

            return results;
        }
        finally
        {
            _isScanning = false;
            _scanLock.Release();
            _onScanCompleted(results);
        }
    }

    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync() =>
        StartTrackedScanAsync(_shutdownToken);

    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); 
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, ct);
        CancellationToken token = linkedCts.Token;

        var tcs = new TaskCompletionSource<List<DeviceBatteryInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Register tcs.Task as a tracked task so DisposeAsync waits for it
        _tracker.Start(_ =>
        {
            return ScanNowAsync(token).ContinueWith(t =>
            {
                linkedCts.Dispose();

                if (t.IsFaulted && t.Exception is not null)
                    System.Diagnostics.Debug.WriteLine(
                        $"[BTChargeTrayWatcher] Scan task fault: {t.Exception}");

                if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
                else if (t.IsCanceled) tcs.TrySetCanceled(token);
                else tcs.TrySetResult(t.Result);
            }, TaskScheduler.Default);
        }, _shutdownToken);

        return tcs.Task;
    }

    internal async Task<List<DeviceBatteryInfo>> QuietReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var gattTask = _gattReader.ReadAllAsync(ct);
        var classicTask = _classicReader.ReadAllAsync(ct);
        await Task.WhenAll(gattTask, classicTask).ConfigureAwait(false);
        return MergeResults(gattTask.Result, classicTask.Result, raiseDeviceFound: false);
    }

    private List<DeviceBatteryInfo> MergeResults(
        IReadOnlyList<DeviceBatteryInfo> first,
        IReadOnlyList<DeviceBatteryInfo> second,
        bool raiseDeviceFound)
    {
        List<DeviceBatteryInfo> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var device in first)
        {
            if (!seen.Add(device.DeviceId)) continue;
            if (raiseDeviceFound) _onDeviceFound(device.Name, device.Battery);
            results.Add(device);
        }
        foreach (var device in second)
        {
            if (!seen.Add(device.DeviceId)) continue;
            if (raiseDeviceFound) _onDeviceFound(device.Name, device.Battery);
            results.Add(device);
        }
        return results;
    }

    public void Dispose()
    {
        _disposed = true;
        _scanLock.Dispose();
    }
}
