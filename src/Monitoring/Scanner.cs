using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

internal sealed class Scanner : IDisposable
{
    private readonly IBatteryReader _gattReader;
    private readonly IBatteryReader _classicReader;
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown;
    private readonly PollingOrchestrator _poller;
    private readonly TaskTracker _tracker;
    private readonly ScannerCallbacks _callbacks;
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private volatile bool _isScanning;
    private bool _disposed;

    public Scanner(ScannerOptions options)
    {
        _gattReader = options.GattReader;
        _classicReader = options.ClassicReader;
        _lastKnown = options.LastKnown;
        _poller = options.Poller;
        _tracker = options.Tracker;
        _callbacks = options.Callbacks;
        _shutdownToken = options.ShutdownToken;
    }

    public async Task<List<DeviceBatteryInfo>> ReadMergedAsync(bool raiseDeviceFound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ct);

        // Read GATT first.
        var gatt = await _gattReader.ReadAsync(ct);

        // Read Classic second.
        var classic = await _classicReader.ReadAsync(ct);

        // Merge preferring GATT if both sources report a value for the same device.
        var merged = gatt
            .Concat(classic)
            .GroupBy(d => d.DeviceId)
            .Select(g => g.FirstOrDefault(d => d.Source == BatterySource.Gatt) ?? g.First())
            .ToList();

        if (raiseDeviceFound)
        {
            foreach (var d in merged)
                _callbacks.OnDeviceFound(d.DeviceId, d.Name, d.Battery);
        }

        return merged;
    }

    public async Task<List<DeviceBatteryInfo>> ScanNowAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isScanning) return new List<DeviceBatteryInfo>();
        _isScanning = true;
        bool scanSucceeded = false;
        List<DeviceBatteryInfo> results = new();

        try
        {
            _callbacks.OnScanStarted();

            results = await ReadMergedAsync(raiseDeviceFound: true, ct);
            await _poller.PollLock.WaitAsync(ct);
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

internal sealed record ScannerCallbacks(
    Action<string, string, int?> OnDeviceFound,
    Action<string, int?> OnBatteryRead,
    Action OnScanStarted,
    Action<IReadOnlyList<DeviceBatteryInfo>> OnScanCompleted);

internal sealed record ScannerOptions(
    IBatteryReader GattReader,
    IBatteryReader ClassicReader,
    ConcurrentDictionary<string, DeviceBatteryInfo> LastKnown,
    PollingOrchestrator Poller,
    TaskTracker Tracker,
    ScannerCallbacks Callbacks,
    CancellationToken ShutdownToken);
