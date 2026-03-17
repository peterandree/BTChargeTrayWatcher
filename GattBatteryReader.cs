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
            // Enumerate only devices that actually advertise the Battery Service UUID.
            // This is the correct filter — pairing state alone is too broad.
            string selector = GattDeviceService.GetDeviceSelectorFromUuid(BatterySvcUuid);
            DeviceInformationCollection serviceInfos =
                await DeviceInformation.FindAllAsync(selector);

            var tasks = serviceInfos.Cast<DeviceInformation>().Select(ReadDeviceAsync);
            var taskResults = await Task.WhenAll(tasks);

            foreach (var (name, battery) in taskResults)
                if (name is not null)
                    results.Add((name, battery));
        }
        catch { }

        return results;
    }

    private static async Task<(string? Name, int Battery)> ReadDeviceAsync(DeviceInformation serviceInfo)
    {
        BluetoothLEDevice? device = null;
        try
        {
            // Open the device via the service's DeviceId — avoids a second FromIdAsync call
            device = await BluetoothLEDevice.FromIdAsync(
                serviceInfo.Properties.TryGetValue("System.Devices.ContainerId", out _)
                    ? serviceInfo.Id
                    : serviceInfo.Id);

            if (device is null) return (null, -1);

            // GetGattServicesForUuidAsync on the already-open device — no extra driver call
            GattDeviceServicesResult svcResult =
                await device.GetGattServicesForUuidAsync(
                    BatterySvcUuid, BluetoothCacheMode.Cached);

            if (svcResult.Status != GattCommunicationStatus.Success
                || svcResult.Services.Count == 0)
                return (null, -1);

            try
            {
                int battery = await ReadBatteryLevelAsync(svcResult.Services[0]);
                if (battery < 0) return (null, -1);

                string name = ResolveDeviceName(device.Name, serviceInfo.Name, serviceInfo.Id);
                return (name, battery);
            }
            finally
            {
                foreach (var svc in svcResult.Services)
                    svc.Dispose();
            }
        }
        catch { return (null, -1); }
        finally { device?.Dispose(); }
    }

    private static async Task<int> ReadBatteryLevelAsync(GattDeviceService service)
    {
        try
        {
            // Cached is fine for characteristic *discovery* — it is metadata, not a live value
            GattCharacteristicsResult charResult =
                await service.GetCharacteristicsForUuidAsync(
                    BatteryLevelUuid, BluetoothCacheMode.Cached);

            if (charResult.Status != GattCommunicationStatus.Success
                || charResult.Characteristics.Count == 0)
                return -1;

            // Always Uncached for the actual byte — Windows cache is stale until first live read
            GattReadResult readResult =
                await charResult.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);

            if (readResult.Status != GattCommunicationStatus.Success)
                return -1;

            using DataReader reader = DataReader.FromBuffer(readResult.Value);
            byte value = reader.ReadByte();
            return value is >= 0 and <= 100 ? value : -1;
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
