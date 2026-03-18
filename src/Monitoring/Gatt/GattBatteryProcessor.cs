using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BTChargeTrayWatcher;

internal sealed class GattBatteryProcessor
{
    private static readonly Guid BatterySvcUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    private readonly GattConnectionCache _cache;

    public GattBatteryProcessor(GattConnectionCache cache)
    {
        _cache = cache;
    }

    public async Task<(string Name, int Battery)> ProcessDeviceAsync(string deviceId, string fallbackName, CancellationToken cancellationToken)
    {
        var device = await GetOrCreateDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
            return (fallbackName, -1);

        string deviceName = GetDeviceName(device) ?? fallbackName;

        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            return (deviceName, -1);

        var cachedEndpoint = _cache.GetEndpoint(deviceId);
        if (cachedEndpoint is not null)
        {
            try
            {
                int battery = await ReadCharacteristicValueAsync(cachedEndpoint.Characteristic, cancellationToken).ConfigureAwait(false);
                if (battery >= 0)
                    return (deviceName, battery);
            }
            catch (Exception ex) when (IsExpectedBluetoothException(ex) || ex is ObjectDisposedException)
            {
                Debug.WriteLine($"[GattBatteryProcessor] Cached characteristic failed: {ex.Message}");
                _cache.RemoveEndpoint(deviceId);
            }
        }

        try
        {
            var servicesResult = await device.GetGattServicesForUuidAsync(BatterySvcUuid, BluetoothCacheMode.Cached)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                return (deviceName, -1);

            var service = servicesResult.Services[0];
            var charsResult = await service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Cached)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
                return (deviceName, -1);

            var characteristic = charsResult.Characteristics[0];
            _cache.SetEndpoint(deviceId, new CachedGattEndpoint(service, characteristic));

            int battery = await ReadCharacteristicValueAsync(characteristic, cancellationToken).ConfigureAwait(false);
            return (deviceName, battery >= 0 ? battery : -1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GattBatteryProcessor] ProcessDeviceAsync failed for '{deviceId}': {ex}");
            return (deviceName, -1);
        }
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

    private static async Task<int> ReadCharacteristicValueAsync(GattCharacteristic characteristic, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var readResult = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);

        if (readResult.Status != GattCommunicationStatus.Success)
            return -1;

        using DataReader reader = DataReader.FromBuffer(readResult.Value);
        byte value = reader.ReadByte();

        return value is >= 0 and <= 100 ? value : -1;
    }

    public static bool IsExpectedBluetoothException(Exception ex)
    {
        return ex is COMException || ex is UnauthorizedAccessException || ex is InvalidOperationException;
    }
}
