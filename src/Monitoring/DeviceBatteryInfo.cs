namespace BTChargeTrayWatcher;

internal sealed record DeviceBatteryInfo(
    string Name,
    int Battery); // -1 for “no value”