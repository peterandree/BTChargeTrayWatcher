using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

public interface IBatteryReader
{
    Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken);
}
