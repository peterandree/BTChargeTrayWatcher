using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

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
            float rawPercent = status.BatteryLifePercent;
            if (rawPercent >= 0)
            {
                batteryPercent = Math.Clamp((int)Math.Round(rawPercent * 100, MidpointRounding.AwayFromZero), 0, 100);
            }
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
}
