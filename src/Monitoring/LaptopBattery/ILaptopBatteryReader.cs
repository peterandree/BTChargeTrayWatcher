namespace BTChargeTrayWatcher;

public interface ILaptopBatteryReader
{
    Task<LaptopBatteryInfo> ReadAsync(CancellationToken cancellationToken);
}
