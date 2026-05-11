using System;
using System.Collections.Generic;
using Xunit;
using BTChargeTrayWatcher;

namespace BTChargeTrayWatcher.Tests;

public sealed class GattConnectionCacheTests
{
    private sealed class FakeDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void SetAndGetEndpoint_ReturnsSame()
    {
        var cache = new GattConnectionCache();
        var fake = new FakeDisposable();
        var endpoint = new CachedGattEndpoint(fake);

        cache.SetEndpoint("dev1", endpoint);

        var got = cache.GetEndpoint("dev1");
        Assert.NotNull(got);
        Assert.Same(endpoint, got);
    }

    [Fact]
    public void RemoveEndpoint_DisposesEndpoint()
    {
        var cache = new GattConnectionCache();
        var fake = new FakeDisposable();
        var endpoint = new CachedGattEndpoint(fake);

        cache.SetEndpoint("dev1", endpoint);
        cache.RemoveEndpoint("dev1");

        Assert.True(fake.Disposed);
        Assert.Null(cache.GetEndpoint("dev1"));
    }

    [Fact]
    public void PruneStaleDevices_RemovesEndpointsNotInSet()
    {
        var cache = new GattConnectionCache();
        var fake1 = new FakeDisposable();
        var endpoint1 = new CachedGattEndpoint(fake1);
        cache.SetEndpoint("dev1", endpoint1);
        var fake2 = new FakeDisposable();
        var endpoint2 = new CachedGattEndpoint(fake2);
        cache.SetEndpoint("dev2", endpoint2);

        cache.PruneStaleDevices(new HashSet<string>(new[] { "dev2" }, StringComparer.OrdinalIgnoreCase));

        Assert.True(fake1.Disposed);
        Assert.Null(cache.GetEndpoint("dev1"));
        Assert.NotNull(cache.GetEndpoint("dev2"));
        Assert.False(fake2.Disposed);
    }
}
