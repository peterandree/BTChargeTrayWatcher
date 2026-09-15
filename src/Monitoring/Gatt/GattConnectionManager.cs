using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BTChargeTrayWatcher;

/// <summary>
/// Long-lived service that reads GATT Battery Level (0x2A19) from BLE devices.
/// Tries the classic Battery Service (0x180F) first, then the Common Battery
/// Service (0x182B) used by newer devices. Caches <em>knowledge</em> (which
/// device IDs support the battery service), not WinRT objects. All WinRT
/// references are dropped immediately after each read so peripherals can enter
/// low-power sleep states.
/// <para>
/// Hybrid push/poll (issue #158, amended ADR-003/ADR-017): a device whose
/// Battery Level characteristic advertises <c>Notify</c> and which is currently connected may be
/// subscribed through <see cref="GattSubscriptionCoordinator"/>, bounded by
/// <see cref="GattSubscriptionDefaults.MaxConcurrentSubscriptions"/>. While subscribed, the
/// unchanged 60 s poll reads the value with <see cref="BluetoothCacheMode.Cached"/> (no radio
/// traffic) and prefers a pushed value when the cache is empty. Every teardown path —
/// disconnect, suspend, eviction, dispose — releases the subscription through the coordinator, so
/// no WinRT reference outlives its use (#78).
/// </para>
/// </summary>
internal sealed class GattConnectionManager : IDisposable, IAsyncDisposable
{
    private static readonly Guid BatteryStatusUuid      = new("00002bea-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryPowerStateUuid  = new("00002a1b-0000-1000-8000-00805f9b34fb");
    private static readonly TimeSpan WinRtTimeout = TimeSpan.FromSeconds(2);

    private readonly HashSet<string> _knownGattDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly Lock _lock = new();

    /// <summary>
    /// Bounded set of live Battery Level subscriptions (#160/#161).
    /// Always present: with <see cref="GattSubscriptionDefaults.MaxConcurrentSubscriptions"/> set
    /// to <c>0</c> the policy refuses every subscribe attempt, which is the baseline configuration
    /// for the #158 hardware measurement.
    /// </summary>
    private readonly GattSubscriptionCoordinator _subscriptions;

    /// <summary>
    /// Test seam: replaces the entire WinRT read path so the concurrency gate,
    /// cancellation plumbing, and result contract can be unit-tested without
    /// hardware (same pattern the legacy reader implementation used).
    /// </summary>
    private readonly Func<string, string, CancellationToken, Task<DeviceBatteryInfo?>>? _testOverride;

    internal GattConnectionManager(int maxConcurrency)
        : this(maxConcurrency, new GattSubscriptionCoordinator(new WinRtGattNotificationSubscription()))
    {
    }

    /// <summary>
    /// Test-friendly overload: the real read-path shape with an injected subscription coordinator.
    /// Two parameters, so ADR-008's options-record threshold is not reached.
    /// </summary>
    internal GattConnectionManager(int maxConcurrency, GattSubscriptionCoordinator subscriptions)
    {
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _subscriptions = subscriptions;
    }

    internal GattConnectionManager(
        Func<string, string, CancellationToken, Task<DeviceBatteryInfo?>> testOverride,
        int maxConcurrency)
        : this(maxConcurrency)
    {
        _testOverride = testOverride;
    }

    internal GattConnectionManager()
        : this(PollingDefaults.GattMaxConcurrentReads) { }

    /// <summary>
    /// Live subscription bookkeeping. Exposed for the suspend/evict/dispose teardown hooks and for
    /// unit tests; production callers must not drive the policy directly.
    /// </summary>
    internal GattSubscriptionCoordinator Subscriptions => _subscriptions;

    /// <summary>
    /// Reads the battery level of a single BLE device via GATT 0x2A19.
    /// Returns <c>null</c> if the device doesn't expose the battery service or the read fails.
    /// All WinRT references are dropped before returning, except for the bounded subscription set.
    /// </summary>
    internal async Task<DeviceBatteryInfo?> TryReadBatteryAsync(
        string deviceId, string fallbackName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_testOverride is not null)
                return await _testOverride(deviceId, fallbackName, ct).ConfigureAwait(false);

            bool subscribed = _subscriptions.IsSubscribed(deviceId);

            return await ReadBatteryCorAsync(
                    deviceId,
                    fallbackName,
                    batteryCacheMode: subscribed ? BluetoothCacheMode.Cached : BluetoothCacheMode.Uncached,
                    attemptSubscribe: !subscribed,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops subscriptions that never produced a notification within the settling window.
    /// Called once per poll cycle by <see cref="BatteryReaderOrchestrator"/> so the settling rule
    /// needs no timer of its own.
    /// </summary>
    internal Task PruneSubscriptionsAsync(CancellationToken ct) => _subscriptions.PruneAsync(ct);

    /// <summary>
    /// Releases every subscription because the machine is going to sleep (#161). A subscription
    /// left open across suspend can leave the CCCD in an inconsistent state on some adapters.
    /// </summary>
    internal Task SuspendSubscriptionsAsync(CancellationToken ct) =>
        _subscriptions.DropAllAsync(GattSubscriptionDropReason.Suspend, ct);

    /// <summary>
    /// Releases the subscription for a device evicted from the known-device cache (#161).
    /// Called before the device is removed from the cache by <see cref="PollingOrchestrator"/>.
    /// </summary>
    internal Task DeviceEvictedAsync(string deviceId, CancellationToken ct) =>
        _subscriptions.DropAsync(deviceId, GattSubscriptionDropReason.Evicted, ct);

    private async Task<DeviceBatteryInfo?> ReadBatteryCorAsync(
        string deviceId,
        string fallbackName,
        BluetoothCacheMode batteryCacheMode,
        bool attemptSubscribe,
        CancellationToken ct)
    {
        try
        {
            var bleDevice = await BluetoothLEDevice.FromIdAsync(deviceId)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (bleDevice is null)
                return null;

            string name = !string.IsNullOrWhiteSpace(bleDevice.Name) ? bleDevice.Name : fallbackName;

            if (bleDevice.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return null;

            var characteristic = await GattBatteryCharacteristicLocator
                .FindBatteryLevelAsync(bleDevice, ct)
                .ConfigureAwait(false);

            if (characteristic is null)
                return null;

            int? level = await ReadBatteryLevelAsync(characteristic, batteryCacheMode, ct).ConfigureAwait(false);

            if (level is null && batteryCacheMode == BluetoothCacheMode.Cached)
            {
                // Watchdog read on a subscribed device. Prefer the value the peripheral pushed;
                // if it has not pushed yet, fall back to a single uncached read so the capability
                // cache and the alert state machine see the same result as the legacy poll path.
                level = _subscriptions.TryGetLastNotificationBattery(deviceId)
                        ?? await ReadBatteryLevelAsync(characteristic, BluetoothCacheMode.Uncached, ct)
                            .ConfigureAwait(false);
            }

            if (level is null)
                return null;

            // Best-effort charging state read — failure must never fail the battery read.
            // Deliberately still uncached even while a subscription is active, so the #146
            // "charging suppresses the High alert" contract never depends on OS cache contents.
            bool? isCharging = await TryReadChargingStateAsync(bleDevice, ct).ConfigureAwait(false);

            // Cache knowledge — this device supports GATT battery.
            lock (_lock) { _knownGattDevices.Add(deviceId); }

            if (attemptSubscribe)
            {
                bool supportsNotify =
                    (characteristic.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0;

                // Bounded, revocable and never forced: the policy decides, and a refusal simply
                // leaves this device on the uncached read path above.
                await _subscriptions
                    .TrySubscribeAsync(deviceId, name, supportsNotify, isConnected: true, ct)
                    .ConfigureAwait(false);
            }

            return new DeviceBatteryInfo(deviceId, name, level, isCharging, Source: BatterySource.Gatt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Debug.WriteLine($"[GattConnectionManager] Timeout reading '{deviceId}'");
            return null;
        }
        catch (Exception ex) when (
            ex is COMException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            Debug.WriteLine($"[GattConnectionManager] Device unavailable '{deviceId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads one byte of Battery Level (0x2A19), returning <c>null</c> when the read did not
    /// succeed or the value is out of range.
    /// </summary>
    private static async Task<int?> ReadBatteryLevelAsync(
        GattCharacteristic characteristic, BluetoothCacheMode cacheMode, CancellationToken ct)
    {
        var readResult = await characteristic
            .ReadValueAsync(cacheMode)
            .AsTask(ct)
            .WaitAsync(WinRtTimeout, ct)
            .ConfigureAwait(false);

        if (readResult.Status != GattCommunicationStatus.Success || readResult.Value.Length == 0)
            return null;

        using var reader = DataReader.FromBuffer(readResult.Value);
        byte value = reader.ReadByte();
        return value > 100 ? null : value;
    }

    /// <summary>
    /// Best-effort read of charging state via BT spec Battery Status (0x2BEA) or
    /// Battery Power State (0x2A1B). Returns null when neither characteristic is present
    /// or the read fails — failure must never surface to the caller.
    /// </summary>
    private async Task<bool?> TryReadChargingStateAsync(
        BluetoothLEDevice device, CancellationToken ct)
    {
        try
        {
            // Try Battery Status 0x2BEA first (BT spec Battery Service 2.0).
            bool? result = await TryReadBatteryStatusAsync(device, ct).ConfigureAwait(false);
            if (result is not null)
                return result;

            // Fall back to Battery Power State 0x2A1B.
            return await TryReadBatteryPowerStateAsync(device, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GattConnectionManager] TryReadChargingStateAsync fault: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads Battery Status characteristic (0x2BEA) from any service.
    /// Lower nibble: 0x01 = Charging, 0x02 = Discharging, 0x05 = Not charging, 0x0F = Full.
    /// </summary>
    private async Task<bool?> TryReadBatteryStatusAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        try
        {
            var allServices = await device.GetGattServicesAsync(BluetoothCacheMode.Cached)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (allServices.Status != GattCommunicationStatus.Success)
                return null;

            foreach (var svc in allServices.Services)
            {
                var chars = await svc.GetCharacteristicsForUuidAsync(BatteryStatusUuid, BluetoothCacheMode.Cached)
                    .AsTask(ct)
                    .WaitAsync(WinRtTimeout, ct)
                    .ConfigureAwait(false);

                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                    continue;

                var readResult = await chars.Characteristics[0]
                    .ReadValueAsync(BluetoothCacheMode.Uncached)
                    .AsTask(ct)
                    .WaitAsync(WinRtTimeout, ct)
                    .ConfigureAwait(false);

                if (readResult.Status != GattCommunicationStatus.Success || readResult.Value.Length == 0)
                    return null;

                using var reader = DataReader.FromBuffer(readResult.Value);
                byte b0 = reader.ReadByte();

                return (b0 & 0x0F) == 0x01;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is COMException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            Debug.WriteLine($"[GattConnectionManager] BatteryStatus read fault: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Reads Battery Power State characteristic (0x2A1B) from any service.
    /// Bits 6-7: 0b11 (0xC0) = Charging, 0b10 (0x80) = Discharging.
    /// </summary>
    private async Task<bool?> TryReadBatteryPowerStateAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        try
        {
            var allServices = await device.GetGattServicesAsync(BluetoothCacheMode.Cached)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (allServices.Status != GattCommunicationStatus.Success)
                return null;

            foreach (var svc in allServices.Services)
            {
                var chars = await svc.GetCharacteristicsForUuidAsync(BatteryPowerStateUuid, BluetoothCacheMode.Cached)
                    .AsTask(ct)
                    .WaitAsync(WinRtTimeout, ct)
                    .ConfigureAwait(false);

                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                    continue;

                var readResult = await chars.Characteristics[0]
                    .ReadValueAsync(BluetoothCacheMode.Uncached)
                    .AsTask(ct)
                    .WaitAsync(WinRtTimeout, ct)
                    .ConfigureAwait(false);

                if (readResult.Status != GattCommunicationStatus.Success || readResult.Value.Length == 0)
                    return null;

                using var reader = DataReader.FromBuffer(readResult.Value);
                byte b0 = reader.ReadByte();

                return (b0 & 0xC0) == 0xC0;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is COMException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            Debug.WriteLine($"[GattConnectionManager] BatteryPowerState read fault: {ex.Message}");
        }

        return null;
    }

    /// <summary>Returns <c>true</c> if <paramref name="deviceId"/> was previously read successfully.</summary>
    internal bool IsKnownGattDevice(string deviceId)
    {
        lock (_lock) { return _knownGattDevices.Contains(deviceId); }
    }

    /// <summary>Clears all cached knowledge (e.g. on sleep/resume).</summary>
    internal void InvalidateAll()
    {
        lock (_lock) { _knownGattDevices.Clear(); }
    }

    /// <summary>
    /// Synchronous fallback for callers that cannot await. Prefer <see cref="DisposeAsync"/>:
    /// it unsubscribes every live subscription before releasing the gate. This overload only
    /// drops bookkeeping, so the WinRT references the seam holds are released by its own
    /// <c>UnsubscribeAsync</c> on the next teardown or by process exit.
    /// </summary>
    public void Dispose()
    {
        _subscriptions.Dispose();
        _gate.Dispose();
    }

    /// <summary>Releases every GATT subscription, then disposes the manager (ADR-007 shutdown path).</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _subscriptions
                .DropAllAsync(GattSubscriptionDropReason.Disposed, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GattConnectionManager] Subscription teardown fault: {ex}");
        }

        Dispose();
    }
}
