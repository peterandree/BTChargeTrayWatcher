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

public class GattBatteryReader : IDisposable, IBatteryReader
{
    private static readonly Guid BatterySvcUuid = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid = new Guid("00002a19-0000-1000-8000-00805f9b34fb");

    private static readonly TimeSpan PerDeviceTimeout = TimeSpan.FromSeconds(4);
    private readonly SemaphoreSlim _deviceReadGate = new(2, 2);

    private readonly ConcurrentDictionary<string, BluetoothLEDevice> _deviceCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, GattCharacteristic> _characteristicCache =
        new(StringComparer.OrdinalIgnoreCase);

    public GattBatteryReader() { }

    public void Dispose()
    {
        foreach (var kvp in _deviceCache)
        {
            if (kvp.Value is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
        _characteristicCache.Clear();
        _deviceCache.Clear();
        _deviceReadGate.Dispose();
    }

    public Task<List<(string Name, int Battery)>> ReadAllAsync() =>
        ReadAllAsync(CancellationToken.None);

    public Task<List<(string Name, int Battery)>> ReadAllAsync(CancellationToken cancellationToken) =>
        ReadAllInternalAsync(cancellationToken);

    private async Task<List<(string Name, int Battery)>> ReadAllInternalAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string selector = GattDeviceService.GetDeviceSelectorFromUuid(BatterySvcUuid);
        DeviceInformationCollection dis =
            await DeviceInformation.FindAllAsync(selector)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

        var currentDeviceIds = new HashSet<string>(dis.Count);
        foreach (DeviceInformation info in dis)
        {
            currentDeviceIds.Add(info.Id);
        }

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
                _characteristicCache.TryRemove(deviceId, out _);
            }
        }

        var results = new List<(string Name, int Battery)>(dis.Count);
        var readTasks = new List<Task<(string Name, int Battery)>>(dis.Count);

        foreach (DeviceInformation info in dis)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readTasks.Add(ProcessDeviceBoundedAsync(info.Id, info.Name, cancellationToken));
        }

        var perDeviceResults = await Task.WhenAll(readTasks).ConfigureAwait(false);

        foreach (var res in perDeviceResults)
        {
            if (!string.IsNullOrWhiteSpace(res.Name))
                results.Add(res);
        }

        return results;
    }

    private async Task<(string Name, int Battery)> ProcessDeviceBoundedAsync(
        string deviceId,
        string fallbackName,
        CancellationToken cancellationToken)
    {
        await _deviceReadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(PerDeviceTimeout);

            try
            {
                return await ProcessDeviceAsync(deviceId, fallbackName, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Debug.WriteLine($"[GattBatteryReader] Timeout while reading '{deviceId}'.");
                return (fallbackName, -1);
            }
        }
        finally
        {
            _deviceReadGate.Release();
        }
    }

    private async Task<(string Name, int Battery)> ProcessDeviceAsync(
        string deviceId,
        string fallbackName,
        CancellationToken cancellationToken)
    {
        var device = await GetOrCreateDeviceAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
            return (fallbackName, -1);

        string deviceName = await GetDeviceNameAsync(device, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = fallbackName;

        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            return (deviceName, -1);

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
                Debug.WriteLine(
                    $"[GattBatteryReader] Cached characteristic for {deviceId} failed: {ex}");
            }
        }

        try
        {
            GattDeviceServicesResult servicesResult =
                await device.GetGattServicesForUuidAsync(
                        BatterySvcUuid,
                        BluetoothCacheMode.Cached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

            if (servicesResult.Status != GattCommunicationStatus.Success ||
                servicesResult.Services.Count == 0)
            {
                return (deviceName, -1);
            }

            GattDeviceService service = servicesResult.Services[0];

            GattCharacteristicsResult charsResult =
                await service.GetCharacteristicsForUuidAsync(
                        BatteryLevelUuid,
                        BluetoothCacheMode.Cached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

            if (charsResult.Status != GattCommunicationStatus.Success ||
                charsResult.Characteristics.Count == 0)
            {
                return (deviceName, -1);
            }

            GattCharacteristic characteristic = charsResult.Characteristics[0];

            _characteristicCache[deviceId] = characteristic;

            int battery = await ReadCharacteristicValueAsync(characteristic, cancellationToken)
                .ConfigureAwait(false);

            if (battery >= 0)
                return (deviceName, battery);

            return (deviceName, -1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[GattBatteryReader] ProcessDeviceAsync failed for '{deviceId}': {ex}");
            return (deviceName, -1);
        }
    }

    private async Task<BluetoothLEDevice?> GetOrCreateDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (_deviceCache.TryGetValue(deviceId, out var existing) && existing != null)
            return existing;

        BluetoothLEDevice? device =
            await BluetoothLEDevice.FromIdAsync(deviceId)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

        if (device != null)
        {
            _deviceCache[deviceId] = device;
        }
        else
        {
            _deviceCache.TryRemove(deviceId, out _);
        }

        return device;
    }

    private async Task<string> GetDeviceNameAsync(
        BluetoothLEDevice device,
        CancellationToken cancellationToken)
    {
        try
        {
            string? name = device.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            return device.BluetoothAddress.ToString("X");
        }
        catch
        {
            return device.BluetoothAddress.ToString("X");
        }
    }

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
