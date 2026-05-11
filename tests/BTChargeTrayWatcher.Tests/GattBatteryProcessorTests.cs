using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using BTChargeTrayWatcher;
using BTChargeTrayWatcher.Monitoring;

namespace BTChargeTrayWatcher.Tests;

public sealed class GattBatteryProcessorTests
{
    [Fact]
    public async Task TestOverride_returns_value()
    {
        var cache = new GattConnectionCache();
        var processor = new GattBatteryProcessor(cache, (deviceId, name, ct) =>
        {
            return Task.FromResult(new GattDeviceReadResult(deviceId, name, 55, true));
        });

        var res = await processor.ProcessDeviceAsync("dev-1", "Dev", CancellationToken.None);
        Assert.Equal(55, res.Battery);
        Assert.True(res.IsCharging);
    }

    [Fact]
    public async Task TestOverride_exception_is_swallowed_and_returns_null()
    {
        var cache = new GattConnectionCache();
        var processor = new GattBatteryProcessor(cache, (deviceId, name, ct) =>
        {
            throw new InvalidOperationException("simulated failure");
        });

        var res = await processor.ProcessDeviceAsync("dev-2", "Dev", CancellationToken.None);
        Assert.Null(res.Battery);
    }
}
