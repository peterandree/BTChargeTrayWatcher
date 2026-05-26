using System.Collections.Concurrent;

namespace BTChargeTrayWatcher;

internal sealed class Scanner
{
    private readonly Func<CancellationToken, Task<List<DeviceBatteryInfo>>> _readGatt;
    private readonly Func<CancellationToken, Task<List<DeviceBatteryInfo>>> _readClassic;
    private readonly ScannerCallbacks _callbacks;
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown;
    private readonly PollingOrchestrator _poller;
    private readonly TaskTracker _tracker;
    private readonly CancellationToken _shutdownToken;

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private volatile bool _isScanning;
    private volatile bool _disposed;

    public bool IsScanning => _isScanning;

    public Scanner(ScannerOptions options)
    {
        _readGatt      = options.ReadGatt;
        _readClassic   = options.ReadClassic;
        _callbacks     = options.Callbacks;
        _lastKnown     = options.LastKnown;
        _poller        = options.Poller;
        _tracker       = options.Tracker;
        _shutdownToken = options.ShutdownToken;
    }

    // ── Core merge ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs both readers concurrently and merges results, deduplicating by
    /// <see cref="DeviceBatteryInfo.DeviceId"/> (case-insensitive).
    /// GATT results take precedence on collision — same rule as the former
    /// <c>DeviceAggregationPipeline.ReadMergedAsync</c>.
    /// </summary>
    private async Task<List<DeviceBatteryInfo>> ReadMergedAsync(
        bool raiseDeviceFound,
        CancellationToken ct)
    {
        var gattTask    = _readGatt(ct);
        var classicTask = _readClassic(ct);
        await Task.WhenAll(gattTask, classicTask).ConfigureAwait(false);

        var merged = new Dictionary<string, DeviceBatteryInfo>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var d in classicTask.Result)
            merged[d.DeviceId] = d;

        foreach (var d in gattTask.Result)
            merged[d.DeviceId] = d;  // GATT wins on collision

        var results = new List<DeviceBatteryInfo>(merged.Values);

        if (raiseDeviceFound)
            foreach (var d in results)
                _callbacks.OnDeviceFound(d.DeviceId, d.Name, d.Battery);

        return results;
    }

    // ── Public scan surface ────────────────────────────────────────────────────────────────────

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
            _callbacks.OnScanStarted();

            results = await ReadMergedAsync(raiseDeviceFound: true, ct)
                .ConfigureAwait(false);

            await _poller.PollLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var device in results)
                {
                    if (device.Battery is null) continue;
                    _lastKnown[device.DeviceId] = device;
                    _poller.UpdateAlertState(device.DeviceId, device.Name, device.Battery.Value);
                    _callbacks.OnBatteryRead(device.Name, device.Battery);
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
                _callbacks.OnScanCompleted(results);
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

    internal Task<List<DeviceBatteryInfo>> QuietReadAsync(CancellationToken ct) =>
        ReadMergedAsync(raiseDeviceFound: false, ct);

    public void Dispose()
    {
        _disposed = true;
        _scanLock.Dispose();
    }
}

/// <summary>
/// Delegate surface for <see cref="Scanner"/> — extracted from <see cref="ScannerOptions"/>
/// so the callback group can be passed and validated independently of infrastructure.
/// Closes #119.
/// </summary>
internal sealed record ScannerCallbacks(
    Action<string, string, int?> OnDeviceFound,
    Action<string, int?> OnBatteryRead,
    Action OnScanStarted,
    Action<IReadOnlyList<DeviceBatteryInfo>> OnScanCompleted);

/// ADR-009: options record keeps infrastructure separate from callbacks.
internal sealed record ScannerOptions(
    Func<CancellationToken, Task<List<DeviceBatteryInfo>>> ReadGatt,
    Func<CancellationToken, Task<List<DeviceBatteryInfo>>> ReadClassic,
    ConcurrentDictionary<string, DeviceBatteryInfo> LastKnown,
    PollingOrchestrator Poller,
    TaskTracker Tracker,
    ScannerCallbacks Callbacks,
    CancellationToken ShutdownToken);
