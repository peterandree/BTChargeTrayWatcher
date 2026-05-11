using System;
using System.Threading.Tasks;
using Xunit;
using BTChargeTrayWatcher.Monitoring.Gatt;

namespace BTChargeTrayWatcher.Tests;

public sealed class GattConnectionManagerTests
{
    [Fact]
    public async Task TryReadBatteryAsync_throws_on_empty_deviceid()
    {
        var mgr = new GattConnectionManager();

        await Assert.ThrowsAsync<ArgumentException>(() => mgr.TryReadBatteryAsync(string.Empty, 100));
        await Assert.ThrowsAsync<ArgumentException>(() => mgr.TryReadBatteryAsync("   ", 100));
    }
}
