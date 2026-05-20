using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

internal sealed class PollingOrchestrator : IDisposable
{
    internal enum BatteryAlertState { Normal = 0, Low = 1, High = 2 }

    private readonly ThresholdSettings _settings;
    private readonly INotificationService _notifier;
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown;
    private readonly TaskTracker _tracker;
    private readonly Func<CancellationToken, Task<List<DeviceBatteryInfo>>> _readDevices;
    private readonly CancellationToken _shutdownToken;
    private readonly Action<string, int?> _onBatteryRead;
    private readonly Action<IReadOnlyList<DeviceBatteryInfo>> _onScanCompleted;
    private readonly Action<bool> _onAlertStateChanged;

    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly ConcurrentDictionary<string, BatteryAlertState> _alertStates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, int> _missCount =
        new(StringComparer.OrdinalIgnoreCase);

    // Timestamp of last processed update per device (UTC)
    private readonly ConcurrentDictionary<string, DateTime> _lastProcessed =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile int _thresholdsChanged;
    private volatile bool _disposed;

    internal SemaphoreSlim PollLock => _pollLock;

    public PollingOrchestrator(PollingOrchestratorOptions options)
    {
        _settings = options.Settings;
        _notifier = options.Notifier;
        _lastKnown = options.LastKnown;
        _tracker = options.Tracker;
        _readDevices = options.ReadDevices;
        _shutdownToken = options.ShutdownToken;
        _onBatteryRead = options.OnBatteryRead;
        _onScanCompleted = options.OnScanCompleted;
        _onAlertStateChanged = options.OnAlertStateChanged;
    }

    public void OnTimerTick()
    {
        if (_disposed || _shutdownToken.IsCancellationRequested) return;
        _tracker.Start(ct => SafePollAsync(ct), _shutdownToken);
    }

    public void SignalThresholdsChanged()
    {
        if (_disposed || _shutdownToken.IsCancellationRequested) return;
        Interlocked.Exchange(ref _thresholdsChanged, 1);
        _tracker.Start(ct => SafePollAsync(ct), _shutdownToken);
    }

    private async Task SafePollAsync(CancellationToken ct)
    {
        try { await PollInternalAsync(ct, honorPerDeviceIntervals: true).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] PollAsync fault: {ex}");
        }
    }

    public Task PollAsync() => PollAsync(_shutdownToken);

    public Task PollAsync(CancellationToken ct) => PollInternalAsync(ct, honorPerDeviceIntervals: false);

    private async Task PollInternalAsync(CancellationToken ct, bool honorPerDeviceIntervals)
    {
        ct.ThrowIfCancellationRequested();
        await _pollLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            bool thresholdsChanged = Interlocked.Exchange(ref _thresholdsChanged, 0) == 1;
            if (thresholdsChanged)
            {
                _alertStates.Clear();
                _missCount.Clear();
            }

            var snapshot = new Dictionary<string, DeviceBatteryInfo>(
                _lastKnown, StringComparer.OrdinalIgnoreCase);

            var devices = await _readDevices(ct).ConfigureAwait(false);
            var currentValid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                ct.ThrowIfCancellationRequested();
                if (device.Battery is null) continue;

                currentValid.Add(device.DeviceId);
                snapshot.TryGetValue(device.DeviceId, out var prevInfo);
                int prev = prevInfo?.Battery ?? 0;
                bool isNew = prevInfo is null;

                // Reset miss count for presence tracking regardless of whether the
                // reading is processed (honours presence detection while allowing
                // per-device throttling of updates/alerts).
                _missCount[device.DeviceId] = 0;

                bool due = !honorPerDeviceIntervals;
                if (honorPerDeviceIntervals)
                {
                    int intervalSec = (int)(_settings.GetPollIntervalForDevice(device.DeviceId, device.Name)
                        ?? (int)PollingDefaults.PollingInterval.TotalSeconds);
                    if (!_lastProcessed.TryGetValue(device.DeviceId, out var last)) due = true;
                    else if ((DateTime.UtcNow - last).TotalSeconds >= intervalSec) due = true;
                }

                if (!due)
                {
                    // Still update presence; skip processing/notifications for now.
                    continue;
                }

                // Mark processed timestamp
                _lastProcessed[device.DeviceId] = DateTime.UtcNow;

                _lastKnown[device.DeviceId] = device;
                _onBatteryRead(device.Name, device.Battery);

                BatteryAlertState previousState = _alertStates.TryGetValue(device.DeviceId, out var es)
                    ? es
                    : ClassifyBatteryState(device.DeviceId, device.Name, prev, BatteryAlertState.Normal, device.IsCharging);

                BatteryAlertState currentState =
                    ClassifyBatteryState(device.DeviceId, device.Name, device.Battery.Value, previousState, device.IsCharging);

                if (_settings.IsIgnored(device.DeviceId, device.Name))
                {
                    _alertStates[device.DeviceId] = BatteryAlertState.Normal;
                    continue;
                }

                if (isNew || thresholdsChanged || !_alertStates.ContainsKey(device.DeviceId))
                {
                    SendAlertIfNeeded(device, currentState);
                    _alertStates[device.DeviceId] = currentState;
                    continue;
                }

                if (prev == device.Battery.Value) continue;

                if (previousState != currentState)
                    SendAlertIfNeeded(device, currentState);

                _alertStates[device.DeviceId] = currentState;
            }

            foreach (var id in snapshot.Keys)
            {
                if (!currentValid.Contains(id))
                {
                    int misses = _missCount.AddOrUpdate(id, 1, (_, prev) => prev + 1);
                    if (misses >= PollingDefaults.MissCountThreshold)
                    {
                        _lastKnown.TryRemove(id, out _);
                        _alertStates.TryRemove(id, out _);
                        _missCount.TryRemove(id, out _);
                    }
                }
            }

            _onScanCompleted([.. _lastKnown.Values]);

            // Emit the authoritative combined alert state derived from classified states.
            // This is the single source of truth for the tray icon overlay (ADR-011).
            bool hasAlert = false;
            foreach (var state in _alertStates.Values)
            {
                if (state != BatteryAlertState.Normal) { hasAlert = true; break; }
            }
            _onAlertStateChanged(hasAlert);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    internal void UpdateAlertState(string deviceId, string name, int battery)
    {
        BatteryAlertState existing = _alertStates.TryGetValue(deviceId, out var s)
            ? s : BatteryAlertState.Normal;
        _alertStates[deviceId] = ClassifyBatteryState(deviceId, name, battery, existing, isCharging: null);
    }

    /// <summary>
    /// Classifies the battery alert state with hysteresis.
    /// When <paramref name="isCharging"/> is confirmed true, the High threshold
    /// is suppressed — a device intentionally on charge must not fire a High alert.
    /// Unknown (null) does not suppress: absence of data is not confirmation.
    /// Low alerts are never suppressed (ADR-004 extension).
    /// </summary>
    internal BatteryAlertState ClassifyBatteryState(
        string deviceId, string name, int battery, BatteryAlertState previousState, bool? isCharging)
    {
        if (battery < 0 || _settings.IsIgnored(deviceId, name))
            return BatteryAlertState.Normal;

        int low = _settings.GetLowForDevice(deviceId, name);
        int high = _settings.GetHighForDevice(deviceId, name);
        int hysteresis = PollingDefaults.Hysteresis;

        if (battery <= low) return BatteryAlertState.Low;

        // Suppress High alert when the device is confirmed charging (ADR-004 extension).
        if (battery >= high && isCharging != true) return BatteryAlertState.High;

        if (previousState == BatteryAlertState.Low && battery <= low + hysteresis)
            return BatteryAlertState.Low;
        if (previousState == BatteryAlertState.High && battery >= high - hysteresis && isCharging != true)
            return BatteryAlertState.High;

        return BatteryAlertState.Normal;
    }

    private void SendAlertIfNeeded(DeviceBatteryInfo device, BatteryAlertState state)
    {
        if (state == BatteryAlertState.Low)
            _notifier.NotifyLow(device.Name, device.Battery!.Value);
        else if (state == BatteryAlertState.High)
            _notifier.NotifyHigh(device.Name, device.Battery!.Value);
    }

    public void Dispose()
    {
        _disposed = true;
        _pollLock.Dispose();
    }
}

internal sealed record PollingOrchestratorOptions(
    ThresholdSettings Settings,
    INotificationService Notifier,
    ConcurrentDictionary<string, DeviceBatteryInfo> LastKnown,
    TaskTracker Tracker,
    Func<CancellationToken, Task<List<DeviceBatteryInfo>>> ReadDevices,
    Action<string, int?> OnBatteryRead,
    Action<IReadOnlyList<DeviceBatteryInfo>> OnScanCompleted,
    Action<bool> OnAlertStateChanged,
    CancellationToken ShutdownToken);
