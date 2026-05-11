using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher.Monitoring;

public sealed class BatteryReaderOrchestrator
{
    public BatteryReaderOrchestrator()
    {
    }

    // Read battery info for all known devices. Implementation to be completed
    public Task<IEnumerable<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        IEnumerable<DeviceBatteryInfo> empty = System.Array.Empty<DeviceBatteryInfo>();
        return Task.FromResult(empty);
    }
}
