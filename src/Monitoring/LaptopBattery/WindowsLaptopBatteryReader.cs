namespace BTChargeTrayWatcher;

public sealed class WindowsLaptopBatteryReader : ILaptopBatteryReader
{
    public Task<LaptopBatteryInfo> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PowerStatus status = SystemInformation.PowerStatus;
        BatteryChargeStatus chargeStatus = status.BatteryChargeStatus;

        bool hasBattery = !chargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery);

        int batteryPercent = -1;
        if (hasBattery)
        {
            batteryPercent = ToBatteryPercent(status.BatteryLifePercent);
        }

        bool isCharging = hasBattery && chargeStatus.HasFlag(BatteryChargeStatus.Charging);
        bool isOnAcPower = status.PowerLineStatus == PowerLineStatus.Online;

        var info = new LaptopBatteryInfo(
            HasBattery: hasBattery,
            BatteryPercent: batteryPercent,
            IsCharging: isCharging,
            IsOnAcPower: isOnAcPower);

        return Task.FromResult(info);
    }

    /// <summary>
    /// Converts a raw <see cref="PowerStatus.BatteryLifePercent"/> value (0.0–1.0) into a
    /// whole percentage, or -1 when the level is unknown.
    /// Windows reports the level as a byte 0–100, or <c>255</c> when unknown; the framework
    /// divides by 100 and clamps, so BOTH a genuinely full battery (byte 100) and an unknown
    /// level (byte 255) surface as exactly 1.0. The float alone cannot distinguish them, so
    /// any value of 1.0 or greater is treated as unknown (-1) rather than a fabricated 100 % —
    /// the safe direction for a threshold-alerting app (fixes #144: phantom 100 % reports).
    /// A precise level for the ambiguous full/unknown case requires WMI
    /// (<see cref="Win32_Battery"/>), tracked in #127/#154.
    /// </summary>
    internal static int ToBatteryPercent(float rawPercent) =>
        rawPercent is >= 0f and < 1f
            ? Math.Clamp((int)Math.Round(rawPercent * 100, MidpointRounding.AwayFromZero), 0, 100)
            : -1;
}
