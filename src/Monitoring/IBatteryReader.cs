namespace BTChargeTrayWatcher;

public interface IBatteryReader
{
    Task<List<(string Name, int Battery)>> ReadAllAsync(CancellationToken cancellationToken);
}
