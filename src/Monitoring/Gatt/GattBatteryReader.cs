using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BTChargeTrayWatcher;

/// <summary>
/// Reads battery level from Bluetooth LE devices using the GATT Battery Service.
/// Optimized with cached BluetoothLEDevice instances and cached GattCharacteristic objects.
/// </summary>
public class GattBatteryReader : IDisposable
{
    // ---- UUIDs (standard Battery Service) ----
    private static readonly Guid BatterySvcUuid = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid = new Guid("00002a19-0000-1000-8000-00805f9b34fb");

    // ---- Caches -------------------------------------------------
    // Maps device ID -> BluetoothLEDevice (live device object, reused across polls)
    private readonly ConcurrentDictionary<string, BluetoothLEDevice> _deviceCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Maps device ID -> GattCharacteristic (the Battery Level characteristic)
    private readonly ConcurrentDictionary<string, GattCharacteristic> _characteristicCache =
        new(StringComparer.OrdinalIgnoreCase);

    public GattBatteryReader() { }

    public void Dispose()
    {
        // Dispose cached characteristics and devices via IDisposable (the .NET projection of IClosable)
        foreach (var kvp in _characteristicCache)
        {
            if (kvp.Value is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
        foreach (var kvp in _deviceCache)
        {
            if (kvp.Value is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
        _characteristicCache.Clear();
        _deviceCache.Clear();
    }

    // -----------------------------------------------------------------
    // Public API – same signature as before
    // -----------------------------------------------------------------
    public Task<List<(string Name, int Battery)>> ReadAllAsync() =>
        ReadAllAsync(CancellationToken.None);

    public Task<List<(string Name, int Battery)>> ReadAllAsync(CancellationToken cancellationToken) =>
        ReadAllInternalAsync(cancellationToken);

    private async Task<List<(string Name, int Battery)>> ReadAllInternalAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1️⃣ Find all devices advertising the Battery Service (selector is cheap and OS‑optimized)
        string selector = GattDeviceService.GetDeviceSelectorFromUuid(BatterySvcUuid);
        DeviceInformationCollection dis =
            await DeviceInformation.FindAllAsync(selector)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

        // 2️⃣ Remove cached entries for devices that are no longer present
        var currentDeviceIds = new HashSet<string>(dis.Count);
        foreach (DeviceInformation info in dis)
        {
            currentDeviceIds.Add(info.Id);
        }

        // Remove from caches any device ID not in the current list
        foreach (var deviceId in _deviceCache.Keys)
        {
            if (!currentDeviceIds.Contains(deviceId))
            {
                _deviceCache.TryRemove(deviceId, out var device);
                if (device is IDisposable d) d.Dispose();
            }
        }
        foreach (var deviceId in _characteristicCache.Keys)
        {
            if (!currentDeviceIds.Contains(deviceId))
            {
                _characteristicCache.TryRemove(deviceId, out var characteristic);
                if (characteristic is IDisposable c) c.Dispose();
            }
        }

        // 3️⃣ Process each present device
        var results = new List<(string Name, int Battery)>(dis.Count);
        var readTasks = new List<Task<(string Name, int Battery)>>(dis.Count);

        foreach (DeviceInformation info in dis)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readTasks.Add(ProcessDeviceAsync(info.Id, cancellationToken));
        }

        // Wait for all device reads to complete
        var perDeviceResults = await Task.WhenAll(readTasks).ConfigureAwait(false);

        foreach (var res in perDeviceResults)
        {
            if (!string.IsNullOrWhiteSpace(res.Name))
                results.Add(res);
        }

        return results;
    }

    // -----------------------------------------------------------------
    // Process a single device: try cached characteristic, fallback to rediscovery
    // -----------------------------------------------------------------
    private async Task<(string Name, int Battery)> ProcessDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        // Ensure we have a BluetoothLEDevice object for this device (create if needed)
        var device = await GetOrCreateDeviceAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);
        if (device is null)
            return (string.Empty, -1);

        // Get a friendly name (we can use the device's Name property)
        string deviceName = await GetDeviceNameAsync(device, cancellationToken)
            .ConfigureAwait(false);

        // ---- Try cached characteristic first ----
        if (_characteristicCache.TryGetValue(deviceId, out GattCharacteristic? cachedChar))
        {
            try
            {
                int battery = await ReadCharacteristicValueAsync(cachedChar, cancellationToken)
                    .ConfigureAwait(false);
                if (battery >= 0)
                    return (deviceName, battery);
            }
            catch (Exception ex) when (ex is ObjectDisposedException ||
                                       ex is InvalidOperationException)
            {
                // Cached characteristic may have become invalid – fall through to rediscovery
                Debug.WriteLine(
                    $"[GattBatteryReader] Cached characteristic for {deviceId} failed: {ex}");
            }
        }

        // ---- Rediscover service and characteristic (cached mode for lookups) ----
        try
        {
            // Get services (cached)
            GattDeviceServicesResult servicesResult =
                await device.GetGattServicesForUuidAsync(
                        BatterySvcUuid,
                        BluetoothCacheMode.Cached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

            if (servicesResult.Status != GattCommunicationStatus.Success ||
                servicesResult.Services.Count == 0)
            {
                return (string.Empty, -1);
            }

            GattDeviceService service = servicesResult.Services[0];

            // Get the characteristic (cached)
            GattCharacteristicsResult charsResult =
                await service.GetCharacteristicsForUuidAsync(
                        BatteryLevelUuid,
                        BluetoothCacheMode.Cached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

            if (charsResult.Status != GattCommunicationStatus.Success ||
                charsResult.Characteristics.Count == 0)
            {
                return (string.Empty, -1);
            }

            GattCharacteristic characteristic = charsResult.Characteristics[0];

            // Update cache
            _characteristicCache[deviceId] = characteristic;

            // Read the value (uncached to get a fresh battery level)
            int battery = await ReadCharacteristicValueAsync(characteristic, cancellationToken)
                .ConfigureAwait(false);

            if (battery >= 0)
                return (deviceName, battery);

            return (string.Empty, -1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[GattBatteryReader] ProcessDeviceAsync failed for '{deviceId}': {ex}");
            return (string.Empty, -1);
        }
    }

    // -----------------------------------------------------------------
    // Helper: get (or create) the BluetoothLEDevice for a given ID
    // -----------------------------------------------------------------
    private async Task<BluetoothLEDevice?> GetOrCreateDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        // Fast path: we already have a live device object
        if (_deviceCache.TryGetValue(deviceId, out var existing) && existing != null)
            return existing;

        // Slow path: create (or recreate) the BluetoothLEDevice
        BluetoothLEDevice? device =
            await BluetoothLEDevice.FromIdAsync(deviceId)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

        if (device != null)
        {
            // Store it for future use
            _deviceCache[deviceId] = device;
        }
        else
        {
            // Ensure we don’t leave a null entry that would cause repeated attempts
            _deviceCache[deviceId] = null;
        }

        return device;
    }

    // -----------------------------------------------------------------
    // Helper: resolve a friendly name for the device
    // -----------------------------------------------------------------
    private async Task<string> GetDeviceNameAsync(
        BluetoothLEDevice device,
        CancellationToken cancellationToken)
    {
        try
        {
            string? name = device.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            // Fallback: we could use the device ID, but the contract expects a Name.
            // We'll return the device's Bluetooth address as a fallback.
            return device.BluetoothAddress.ToString("X");
        }
        catch
        {
            // If anything goes wrong, return the device's Bluetooth address
            return device.BluetoothAddress.ToString("X");
        }
    }

    // -----------------------------------------------------------------
    // Helper: read a single byte from the characteristic (0‑100)
    // -----------------------------------------------------------------
    private static async Task<int> ReadCharacteristicValueAsync(
        GattCharacteristic characteristic,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GattReadResult readResult =
            await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

        if (readResult.Status != GattCommunicationStatus.Success)
            return -1;

        using DataReader reader = DataReader.FromBuffer(readResult.Value);
        byte value = reader.ReadByte();

        return value is >= 0 and <= 100 ? value : -1;
    }
}
