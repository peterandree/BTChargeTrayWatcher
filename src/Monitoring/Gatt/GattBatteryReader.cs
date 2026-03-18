using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BTChargeTrayWatcher;

public class GattBatteryReader
{
    private static readonly Guid BatterySvcUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    public Task<List<(string Name, int Battery)>> ReadAllAsync() =>
        ReadAllAsync(CancellationToken.None);

    public async Task<List<(string Name, int Battery)>> ReadAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<(string, int)>();

        try
        {
            string selector = GattDeviceService.GetDeviceSelectorFromUuid(BatterySvcUuid);

            DeviceInformationCollection serviceInfos =
                await DeviceInformation.FindAllAsync(selector)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

            var tasks = serviceInfos
                .Cast<DeviceInformation>()
                .Select(info => ReadDeviceAsync(info, cancellationToken));

            var taskResults = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var (name, battery) in taskResults)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrWhiteSpace(name))
                    results.Add((name, battery));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GattBatteryReader] ReadAllAsync fault: {ex}");
        }

        return results;
    }

    private static async Task<(string? Name, int Battery)> ReadDeviceAsync(
        DeviceInformation serviceInfo,
        CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            device = await BluetoothLEDevice.FromIdAsync(serviceInfo.Id)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (device is null)
                return (null, -1);

            GattDeviceServicesResult svcResult =
                await device.GetGattServicesForUuidAsync(
                        BatterySvcUuid,
                        BluetoothCacheMode.Cached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
                return (null, -1);

            try
            {
                int battery = await ReadBatteryLevelAsync(svcResult.Services[0], cancellationToken)
                    .ConfigureAwait(false);

                if (battery < 0)
                    return (null, -1);

                string name = ResolveDeviceName(device.Name, serviceInfo.Name, serviceInfo.Id);
                return (name, battery);
            }
            finally
            {
                foreach (var svc in svcResult.Services)
                    svc.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GattBatteryReader] ReadDeviceAsync fault for '{serviceInfo.Id}': {ex}");
            return (null, -1);
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static async Task<int> ReadBatteryLevelAsync(
        GattDeviceService service,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            GattCharacteristicsResult charResult =
                await service.GetCharacteristicsForUuidAsync(
                        BatteryLevelUuid,
                        BluetoothCacheMode.Cached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

            if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
                return -1;

            GattReadResult readResult =
                await charResult.Characteristics[0]
                    .ReadValueAsync(BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

            if (readResult.Status != GattCommunicationStatus.Success)
                return -1;

            using DataReader reader = DataReader.FromBuffer(readResult.Value);
            byte value = reader.ReadByte();

            return value is >= 0 and <= 100 ? value : -1;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GattBatteryReader] ReadBatteryLevelAsync fault: {ex}");
            return -1;
        }
    }

    private static string ResolveDeviceName(params string?[] candidates)
    {
        foreach (string? s in candidates)
        {
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }

        return "Unknown";
    }
}
