using System;
using System.Threading;
using System.Threading.Tasks;
using BTChargeTrayWatcher.Core;
using Xunit;


// NOTE: All tests in this file are integration/manual only until .NET SDK supports TargetPlatformVersion 10.0.22621.0.
// See WINRT-TEST-LIMITATION.md for details.
namespace BTChargeTrayWatcher.CoreTests;

public class GattConnectionManagerTests
{
    [Fact]
    public async Task ReadBatteryLevelAsync_ReturnsNull_OnInvalidDeviceId()
    {
        using var mgr = new GattConnectionManager();
        var result = await mgr.ReadBatteryLevelAsync("invalid-device-id", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReadBatteryLevelAsync_CancelsGracefully()
    {
        using var mgr = new GattConnectionManager();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await mgr.ReadBatteryLevelAsync("any-device-id", cts.Token);
        Assert.Null(result);
    }

    // Note: Hardware integration tests would go here, but are skipped in CI.
    // [Fact]
    // public async Task ReadBatteryLevelAsync_ReturnsBatteryLevel_OnRealDevice() { ... }
}
