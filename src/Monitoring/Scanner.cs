using System.Collections.Concurrent;

namespace BTChargeTrayWatcher;

internal sealed class Scanner
{
    private readonly Func<CancellationToken, Task<List<DeviceBatteryInfo>>> _readDevices;
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
        _readDevices   = options.ReadDevices;
        _callbacks     = options.Callbacks;
        _lastKnown     = options.LastKnown;
        _poller        = options.Poller;
        _tracker       = options.Tracker;
        _shutdownToken = options.ShutdownToken;
    }

    // ── Read path ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current device list through <see cref="ScannerOptions.ReadDevices"/>, which is
    /// already the fully merged GATT + Classic result produced by
    /// <c>BatteryReaderOrchestrator</c> (ADR-002). The Scanner deliberately performs no merge of
    /// its own — there is exactly one merge point in the app, and it is the orchestrator's.
    /// </summary>
    private async Task<List<DeviceBatteryInfo>> ReadDevicesAsync(
        bool raiseDeviceFound,
        CancellationToken ct)
    {
        var results = await _readDevices(ct).ConfigureAwait(false);

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

            results = await ReadDevicesAsync(raiseDeviceFound: true, ct)
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
        ReadDevicesAsync(raiseDeviceFound: false, ct);

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
/// <paramref name="ReadDevices"/> returns the already-merged GATT + Classic list owned by
/// <c>BatteryReaderOrchestrator</c> (ADR-002) — it is not a GATT-only delegate (#157).
internal sealed record ScannerOptions(
    Func<CancellationToken, Task<List<DeviceBatteryInfo>>> ReadDevices,
    ConcurrentDictionary<string, DeviceBatteryInfo> LastKnown,
    PollingOrchestrator Poller,
    TaskTracker Tracker,
    ScannerCallbacks Callbacks,
    CancellationToken ShutdownToken);
