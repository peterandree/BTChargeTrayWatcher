namespace BTChargeTrayWatcher;

public sealed record DeviceBatteryInfo(
    string DeviceId,
    string Name,
    int? Battery,
    bool? IsCharging = null,
    BatterySource Source = BatterySource.Unknown);
