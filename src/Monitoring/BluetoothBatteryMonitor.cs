using System.Collections.Concurrent;
using Microsoft.Win32;

namespace BTChargeTrayWatcher;

public sealed class BluetoothBatteryMonitor : IAsyncDisposable
{
    private readonly ThresholdSettings _settings;

    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly System.Threading.Timer _timer;

    private readonly TaskTracker _taskTracker;
    private readonly PollingOrchestrator _poller;
    private readonly Scanner _scanner;

    private readonly DeviceWatcherService? _deviceWatcher;
    private readonly GattConnectionManager? _gattConnectionManager;
    private readonly DeviceCapabilityCache? _capabilityCache;

    private volatile bool _disposeStarted;
    private volatile bool _isDisposed;

    private readonly TaskCompletionSource _disposalComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<string, int?>? DeviceBatteryRead;
    public event Action<string, string, int?>? DeviceFound;
    public event Action<IReadOnlyList<DeviceBatteryInfo>>? ManualScanCompleted;
    public event Action<IReadOnlyList<DeviceBatteryInfo>>? BackgroundRefreshCompleted;
    public event Action? ScanStarted;
    public event Action<bool>? AlertStateChanged;

    public bool IsScanning => _scanner.IsScanning;

    public IReadOnlyList<DeviceBatteryInfo> LastKnownDevices =>
        [.. _lastKnown.Values];

    public bool HasCachedResults => !_lastKnown.IsEmpty;

    internal IReadOnlyList<WatchedDevice> TrackedDevices =>
        _deviceWatcher?.CurrentDevices ?? [];

    internal async Task RefreshTrackedDevicesAsync(CancellationToken ct = default)
    {
        if (_deviceWatcher is not null)
            await _deviceWatcher.RefreshAsync(ct).ConfigureAwait(false);
    }

    internal BluetoothBatteryMonitor(
        ThresholdSettings                 settings,
        INotificationService              notifier,
        BluetoothMonitoringInfrastructure infrastructure)
    {
        _settings = settings;

        if (infrastructure.AliasSuggestionService is { } svc)
            infrastructure.Orchestrator.AliasSuggested += svc.OnAliasSuggested;

        // Read path for the Scanner: the orchestrator already merges GATT and Classic internally
        // (ADR-002), so the Scanner must not merge a second time. The mode is what differs between
        // the two callers, and the Scanner gets one delegate per mode so a caller cannot silently
        // end up on the wrong one (issue #164):
        //   Background — the 60 s poll and the quiet read. Passive: DeviceWatcherService already
        //                provides IsConnected data, so active radio queries are unnecessary
        //                (ADR-017), and a BLE device may hold a notification subscription.
        //   DeepScan   — the user-initiated scan. Active: each Classic candidate is verified and
        //                GATT reads are uncached and never subscribe (ADR-019).
        Func<CancellationToken, Task<List<DeviceBatteryInfo>>> readDevices = ct =>
        {
            infrastructure.AliasSuggestionService?.BeginCycle();
            return infrastructure.Orchestrator.ReadAllAsync(
                infrastructure.DeviceWatcher.CurrentDevices,
                BatteryReadMode.Background, ct);
        };

        Func<CancellationToken, Task<List<DeviceBatteryInfo>>> deepReadDevices = ct =>
        {
            infrastructure.AliasSuggestionService?.BeginCycle();
            return infrastructure.Orchestrator.ReadAllAsync(
                infrastructure.DeviceWatcher.CurrentDevices,
                BatteryReadMode.DeepScan, ct);
        };

        _taskTracker = new TaskTracker();

        _poller = new PollingOrchestrator(new PollingOrchestratorOptions(
            Settings:      settings,
            Notifier:      notifier,
            LastKnown:     _lastKnown,
            Tracker:       _taskTracker,
            ReadDevices:   ct => _scanner!.QuietReadAsync(ct),
            ShutdownToken: _shutdownCts.Token,
            Callbacks:     new PollingOrchestratorCallbacks(
                OnBatteryRead:       (name, lvl) => DeviceBatteryRead?.Invoke(name, lvl),
                OnScanCompleted:     list => BackgroundRefreshCompleted?.Invoke(list),
                OnAlertStateChanged: hasAlert => AlertStateChanged?.Invoke(hasAlert),
                // #161: release the GATT subscription when a device is evicted after repeated
                // misses, so no subscription record outlives its known-device cache entry.
                OnDeviceEvicted:     (deviceId, ct) =>
                    _gattConnectionManager?.DeviceEvictedAsync(deviceId, ct) ?? Task.CompletedTask)));

        _scanner = new Scanner(new ScannerOptions(
            ReadDevices:     readDevices,
            DeepReadDevices: deepReadDevices,
            LastKnown:       _lastKnown,
            Poller:          _poller,
            Tracker:         _taskTracker,
            ShutdownToken:   _shutdownCts.Token,
            Callbacks:       new ScannerCallbacks(
                OnDeviceFound:   (id, name, lvl) => DeviceFound?.Invoke(id, name, lvl),
                OnBatteryRead:   (name, lvl) => DeviceBatteryRead?.Invoke(name, lvl),
                OnScanStarted:   () => ScanStarted?.Invoke(),
                OnScanCompleted: list => ManualScanCompleted?.Invoke(list))));

        _timer = new System.Threading.Timer(
            _ => OnTimerTick(),
            null,
            PollingDefaults.StartupDelay,
            PollingDefaults.PollingInterval);

        _settings.Changed += Settings_Changed;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

        _deviceWatcher         = infrastructure.DeviceWatcher;
        _gattConnectionManager = infrastructure.GattConnectionManager;
        _capabilityCache       = infrastructure.CapabilityCache;
        _deviceWatcher.DevicesChanged += OnDevicesChanged;
    }

    public Task PollAsync() => StartTrackedPollAsync(_shutdownCts.Token);
    public Task PollAsync(CancellationToken ct) => StartTrackedPollAsync(ct);

    /// <summary>Automatic startup scan — passive (<see cref="BatteryReadMode.Background"/>).</summary>
    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync() =>
        _scanner.StartTrackedScanAsync(_shutdownCts.Token);

    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync(CancellationToken ct) =>
        _scanner.StartTrackedScanAsync(ct);

    /// <summary>
    /// User-initiated diagnostic scan (ADR-019): actively verifies each Classic candidate and reads
    /// GATT uncached without ever subscribing. Deliberately a separate method rather than a mode
    /// argument on <see cref="StartTrackedScanAsync()"/> so an automatic caller cannot end up on
    /// the active path by accident (issue #164).
    /// </summary>
    public Task<List<DeviceBatteryInfo>> StartTrackedDeepScanAsync() =>
        _scanner.StartTrackedScanAsync(BatteryReadMode.DeepScan, _shutdownCts.Token);

    public Task<List<DeviceBatteryInfo>> StartTrackedDeepScanAsync(CancellationToken ct) =>
        _scanner.StartTrackedScanAsync(BatteryReadMode.DeepScan, ct);

    private Task StartTrackedPollAsync(CancellationToken ct)
    {
        ThrowIfDisposingOrDisposed();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _taskTracker.Start(_ =>
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, ct);
            CancellationToken token = linkedCts.Token;

            return _poller.PollAsync(token).ContinueWith(t =>
            {
                linkedCts.Dispose();
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine(
                        $"[BTChargeTrayWatcher] PollAsync fault: {t.Exception}");
                if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
                else if (t.IsCanceled) tcs.TrySetCanceled(token);
                else tcs.TrySetResult();
            }, TaskScheduler.Default);
        }, _shutdownCts.Token);

        return tcs.Task;
    }

    private void OnTimerTick()
    {
        if (_disposeStarted || _isDisposed) return;
        try { _poller.OnTimerTick(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BTChargeTrayWatcher] Timer tick fault: {ex}");
        }
    }

    private void OnDevicesChanged()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested) return;
        _poller.OnTimerTick();
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposeStarted || _isDisposed) return;

        if (e.Mode == PowerModes.Suspend)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);

            // #161: drop every GATT subscription before the machine sleeps — a subscription left
            // open across suspend can leave the peripheral's CCCD inconsistent on some adapters.
            // Tracked so shutdown waits for the round-trip (ADR-007).
            if (_gattConnectionManager is not null)
                _taskTracker.Start(ct => _gattConnectionManager.SuspendSubscriptionsAsync(ct), _shutdownCts.Token);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _capabilityCache?.InvalidateAll();
            _gattConnectionManager?.InvalidateAll();
            _timer.Change(PollingDefaults.ResumeDelay, PollingDefaults.PollingInterval);
        }
    }

    private void Settings_Changed()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested) return;
        _poller.SignalThresholdsChanged();
    }

    private void ThrowIfDisposingOrDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposeStarted || _isDisposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        if (_disposeStarted)
        {
            await _disposalComplete.Task.ConfigureAwait(false);
            return;
        }

        _disposeStarted = true;

        _settings.Changed -= Settings_Changed;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        if (_deviceWatcher is not null)
            _deviceWatcher.DevicesChanged -= OnDevicesChanged;

        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _taskTracker.Stop();
        _shutdownCts.Cancel();

        Task[] tasks = _taskTracker.Snapshot();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BTChargeTrayWatcher] Shutdown wait fault: {ex}");
            }
        }

        _timer.Dispose();
        _shutdownCts.Dispose();
        _poller.Dispose();
        _scanner.Dispose();

        // #161: unsubscribe every device before releasing the manager, so shutdown never leaves a
        // dangling GATT session on a peripheral.
        if (_gattConnectionManager is not null)
            await _gattConnectionManager.DisposeAsync().ConfigureAwait(false);

        if (_deviceWatcher is not null)
            await _deviceWatcher.DisposeAsync().ConfigureAwait(false);

        _isDisposed = true;
        GC.SuppressFinalize(this);
        _disposalComplete.TrySetResult();
    }
}
