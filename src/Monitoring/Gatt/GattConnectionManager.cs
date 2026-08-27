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
/// </summary>
internal sealed class GattConnectionManager : IDisposable
{
    private static readonly Guid BatterySvcUuid         = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid CommonBatterySvcUuid   = new("0000182b-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid       = new("00002a19-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryStatusUuid      = new("00002bea-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryPowerStateUuid  = new("00002a1b-0000-1000-8000-00805f9b34fb");
    private static readonly TimeSpan WinRtTimeout = TimeSpan.FromSeconds(2);

    private readonly HashSet<string> _knownGattDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly Lock _lock = new();

    /// <summary>
    /// Test seam: replaces the entire WinRT read path so the concurrency gate,
    /// cancellation plumbing, and result contract can be unit-tested without
    /// hardware (same pattern the legacy reader implementation used).
    /// </summary>
    private readonly Func<string, string, CancellationToken, Task<DeviceBatteryInfo?>>? _testOverride;

    internal GattConnectionManager(int maxConcurrency)
    {
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
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
    /// Reads the battery level of a single BLE device via GATT 0x2A19.
    /// Returns <c>null</c> if the device doesn't expose the battery service or the read fails.
    /// All WinRT references are dropped before returning.
    /// </summary>
    internal async Task<DeviceBatteryInfo?> TryReadBatteryAsync(
        string deviceId, string fallbackName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_testOverride is not null)
                return await _testOverride(deviceId, fallbackName, ct).ConfigureAwait(false);

            return await ReadBatteryCorAsync(deviceId, fallbackName, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DeviceBatteryInfo?> ReadBatteryCorAsync(
        string deviceId, string fallbackName, CancellationToken ct)
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

            // Try classic Battery Service (0x180F) first, then Common Battery Service (0x182B).
            // Both expose Battery Level 0x2A19; 0x182B is used by many newer Windows 11-era devices.
            var characteristic = await FindBatteryLevelCharacteristicAsync(bleDevice, ct).ConfigureAwait(false);
            if (characteristic is null)
                return null;

            var readResult = await characteristic
                .ReadValueAsync(BluetoothCacheMode.Uncached)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (readResult.Status != GattCommunicationStatus.Success ||
                readResult.Value.Length == 0)
                return null;

            using var reader = DataReader.FromBuffer(readResult.Value);
            byte value = reader.ReadByte();
            if (value > 100) return null;

            // Best-effort charging state read — failure must never fail the battery read.
            bool? isCharging = await TryReadChargingStateAsync(bleDevice, ct).ConfigureAwait(false);

            // Cache knowledge — this device supports GATT battery.
            lock (_lock) { _knownGattDevices.Add(deviceId); }

            return new DeviceBatteryInfo(deviceId, name, value, isCharging, Source: BatterySource.Gatt);
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
    /// Looks up the Battery Level characteristic (0x2A19) by trying the classic
    /// Battery Service (0x180F) first, then the Common Battery Service (0x182B).
    /// Returns the characteristic on success, or null if neither service is present.
    /// </summary>
    private async Task<GattCharacteristic?> FindBatteryLevelCharacteristicAsync(
        BluetoothLEDevice device, CancellationToken ct)
    {
        Guid[] serviceUuids = [BatterySvcUuid, CommonBatterySvcUuid];

        foreach (var svcUuid in serviceUuids)
        {
            var servicesResult = await device
                .GetGattServicesForUuidAsync(svcUuid, BluetoothCacheMode.Cached)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (servicesResult.Status != GattCommunicationStatus.Success ||
                servicesResult.Services.Count == 0)
                continue;

            var charsResult = await servicesResult.Services[0]
                .GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Cached)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (charsResult.Status == GattCommunicationStatus.Success &&
                charsResult.Characteristics.Count > 0)
                return charsResult.Characteristics[0];
        }

        return null;
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

    public void Dispose() => _gate.Dispose();
}
