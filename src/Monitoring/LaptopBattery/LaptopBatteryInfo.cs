namespace BTChargeTrayWatcher;

public sealed record LaptopBatteryInfo(
    bool HasBattery,
    int BatteryPercent,
    bool IsCharging,
    bool IsOnAcPower,
    float? DischargeRateWatts = null,
    float? EstimatedRunTimeMinutes = null,
    int? DesignCapacityMWh = null,
    int? FullChargeCapacityMWh = null)
{
    /// <summary>Battery health as a percentage (0–100), or null when WMI data is unavailable.</summary>
    public float? HealthPercent =>
        DesignCapacityMWh > 0 && FullChargeCapacityMWh.HasValue
            ? Math.Clamp((float)FullChargeCapacityMWh.Value / DesignCapacityMWh.Value * 100f, 0f, 100f)
            : null;
}
