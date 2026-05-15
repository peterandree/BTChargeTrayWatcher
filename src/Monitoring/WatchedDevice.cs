namespace BTChargeTrayWatcher;

/// <summary>
/// Lightweight DTO representing a paired Bluetooth device tracked by the
/// <see cref="DeviceWatcherService"/>. Decouples domain logic from WinRT
/// <c>DeviceInformation</c> for testability.
/// </summary>
internal sealed record WatchedDevice(string DeviceId, string Name, bool IsBle);
