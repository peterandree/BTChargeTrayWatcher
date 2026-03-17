using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BTChargeTrayWatcher;

public class GattBatteryReader
{
    private static readonly Guid BatterySvcUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    public async Task<List<(string Name, int Battery)>> ReadAllAsync()
    {
        var results = new List<(string, int)>();
        try
        {
            string selector = GattDeviceService.GetDeviceSelectorFromUuid(BatterySvcUuid);
            DeviceInformationCollection serviceInfos =
                await DeviceInformation.FindAllAsync(selector);

            var tasks = serviceInfos.Cast<DeviceInformation>().Select(async serviceInfo =>
            {
                GattDeviceService? service = null;
                try
                {
                    service = await GattDeviceService.FromIdAsync(serviceInfo.Id);
                    if (service is null) return ((string?)null, -1);

                    string name = await ResolveNameAsync(service, serviceInfo);
                    int battery = await ReadFromServiceAsync(service);
                    return ((string?)name, battery);
                }
                catch { return ((string?)null, -1); }
                finally { service?.Dispose(); }
            });

            var taskResults = await Task.WhenAll(tasks);
            foreach (var (name, battery) in taskResults)
                if (name is not null) results.Add((name, battery));
        }
        catch { }

        return results;
    }

    private static async Task<string> ResolveNameAsync(
        GattDeviceService service, DeviceInformation fallback)
    {
        try
        {
            using BluetoothLEDevice? dev =
                await BluetoothLEDevice.FromIdAsync(service.DeviceId);
            return ResolveDeviceName(dev?.Name, fallback.Name, fallback.Id);
        }
        catch
        {
            return ResolveDeviceName(fallback.Name, fallback.Id);
        }
    }

    private static async Task<int> ReadFromServiceAsync(GattDeviceService service)
    {
        try
        {
            GattCharacteristicsResult charResult =
                await service.GetCharacteristicsForUuidAsync(
                    BatteryLevelUuid, BluetoothCacheMode.Uncached);

            if (charResult.Status != GattCommunicationStatus.Success
                || charResult.Characteristics.Count == 0)
                return -1;

            GattReadResult readResult =
                await charResult.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);

            if (readResult.Status != GattCommunicationStatus.Success)
                return -1;

            using DataReader reader = DataReader.FromBuffer(readResult.Value);
            return reader.ReadByte();
        }
        catch { return -1; }
    }

    private static string ResolveDeviceName(params string?[] candidates)
    {
        foreach (string? s in candidates)
            if (!string.IsNullOrWhiteSpace(s)) return s!;
        return "Unknown";
    }
}
