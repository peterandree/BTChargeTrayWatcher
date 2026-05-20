using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BTChargeTrayWatcher;

internal sealed class GattBatteryProcessor
{
    private static readonly Guid BatterySvcUuid         = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid       = new("00002a19-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryStatusUuid      = new("00002bea-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryPowerStateUuid  = new("00002a1b-0000-1000-8000-00805f9b34fb");

    private readonly GattConnectionCache _cache;
    private readonly Func<string, string, CancellationToken, Task<GattDeviceReadResult>>? _testProcessOverride;

    public GattBatteryProcessor(GattConnectionCache cache)
    {
        _cache = cache;
    }

    // Internal constructor for tests that allows injecting a deterministic processor override
    internal GattBatteryProcessor(GattConnectionCache cache, Func<string, string, CancellationToken, Task<GattDeviceReadResult>>? testProcessOverride)
    {
        _cache = cache;
        _testProcessOverride = testProcessOverride;
    }

    public async Task<GattDeviceReadResult> ProcessDeviceAsync(
        string deviceId, string fallbackName, CancellationToken cancellationToken)
    {
        if (_testProcessOverride is not null)
        {
            try
            {
                return await _testProcessOverride(deviceId, fallbackName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GattBatteryProcessor][TestOverride] fault: {ex.Message}");
                return new GattDeviceReadResult(deviceId, fallbackName, null);
            }
        }
        var device = await GetOrCreateDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
            return new GattDeviceReadResult(deviceId, fallbackName, null);

        string deviceName = GetDeviceName(device) ?? fallbackName;

        // If the cached device is stale (disconnected), evict it and try once with a fresh instance.
        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            _cache.RemoveDevice(deviceId);
            device = await GetOrCreateDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
            if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return new GattDeviceReadResult(deviceId, deviceName, null);

            deviceName = GetDeviceName(device) ?? deviceName;
        }

        var cachedEndpoint = _cache.GetEndpoint(deviceId);
        if (cachedEndpoint is not null)
        {
            try
            {
                int? battery = await ReadCharacteristicValueAsync(cachedEndpoint.Characteristic, cancellationToken).ConfigureAwait(false);
                if (battery.HasValue)
                {
                    bool? isCharging = await TryReadChargingStateAsync(device, cancellationToken).ConfigureAwait(false);
                    return new GattDeviceReadResult(deviceId, deviceName, battery, isCharging);
                }
            }
            catch (Exception ex) when (IsExpectedBluetoothException(ex) || ex is ObjectDisposedException)
            {
                Debug.WriteLine($"[GattBatteryProcessor] Cached characteristic failed: {ex.Message}");
                // Evict the cached endpoint because it failed.
                _cache.RemoveEndpoint(deviceId);

                // Also remove any cached BluetoothLEDevice to avoid stale WinRT instances
                // and force a fresh FromIdAsync on the subsequent attempt.
                _cache.RemoveDevice(deviceId);

                // Attempt to obtain a fresh device instance. If we cannot, give up.
                device = await GetOrCreateDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
                if (device is null)
                    return new GattDeviceReadResult(deviceId, deviceName, null);

                if (device.ConnectionStatus != Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected)
                {
                    // Device still not connected; evict and give up this cycle.
                    _cache.RemoveDevice(deviceId);
                    return new GattDeviceReadResult(deviceId, deviceName, null);
                }

                // Update display name from fresh device if available and continue with discovery.
                deviceName = GetDeviceName(device) ?? deviceName;
            }
        }

        try
        {
            var servicesResult = await device.GetGattServicesForUuidAsync(BatterySvcUuid, BluetoothCacheMode.Cached)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                return new GattDeviceReadResult(deviceId, deviceName, null);

            var service = servicesResult.Services[0];
            var charsResult = await service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Cached)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
                return new GattDeviceReadResult(deviceId, deviceName, null);

            var characteristic = charsResult.Characteristics[0];
            _cache.SetEndpoint(deviceId, new CachedGattEndpoint(service, characteristic));

            int? battery = await ReadCharacteristicValueAsync(characteristic, cancellationToken).ConfigureAwait(false);
            bool? isCharging = await TryReadChargingStateAsync(device, cancellationToken).ConfigureAwait(false);
            return new GattDeviceReadResult(deviceId, deviceName, battery, isCharging);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GattBatteryProcessor] ProcessDeviceAsync failed for '{deviceId}': {ex}");
            return new GattDeviceReadResult(deviceId, deviceName, null);
        }
    }

    /// <summary>
    /// Best-effort read of charging state via BT spec Battery Status (0x2BEA) or
    /// Battery Power State (0x2A1B). Returns null when neither characteristic is present
    /// or the read fails — failure must never surface to the caller.
    /// </summary>
    private async Task<bool?> TryReadChargingStateAsync(
        BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        try
        {
            // Try Battery Status 0x2BEA first (BT spec Battery Service 2.0).
            bool? result = await TryReadBatteryStatusCharacteristicAsync(device, cancellationToken).ConfigureAwait(false);
            if (result is not null)
                return result;

            // Fall back to Battery Power State 0x2A1B.
            return await TryReadBatteryPowerStateCharacteristicAsync(device, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GattBatteryProcessor] TryReadChargingStateAsync fault: {ex.Message}");
            return null;
        }
    }

    private async Task<bool?> TryReadBatteryStatusCharacteristicAsync(
        BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        try
        {
            // Battery Status service UUID reuses 0x180F in some stacks; characteristic is 0x2BEA.
            // We scan all services for the characteristic UUID directly.
            var allServices = await device.GetGattServicesAsync(BluetoothCacheMode.Cached)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (allServices.Status != GattCommunicationStatus.Success)
                return null;

            foreach (var svc in allServices.Services)
            {
                var chars = await svc.GetCharacteristicsForUuidAsync(BatteryStatusUuid, BluetoothCacheMode.Cached)
                    .AsTask(cancellationToken).ConfigureAwait(false);

                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                    continue;

                var readResult = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken).ConfigureAwait(false);

                if (readResult.Status != GattCommunicationStatus.Success || readResult.Value.Length == 0)
                    return null;

                using var reader = DataReader.FromBuffer(readResult.Value);
                byte b0 = reader.ReadByte();

                // Lower nibble: 0x01 = Charging, 0x02 = Discharging, 0x05 = Not charging, 0x0F = Full.
                return (b0 & 0x0F) == 0x01;
            }
        }
        catch (Exception ex) when (IsExpectedBluetoothException(ex) || ex is OperationCanceledException)
        {
            if (ex is OperationCanceledException) throw;
            Debug.WriteLine($"[GattBatteryProcessor] BatteryStatus read fault: {ex.Message}");
        }

        return null;
    }

    private async Task<bool?> TryReadBatteryPowerStateCharacteristicAsync(
        BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        try
        {
            var allServices = await device.GetGattServicesAsync(BluetoothCacheMode.Cached)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (allServices.Status != GattCommunicationStatus.Success)
                return null;

            foreach (var svc in allServices.Services)
            {
                var chars = await svc.GetCharacteristicsForUuidAsync(BatteryPowerStateUuid, BluetoothCacheMode.Cached)
                    .AsTask(cancellationToken).ConfigureAwait(false);

                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                    continue;

                var readResult = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken).ConfigureAwait(false);

                if (readResult.Status != GattCommunicationStatus.Success || readResult.Value.Length == 0)
                    return null;

                using var reader = DataReader.FromBuffer(readResult.Value);
                byte b0 = reader.ReadByte();

                // Bits 6-7: 0b11 (0xC0) = Charging, 0b10 (0x80) = Discharging.
                return (b0 & 0xC0) == 0xC0;
            }
        }
        catch (Exception ex) when (IsExpectedBluetoothException(ex) || ex is OperationCanceledException)
        {
            if (ex is OperationCanceledException) throw;
            Debug.WriteLine($"[GattBatteryProcessor] BatteryPowerState read fault: {ex.Message}");
        }

        return null;
    }

    private async Task<BluetoothLEDevice?> GetOrCreateDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        var existing = _cache.GetDevice(deviceId);
        if (existing is not null)
            return existing;

        try
        {
            var device = await BluetoothLEDevice.FromIdAsync(deviceId).AsTask(cancellationToken).ConfigureAwait(false);
            if (device != null)
                _cache.SetDevice(deviceId, device);

            return device;
        }
        catch (Exception ex) when (IsExpectedBluetoothException(ex))
        {
            return null;
        }
    }

    private static string? GetDeviceName(BluetoothLEDevice device)
    {
        try
        {
            string? name = device.Name;
            return !string.IsNullOrWhiteSpace(name) ? name : device.BluetoothAddress.ToString("X");
        }
        catch
        {
            return device.BluetoothAddress.ToString("X");
        }
    }

    private static async Task<int?> ReadCharacteristicValueAsync(
        GattCharacteristic characteristic, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var readResult = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);

        if (readResult.Status != GattCommunicationStatus.Success)
            return null;

        if (readResult.Value.Length == 0)
            return null;

        using DataReader reader = DataReader.FromBuffer(readResult.Value);
        byte value = reader.ReadByte();

        return value <= 100 ? value : null;
    }

    public static bool IsExpectedBluetoothException(Exception ex)
    {
        return ex is COMException || ex is UnauthorizedAccessException || ex is InvalidOperationException;
    }
}

internal sealed record GattDeviceReadResult(string DeviceId, string Name, int? Battery, bool? IsCharging = null);
