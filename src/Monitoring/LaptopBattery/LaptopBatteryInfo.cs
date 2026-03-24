namespace BTChargeTrayWatcher;

public sealed record LaptopBatteryInfo(
    bool HasBattery,
    int BatteryPercent,
    bool IsCharging,
    bool IsOnAcPower);
