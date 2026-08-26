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

    // ── Test seam: injected read override (no WinRT) ────────────────────────────────────────
    // These re-home the coverage the legacy reader tests provided (result plumbing,
    // concurrency limiting, cancellation) onto the production GattConnectionManager.

    [Fact]
    public async Task TestOverride_result_is_returned_verbatim()
    {
        var mgr = new GattConnectionManager(
            (id, name, ct) => Task.FromResult<DeviceBatteryInfo?>(
                new DeviceBatteryInfo(id, name, 42, IsCharging: null, Source: BatterySource.Gatt)),
            maxConcurrency: 1);

        var result = await mgr.TryReadBatteryAsync(
            "dev-1", "Dev", TestContext.Current.CancellationToken).ConfigureAwait(false);

        Assert.NotNull(result);
        Assert.Equal("dev-1", result!.DeviceId);
        Assert.Equal(42, result.Battery);
        Assert.Equal(BatterySource.Gatt, result.Source);
    }

    [Fact]
    public async Task TestOverride_null_result_is_returned_as_null()
    {
        var mgr = new GattConnectionManager(
            (id, name, ct) => Task.FromResult<DeviceBatteryInfo?>(null),
            maxConcurrency: 1);

        var result = await mgr.TryReadBatteryAsync(
            "dev-1", "Dev", TestContext.Current.CancellationToken).ConfigureAwait(false);

        Assert.Null(result);
    }

    [Fact]
    public async Task Concurrency_is_limited_to_maxConcurrency()
    {
        int running = 0;
        int maxObserved = 0;

        async Task<DeviceBatteryInfo?> SlowRead(string id, string name, CancellationToken ct)
        {
            int cur = Interlocked.Increment(ref running);
            try
            {
                if (cur > maxObserved) maxObserved = cur;
                await Task.Delay(100, ct).ConfigureAwait(false);
                return new DeviceBatteryInfo(id, name, 5);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        var mgr = new GattConnectionManager(SlowRead, maxConcurrency: 2);

        Task<DeviceBatteryInfo?>[] tasks = Enumerable.Range(0, 6)
            .Select(i => mgr.TryReadBatteryAsync($"d{i}", $"D{i}", TestContext.Current.CancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        Assert.All(results, r => Assert.NotNull(r));
        Assert.InRange(maxObserved, 1, 2);
    }

    [Fact]
    public async Task Cancelled_token_prevents_read_and_throws()
    {
        var mgr = new GattConnectionManager(
            (id, name, ct) => Task.FromResult<DeviceBatteryInfo?>(
                new DeviceBatteryInfo(id, name, 7)),
            maxConcurrency: 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => mgr.TryReadBatteryAsync("dev-1", "Dev", cts.Token));
    }

    [Fact]
    public void Knowledge_cache_is_empty_initially_and_invalidate_all_is_safe()
    {
        using var mgr = new GattConnectionManager(1);

        Assert.False(mgr.IsKnownGattDevice("dev-1"));
        mgr.InvalidateAll(); // must not throw
        Assert.False(mgr.IsKnownGattDevice("dev-1"));
    }
}

