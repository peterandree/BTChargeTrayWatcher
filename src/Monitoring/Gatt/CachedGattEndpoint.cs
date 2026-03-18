using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BTChargeTrayWatcher;

internal sealed class CachedGattEndpoint : IDisposable
{
    public GattDeviceService Service { get; }
    public GattCharacteristic Characteristic { get; }

    public CachedGattEndpoint(GattDeviceService service, GattCharacteristic characteristic)
    {
        Service = service;
        Characteristic = characteristic;
    }

    public void Dispose()
    {
        try { Service.Dispose(); } catch { }
    }
}
