namespace BTChargeTrayWatcher;

/// <summary>
/// Classifies a Bluetooth device's transport and category from primitive inputs.
/// The thin WinRT layer (<see cref="BluetoothDeviceExtensions"/>) extracts the
/// properties; this class contains the pure logic that is unit-testable.
/// </summary>
internal sealed class DeviceProfileClassifier
{
    /// <summary>
    /// Classifies a device based on transport flags and the Bluetooth Class of Device (CoD).
    /// </summary>
    /// <param name="isBle">Device was enumerated via a BLE selector.</param>
    /// <param name="isClassic">Device was enumerated via a Classic BT selector.</param>
    /// <param name="classOfDevice">
    /// The raw 24-bit Bluetooth Class of Device value, or <c>null</c> if unavailable.
    /// Major Device Class is extracted from bits 12–8.
    /// </param>
    internal (DeviceTransport Transport, DeviceCategory Category) Classify(
        bool isBle, bool isClassic, uint? classOfDevice)
    {
        var transport = (isBle, isClassic) switch
        {
            (true, true) => DeviceTransport.DualMode,
            (true, false) => DeviceTransport.Ble,
            (false, true) => DeviceTransport.Classic,
            _ => DeviceTransport.Unknown
        };

        var category = DeviceCategory.Unknown;
        if (classOfDevice is not null)
        {
            // Major Device Class: bits 12–8 of the 24-bit CoD (Bluetooth Assigned Numbers §2.8.2)
            uint majorClass = (classOfDevice.Value >> 8) & 0x1F;
            category = majorClass switch
            {
                0x04 => DeviceCategory.Audio,       // Audio/Video
                0x05 => DeviceCategory.Hid,         // Peripheral (keyboard, mouse, gamepad)
                _ => DeviceCategory.Unknown
            };
        }

        return (transport, category);
    }
}
