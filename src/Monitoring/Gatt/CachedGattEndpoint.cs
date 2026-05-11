using System;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BTChargeTrayWatcher;

internal sealed class CachedGattEndpoint : IDisposable
{
    public GattDeviceService Service { get; }
    public GattCharacteristic Characteristic { get; }

    private readonly IDisposable? _testDisposable;

    public CachedGattEndpoint(GattDeviceService service, GattCharacteristic characteristic)
    {
        Service = service;
        Characteristic = characteristic;
    }

    // Internal constructor for tests: accepts any IDisposable which will be invoked when
    // the endpoint is disposed. This allows testing eviction behavior without real
    // WinRT `GattDeviceService` objects.
    internal CachedGattEndpoint(IDisposable disposable)
    {
        _testDisposable = disposable;
        Service = null!;
        Characteristic = null!;
    }

    public void Dispose()
    {
        try
        {
            if (_testDisposable is not null)
            {
                _testDisposable.Dispose();
            }
            else
            {
                Service.Dispose();
            }
        }
        catch { }
    }
}
