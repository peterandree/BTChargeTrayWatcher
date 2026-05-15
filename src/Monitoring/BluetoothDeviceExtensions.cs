using Windows.Devices.Enumeration;

namespace BTChargeTrayWatcher;

/// <summary>
/// Thin extension methods that extract Bluetooth transport/identity properties
/// from a <see cref="DeviceInformation"/>. Not unit-tested directly (WinRT
/// dependency); logic is tested via <see cref="DeviceProfileClassifier"/>.
/// </summary>
internal static class BluetoothDeviceExtensions
{
    // AEP (Association Endpoint) property keys used by Windows Bluetooth stack.
    private const string BleAppearanceProperty = "System.Devices.Aep.Bluetooth.Le.Appearance";
    private const string ContainerIdProperty = "System.Devices.ContainerId";
    private const string DeviceAddressProperty = "System.Devices.Aep.DeviceAddress";
    private const string ClassOfDeviceProperty = "System.Devices.Aep.Bluetooth.Cod.Major";

    /// <summary>Returns <c>true</c> if the device was enumerated as a BLE device.</summary>
    internal static bool IsBleDevice(this DeviceInformation device) =>
        device.Properties.ContainsKey(BleAppearanceProperty);

    /// <summary>Extracts the ContainerId (stable across RPA changes), or <c>null</c>.</summary>
    internal static string? GetContainerId(this DeviceInformation device) =>
        device.Properties.TryGetValue(ContainerIdProperty, out var value) && value is Guid guid
            ? guid.ToString()
            : null;

    /// <summary>Extracts the device MAC address, or <c>null</c>.</summary>
    internal static string? GetDeviceAddress(this DeviceInformation device) =>
        device.Properties.TryGetValue(DeviceAddressProperty, out var value) && value is string mac
            ? mac
            : null;

    /// <summary>Extracts the raw Class of Device value, or <c>null</c>.</summary>
    internal static uint? GetClassOfDevice(this DeviceInformation device) =>
        device.Properties.TryGetValue(ClassOfDeviceProperty, out var value) && value is uint cod
            ? cod
            : null;
}
