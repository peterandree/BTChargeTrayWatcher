using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class GattConnectionManagerTests
{
    [Fact]
    public void Constructor_default_succeeds()
    {
        using var mgr = new GattConnectionManager();
        Assert.NotNull(mgr);
    }

    [Fact]
    public void Constructor_custom_concurrency_succeeds()
    {
        using var mgr = new GattConnectionManager(2);
        Assert.NotNull(mgr);
    }
}

