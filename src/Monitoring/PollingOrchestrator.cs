using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

internal sealed class PollingOrchestrator : IDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly INotificationService _notifier;
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown;
    private readonly TaskTracker _tracker;
    private readonly Func<CancellationToken, Task<List<DeviceBatteryInfo>>> _readDevices;
    private readonly PollingOrchestratorCallbacks _callbacks;
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly ConcurrentDictionary<string, BatteryAlertState> _alertStates = new();
    private bool _disposed;

    public SemaphoreSlim PollLock => _pollLock;

    public PollingOrchestrator(PollingOrchestratorOptions options)
    {
        _settings = options.Settings;
        _notifier = options.Notifier;
        _lastKnown = options.LastKnown;
        _tracker = options.Tracker;
        _readDevices = options.ReadDevices;
        _callbacks = options.Callbacks;
        _shutdownToken = options.ShutdownToken;
    }

    public void StartBackgroundPolling()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _tracker.Start(async ct =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PollOnceAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] Polling fault: {ex}");
                }

                await Task.Delay(PollingDefaults.PollingInterval, ct);
            }
        }, _shutdownToken);
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _pollLock.WaitAsync(ct);
        try
        {
            var devices = await _readDevices(ct);
            bool hasAlert = false;

            foreach (var device in devices)
            {
                if (device.Battery is null) continue;

                _lastKnown[device.DeviceId] = device;
                _callbacks.OnBatteryRead(device.Name, device.Battery);

                BatteryAlertState previousState = _alertStates.TryGetValue(device.DeviceId, out var s)
                    ? s : BatteryAlertState.Normal;

                BatteryAlertState newState = ClassifyBatteryState(
                    device.DeviceId,
                    device.Name,
                    device.Battery.Value,
                    previousState,
                    device.IsCharging);

                _alertStates[device.DeviceId] = newState;
                if (newState is BatteryAlertState.Low or BatteryAlertState.High)
                    hasAlert = true;

                if (newState != previousState)
                    SendAlertIfNeeded(device, newState);
            }

            _callbacks.OnScanCompleted(devices);
            _callbacks.OnAlertStateChanged(hasAlert);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    public void UpdateAlertState(string deviceId, string name, int battery)
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

internal sealed record PollingOrchestratorCallbacks(
    Action<string, int?> OnBatteryRead,
    Action<IReadOnlyList<DeviceBatteryInfo>> OnScanCompleted,
    Action<bool> OnAlertStateChanged);

internal sealed record PollingOrchestratorOptions(
    ThresholdSettings Settings,
    INotificationService Notifier,
    ConcurrentDictionary<string, DeviceBatteryInfo> LastKnown,
    TaskTracker Tracker,
    Func<CancellationToken, Task<List<DeviceBatteryInfo>>> ReadDevices,
    PollingOrchestratorCallbacks Callbacks,
    CancellationToken ShutdownToken);
