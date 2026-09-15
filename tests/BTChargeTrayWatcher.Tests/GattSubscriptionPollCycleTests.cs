using BTChargeTrayWatcher.Monitoring.Logging;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Wiring tests for the teardown entry points of <see cref="GattConnectionManager"/> (#161) and for
/// the settling-window prune that runs once per poll cycle (#160), using the fake seam.
/// </summary>
public sealed class GattSubscriptionPollCycleTests
{
    private static GattConnectionManager Manager(
        FakeGattNotificationSubscription fake,
        TimeSpan? settlingWindow = null,
        Func<DateTime>? clock = null) =>
        new(1, new GattSubscriptionCoordinator(fake, settlingWindow: settlingWindow, clock: clock));

    // ── Manager teardown entry points (#161) ──────────────────────────────────────────────

    [Fact]
    public async Task Suspend_releases_every_subscription()
    {
        var fake = new FakeGattNotificationSubscription();
        await using var manager = Manager(fake);
        var ct = TestContext.Current.CancellationToken;

        await manager.Subscriptions.TrySubscribeAsync("dev-1", "A", true, true, ct);
        await manager.Subscriptions.TrySubscribeAsync("dev-2", "B", true, true, ct);

        await manager.SuspendSubscriptionsAsync(ct);

        Assert.Equal(0, manager.Subscriptions.ActiveCount);
        Assert.Equal(2, fake.Unsubscribed.Count);
        Assert.Contains("dev-1", fake.Unsubscribed);
        Assert.Contains("dev-2", fake.Unsubscribed);
    }

    [Fact]
    public async Task Device_eviction_releases_only_the_evicted_device()
    {
        var fake = new FakeGattNotificationSubscription();
        await using var manager = Manager(fake);
        var ct = TestContext.Current.CancellationToken;

        await manager.Subscriptions.TrySubscribeAsync("dev-1", "A", true, true, ct);
        await manager.Subscriptions.TrySubscribeAsync("dev-2", "B", true, true, ct);

        await manager.DeviceEvictedAsync("dev-1", ct);

        Assert.False(manager.Subscriptions.IsSubscribed("dev-1"));
        Assert.True(manager.Subscriptions.IsSubscribed("dev-2"));
        Assert.Equal(new[] { "dev-1" }, fake.Unsubscribed);
    }

    [Fact]
    public async Task DisposeAsync_releases_subscriptions_and_logs_the_disposed_reason()
    {
        var fake = new FakeGattNotificationSubscription();
        var manager = Manager(fake);
        var ct = TestContext.Current.CancellationToken;

        await manager.Subscriptions.TrySubscribeAsync("dev-1", "A", true, true, ct);

        List<DiscoveryLogEntry> entries = [];
        using (DiscoveryLogger.Capture(entries.Add))
        {
            await manager.DisposeAsync();
        }

        Assert.Equal(new[] { "dev-1" }, fake.Unsubscribed);

        var drop = Assert.Single(
            entries, e => e.ErrorCode == DiscoveryLogger.Codes.GattSubscriptionDropped);
        Assert.Equal("dev-1", drop.DeviceId);
        Assert.Contains("reason=Disposed", drop.Message);
    }

    [Fact]
    public async Task DisposeAsync_without_subscriptions_is_safe()
    {
        var fake = new FakeGattNotificationSubscription();
        var manager = Manager(fake);

        await manager.DisposeAsync();
        await manager.DisposeAsync(); // idempotent

        Assert.Empty(fake.Unsubscribed);
    }

    // ── Poll-cycle prune (#160) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Poll_cycle_drops_a_subscription_that_never_notified()
    {
        var fake = new FakeGattNotificationSubscription();
        DateTime now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var manager = Manager(fake, settlingWindow: TimeSpan.FromMinutes(10), clock: () => now);
        var ct = TestContext.Current.CancellationToken;

        await manager.Subscriptions.TrySubscribeAsync("dev-1", "A", true, true, ct);

        var orchestrator = new BatteryReaderOrchestrator(
            manager,
            readClassic: (_, _) => Task.FromResult(new List<DeviceBatteryInfo>()),
            new DeviceCapabilityCache());

        // Inside the settling window: the subscription survives the poll.
        await orchestrator.ReadAllAsync([], skipConnectionCheck: true, ct);
        Assert.True(manager.Subscriptions.IsSubscribed("dev-1"));

        // Past the settling window: the next poll cycle drops it and never retries.
        now += TimeSpan.FromMinutes(10);
        await orchestrator.ReadAllAsync([], skipConnectionCheck: true, ct);

        Assert.False(manager.Subscriptions.IsSubscribed("dev-1"));
        Assert.True(manager.Subscriptions.IsDroppedForSession("dev-1"));
        Assert.Equal(new[] { "dev-1" }, fake.Unsubscribed);

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Poll_cycle_leaves_a_notifying_subscription_alone()
    {
        var fake = new FakeGattNotificationSubscription();
        DateTime now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var manager = Manager(fake, settlingWindow: TimeSpan.FromMinutes(10), clock: () => now);
        var ct = TestContext.Current.CancellationToken;

        await manager.Subscriptions.TrySubscribeAsync("dev-1", "A", true, true, ct);
        fake.RaiseNotification("dev-1", 55);

        var orchestrator = new BatteryReaderOrchestrator(
            manager,
            readClassic: (_, _) => Task.FromResult(new List<DeviceBatteryInfo>()),
            new DeviceCapabilityCache());

        now += TimeSpan.FromMinutes(30);
        await orchestrator.ReadAllAsync([], skipConnectionCheck: true, ct);

        Assert.True(manager.Subscriptions.IsSubscribed("dev-1"));
        Assert.Empty(fake.Unsubscribed);

        await manager.DisposeAsync();
    }
}
