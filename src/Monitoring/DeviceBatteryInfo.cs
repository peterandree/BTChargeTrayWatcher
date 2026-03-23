namespace BTChargeTrayWatcher;

public sealed record DeviceBatteryInfo(
    string DeviceId,
    string Name,
    int Battery); // -1 for "no value"
