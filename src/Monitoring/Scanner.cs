using System.Collections.Concurrent;

namespace BTChargeTrayWatcher;

internal sealed class Scanner
{
    private readonly DeviceAggregationPipeline _aggregationPipeline;
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown;
    private readonly PollingOrchestrator _poller;
    private readonly TaskTracker _tracker;
    private readonly CancellationToken _shutdownToken;
    private readonly Action<string, int?> _onBatteryRead;
    private readonly Action _onScanStarted;
    private readonly Action<IReadOnlyList<DeviceBatteryInfo>> _onScanCompleted;

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private volatile bool _isScanning;
    private volatile bool _disposed;

    public bool IsScanning => _isScanning;

    public Scanner(ScannerOptions options)
    {
        _aggregationPipeline = new DeviceAggregationPipeline(
            options.GattReader,
            options.ClassicReader,
            options.OnDeviceFound);
        _lastKnown = options.LastKnown;
        _poller = options.Poller;
        _tracker = options.Tracker;
        _shutdownToken = options.ShutdownToken;
        _onBatteryRead = options.OnBatteryRead;
        _onScanStarted = options.OnScanStarted;
        _onScanCompleted = options.OnScanCompleted;
    }

    public Task<List<DeviceBatteryInfo>> ScanNowAsync() =>
        ScanNowAsync(_shutdownToken);

    public async Task<List<DeviceBatteryInfo>> ScanNowAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);

        List<DeviceBatteryInfo> results = [];
        bool scanSucceeded = false;
        try
        {
            _isScanning = true;
            _onScanStarted();

            results = await _aggregationPipeline
                .ReadMergedAsync(raiseDeviceFound: true, ct)
                .ConfigureAwait(false);

            await _poller.PollLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var device in results)
                {
                    if (device.Battery is null) continue;
                    _lastKnown[device.DeviceId] = device;
                    _poller.UpdateAlertState(device.DeviceId, device.Name, device.Battery.Value);
                    _onBatteryRead(device.Name, device.Battery);
                }
            }
            finally
            {
                _poller.PollLock.Release();
            }

            scanSucceeded = true;
            return results;
        }
        finally
        {
            _isScanning = false;
            _scanLock.Release();
            if (scanSucceeded)
                _onScanCompleted(results);
        }
    }

    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync() =>
        StartTrackedScanAsync(_shutdownToken);

    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<List<DeviceBatteryInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _tracker.Start(_ =>
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, ct);
            CancellationToken token = linkedCts.Token;

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
        return await _aggregationPipeline
            .ReadMergedAsync(raiseDeviceFound: false, ct)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposed = true;
        _scanLock.Dispose();
    }
}

internal sealed record ScannerOptions(
    IBatteryReader GattReader,
    IBatteryReader ClassicReader,
    ConcurrentDictionary<string, DeviceBatteryInfo> LastKnown,
    PollingOrchestrator Poller,
    TaskTracker Tracker,
    Action<string, string, int?> OnDeviceFound,
    Action<string, int?> OnBatteryRead,
    Action OnScanStarted,
    Action<IReadOnlyList<DeviceBatteryInfo>> OnScanCompleted,
    CancellationToken ShutdownToken);
