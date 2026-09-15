using System.Collections.Concurrent;

namespace BTChargeTrayWatcher;

internal sealed class Scanner
{
    private readonly Func<CancellationToken, Task<List<DeviceBatteryInfo>>> _readDevices;
    private readonly Func<CancellationToken, Task<List<DeviceBatteryInfo>>> _deepReadDevices;
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
        _readDevices     = options.ReadDevices;
        _deepReadDevices = options.DeepReadDevices;
        _callbacks       = options.Callbacks;
        _lastKnown       = options.LastKnown;
        _poller          = options.Poller;
        _tracker         = options.Tracker;
        _shutdownToken   = options.ShutdownToken;
    }

    // ── Read path ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current device list through the delegate for <paramref name="mode"/>, which is
    /// already the fully merged GATT + Classic result produced by
    /// <c>BatteryReaderOrchestrator</c> (ADR-002). The Scanner deliberately performs no merge of
    /// its own — there is exactly one merge point in the app, and it is the orchestrator's.
    /// </summary>
    /// <remarks>
    /// Two delegates, not one, because the two read modes must not be interchangeable: the quiet
    /// background read is passive (ADR-017) while a user-initiated scan is the diagnostic read
    /// (ADR-019) that actively verifies candidates and must never subscribe. Sharing one delegate
    /// with a hardcoded mode is how the scan ended up on the background path (issue #164).
    /// </remarks>
    private async Task<List<DeviceBatteryInfo>> ReadDevicesAsync(
        BatteryReadMode mode,
        bool raiseDeviceFound,
        CancellationToken ct)
    {
        var read = mode == BatteryReadMode.DeepScan ? _deepReadDevices : _readDevices;
        var results = await read(ct).ConfigureAwait(false);

        if (raiseDeviceFound)
            foreach (var d in results)
                _callbacks.OnDeviceFound(d.DeviceId, d.Name, d.Battery);

        return results;
    }

    // ── Public scan surface ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a scan in the passive <see cref="BatteryReadMode.Background"/> mode. This is the safe
    /// default: the automatic startup scan uses it, and passive reads must never become active
    /// probing without an explicit caller decision (ADR-017, issue #164).
    /// </summary>
    public Task<List<DeviceBatteryInfo>> ScanNowAsync() =>
        ScanNowAsync(BatteryReadMode.Background, _shutdownToken);

    public Task<List<DeviceBatteryInfo>> ScanNowAsync(CancellationToken ct) =>
        ScanNowAsync(BatteryReadMode.Background, ct);

    /// <summary>
    /// Runs a scan in an explicit <see cref="BatteryReadMode"/>. Only a user-initiated diagnostic
    /// scan may pass <see cref="BatteryReadMode.DeepScan"/> (ADR-019).
    /// </summary>
    public async Task<List<DeviceBatteryInfo>> ScanNowAsync(BatteryReadMode mode, CancellationToken ct)
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

            results = await ReadDevicesAsync(mode, raiseDeviceFound: true, ct)
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
        StartTrackedScanAsync(BatteryReadMode.Background, _shutdownToken);

    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync(CancellationToken ct) =>
        StartTrackedScanAsync(BatteryReadMode.Background, ct);

    /// <summary>
    /// Runs a tracked scan in an explicit <see cref="BatteryReadMode"/>. The caller chooses the
    /// mode because only it knows whether the scan was user-initiated: the automatic startup scan
    /// is background, the manual scan is the diagnostic one (ADR-019, issue #164).
    /// </summary>
    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync(BatteryReadMode mode, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<List<DeviceBatteryInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _tracker.Start(_ =>
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, ct);
            CancellationToken token = linkedCts.Token;

            return ScanNowAsync(mode, token).ContinueWith(t =>
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
        ReadDevicesAsync(BatteryReadMode.Background, raiseDeviceFound: false, ct);

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
/// <c>BatteryReaderOrchestrator</c> (ADR-002) for the passive background read — it is not a
/// GATT-only delegate (#157). <paramref name="DeepReadDevices"/> is the same shape for the
/// user-initiated diagnostic scan (ADR-019); the two must not be wired to one mode (#164).
internal sealed record ScannerOptions(
    Func<CancellationToken, Task<List<DeviceBatteryInfo>>> ReadDevices,
    Func<CancellationToken, Task<List<DeviceBatteryInfo>>> DeepReadDevices,
    ConcurrentDictionary<string, DeviceBatteryInfo> LastKnown,
    PollingOrchestrator Poller,
    TaskTracker Tracker,
    ScannerCallbacks Callbacks,
    CancellationToken ShutdownToken);
