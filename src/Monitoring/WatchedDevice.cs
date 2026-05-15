namespace BTChargeTrayWatcher;

/// <summary>
/// Lightweight DTO representing a paired Bluetooth device tracked by the
/// <see cref="DeviceWatcherService"/>. Decouples domain logic from WinRT
/// <c>DeviceInformation</c> for testability.
/// </summary>
/// <param name="IsConnected">
/// <c>true</c> if Windows reports the device is actively connected
/// (<c>System.Devices.Aep.IsConnected</c>). BLE devices that are sleeping
/// will have <c>false</c>. Classic devices default to <c>true</c> since
/// the Classic watcher only reports connected/paired devices.
/// </param>
internal sealed record WatchedDevice(string DeviceId, string Name, bool IsBle, bool IsConnected = true);
