namespace BTChargeTrayWatcher;

public interface IBatteryReader
{
    Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken);
}
