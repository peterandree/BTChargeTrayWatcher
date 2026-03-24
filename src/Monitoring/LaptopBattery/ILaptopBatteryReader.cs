using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

public interface ILaptopBatteryReader
{
    Task<LaptopBatteryInfo> ReadAsync(CancellationToken cancellationToken);
}
