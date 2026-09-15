using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Behaviour of the bounded subscription set (issues #160 and #161) against the fake seam.
/// Covers cap enforcement, the settling-window drop, every teardown trigger, and the guarantee
/// that a notification arriving after teardown is ignored.
/// </summary>
public sealed class GattSubscriptionCoordinatorTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static (GattSubscriptionCoordinator Coordinator, FakeGattNotificationSubscription Fake, FakeClock Clock)
        Create(int cap = 2)
    {
        var fake = new FakeGattNotificationSubscription();
        var clock = new FakeClock(new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));
        var coordinator = new GattSubscriptionCoordinator(
            fake, maxConcurrentSubscriptions: cap, settlingWindow: Window, clock: clock.Now);
        return (coordinator, fake, clock);
    }

    private sealed class FakeClock(DateTime start)
    {
        private DateTime _now = start;

        public DateTime Now() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    // ── Subscribe / policy ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Eligible_device_is_subscribed()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;

        bool subscribed = await coordinator.TrySubscribeAsync(
            "dev-1", "Headset", supportsNotify: true, isConnected: true,
            TestContext.Current.CancellationToken);

        Assert.True(subscribed);
        Assert.True(coordinator.IsSubscribed("dev-1"));
        Assert.Equal(1, coordinator.ActiveCount);
        Assert.Equal(new[] { "dev-1" }, fake.SubscribeAttempts);
    }

    [Fact]
    public async Task Device_without_Notify_never_reaches_the_seam()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;

        bool subscribed = await coordinator.TrySubscribeAsync(
            "dev-1", "Headset", supportsNotify: false, isConnected: true,
            TestContext.Current.CancellationToken);

        Assert.False(subscribed);
        Assert.Equal(0, coordinator.ActiveCount);
        Assert.Empty(fake.SubscribeAttempts);
    }

    [Fact]
    public async Task Disconnected_device_never_reaches_the_seam()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;

        bool subscribed = await coordinator.TrySubscribeAsync(
            "dev-1", "Sleeping headset", supportsNotify: true, isConnected: false,
            TestContext.Current.CancellationToken);

        Assert.False(subscribed);
        Assert.Empty(fake.SubscribeAttempts);
    }

    [Fact]
    public async Task Cap_refuses_the_third_device_without_touching_the_seam()
    {
        var (coordinator, fake, _) = Create(cap: 2);
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct));
        Assert.True(await coordinator.TrySubscribeAsync("dev-2", "B", true, true, ct));
        Assert.False(await coordinator.TrySubscribeAsync("dev-3", "C", true, true, ct));

        Assert.Equal(2, coordinator.ActiveCount);
        Assert.Equal(new[] { "dev-1", "dev-2" }, fake.SubscribeAttempts);
        Assert.False(coordinator.IsSubscribed("dev-3"));
    }

    [Fact]
    public async Task Concurrent_attempts_cannot_exceed_the_cap()
    {
        // Devices are read in parallel, so a check-then-await implementation would let three
        // overlapping attempts all pass a cap of two.
        var (coordinator, fake, _) = Create(cap: 2);
        using var _c = coordinator;
        fake.SubscribeDelay = TimeSpan.FromMilliseconds(40);
        var ct = TestContext.Current.CancellationToken;

        bool[] results = await Task.WhenAll(
            coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct),
            coordinator.TrySubscribeAsync("dev-2", "B", true, true, ct),
            coordinator.TrySubscribeAsync("dev-3", "C", true, true, ct));

        Assert.Equal(2, results.Count(r => r));
        Assert.Equal(2, coordinator.ActiveCount);
        Assert.Equal(2, fake.SubscribeAttempts.Count);
    }

    [Fact]
    public async Task Cap_of_zero_disables_subscriptions()
    {
        var (coordinator, fake, _) = Create(cap: 0);
        using var _c = coordinator;

        bool subscribed = await coordinator.TrySubscribeAsync(
            "dev-1", "Headset", supportsNotify: true, isConnected: true,
            TestContext.Current.CancellationToken);

        Assert.False(subscribed);
        Assert.Empty(fake.SubscribeAttempts);
        Assert.Equal(0, coordinator.ActiveCount);
    }

    [Fact]
    public async Task Seam_refusal_is_reported_as_not_subscribed()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;
        fake.SubscribeResult = false;

        bool subscribed = await coordinator.TrySubscribeAsync(
            "dev-1", "Headset", supportsNotify: true, isConnected: true,
            TestContext.Current.CancellationToken);

        Assert.False(subscribed);
        Assert.Equal(0, coordinator.ActiveCount);
        Assert.Equal(new[] { "dev-1" }, fake.SubscribeAttempts);
    }

    [Fact]
    public async Task Repeated_subscribe_for_an_active_device_is_a_no_op()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct));
        Assert.True(await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct));

        Assert.Equal(1, coordinator.ActiveCount);
        Assert.Equal(new[] { "dev-1" }, fake.SubscribeAttempts);
    }

    // ── Notifications ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Notification_is_remembered_for_the_watchdog_read()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;

        await coordinator.TrySubscribeAsync("dev-1", "A", true, true, TestContext.Current.CancellationToken);
        fake.RaiseNotification("dev-1", 63);

        Assert.Equal(63, coordinator.TryGetLastNotificationBattery("dev-1"));
    }

    [Fact]
    public async Task Out_of_range_notification_is_ignored()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;

        await coordinator.TrySubscribeAsync("dev-1", "A", true, true, TestContext.Current.CancellationToken);
        fake.RaiseNotification("dev-1", 250);

        Assert.Null(coordinator.TryGetLastNotificationBattery("dev-1"));
    }

    [Fact]
    public async Task Notification_after_teardown_is_ignored()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct);
        await coordinator.DropAsync("dev-1", GattSubscriptionDropReason.Suspend, ct);

        // A push that was already in flight must not resurrect the device.
        fake.RaiseNotification("dev-1", 42);

        Assert.Null(coordinator.TryGetLastNotificationBattery("dev-1"));
        Assert.Equal(0, coordinator.ActiveCount);
    }

    // ── Settling window ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Silent_subscription_is_dropped_at_the_settling_window_and_not_retried()
    {
        var (coordinator, fake, clock) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct));

        clock.Advance(Window - TimeSpan.FromSeconds(1));
        await coordinator.PruneAsync(ct);
        Assert.True(coordinator.IsSubscribed("dev-1"));

        clock.Advance(TimeSpan.FromSeconds(1));
        await coordinator.PruneAsync(ct);

        Assert.False(coordinator.IsSubscribed("dev-1"));
        Assert.True(coordinator.IsDroppedForSession("dev-1"));
        Assert.Equal(0, coordinator.ActiveCount);
        Assert.Equal(new[] { "dev-1" }, fake.Unsubscribed);

        // Never re-subscribed in this session, and the seam is not contacted again.
        Assert.False(await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct));
        Assert.Single(fake.SubscribeAttempts);
    }

    [Fact]
    public async Task A_single_notification_keeps_the_subscription_alive()
    {
        var (coordinator, fake, clock) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct);
        fake.RaiseNotification("dev-1", 80);

        clock.Advance(Window * 3);
        await coordinator.PruneAsync(ct);

        Assert.True(coordinator.IsSubscribed("dev-1"));
        Assert.False(coordinator.IsDroppedForSession("dev-1"));
        Assert.Empty(fake.Unsubscribed);
    }

    // ── Teardown paths (#161) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disconnect_releases_bookkeeping_without_a_second_unsubscribe()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct);

        // The seam detects the disconnect itself and has already released its WinRT references.
        fake.RaiseLost("dev-1", GattSubscriptionDropReason.Disconnected);

        Assert.False(coordinator.IsSubscribed("dev-1"));
        Assert.Equal(0, coordinator.ActiveCount);
        Assert.Empty(fake.Unsubscribed);
        Assert.False(coordinator.IsDroppedForSession("dev-1"));
    }

    [Fact]
    public async Task Disconnected_device_may_resubscribe_later()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct);
        fake.RaiseLost("dev-1", GattSubscriptionDropReason.Disconnected);

        Assert.True(await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct));
        Assert.Equal(2, fake.SubscribeAttempts.Count);
    }

    [Fact]
    public async Task Suspend_releases_every_subscription_exactly_once()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct);
        await coordinator.TrySubscribeAsync("dev-2", "B", true, true, ct);

        await coordinator.DropAllAsync(GattSubscriptionDropReason.Suspend, ct);

        Assert.Equal(0, coordinator.ActiveCount);
        Assert.Equal(2, fake.Unsubscribed.Count);
        Assert.Contains("dev-1", fake.Unsubscribed);
        Assert.Contains("dev-2", fake.Unsubscribed);
        Assert.False(coordinator.IsDroppedForSession("dev-1"));

        // A device dropped for suspend may be re-evaluated after resume.
        Assert.True(await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct));
        Assert.Equal(3, fake.SubscribeAttempts.Count);
    }

    [Fact]
    public async Task Eviction_releases_only_that_device()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct);
        await coordinator.TrySubscribeAsync("dev-2", "B", true, true, ct);

        await coordinator.DropAsync("dev-1", GattSubscriptionDropReason.Evicted, ct);

        Assert.False(coordinator.IsSubscribed("dev-1"));
        Assert.True(coordinator.IsSubscribed("dev-2"));
        Assert.Equal(new[] { "dev-1" }, fake.Unsubscribed);
        Assert.False(coordinator.IsDroppedForSession("dev-1"));
    }

    [Fact]
    public async Task Drop_is_idempotent()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct);
        await coordinator.DropAsync("dev-1", GattSubscriptionDropReason.Evicted, ct);
        await coordinator.DropAsync("dev-1", GattSubscriptionDropReason.Evicted, ct);

        Assert.Equal(new[] { "dev-1" }, fake.Unsubscribed);
    }

    [Fact]
    public async Task Drop_all_on_an_empty_set_completes()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;

        await coordinator.DropAllAsync(GattSubscriptionDropReason.Suspend, TestContext.Current.CancellationToken);

        Assert.Empty(fake.Unsubscribed);
    }

    [Fact]
    public async Task Dispose_releases_coordinator_state_and_detaches_from_the_seam()
    {
        var (coordinator, fake, _) = Create();
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "A", true, true, ct);
        coordinator.Dispose();

        Assert.Equal(0, coordinator.ActiveCount);

        // Handlers are detached: a late push must not reach already-disposed state.
        fake.RaiseNotification("dev-1", 55);
        Assert.Null(coordinator.TryGetLastNotificationBattery("dev-1"));

        // Disposed coordinators refuse new subscriptions.
        Assert.False(await coordinator.TrySubscribeAsync("dev-2", "B", true, true, ct));
    }

    [Fact]
    public async Task A_faulted_seam_does_not_escape_as_an_exception()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;
        fake.ThrowOnSubscribe = true;

        bool subscribed = await coordinator.TrySubscribeAsync(
            "dev-1", "A", supportsNotify: true, isConnected: true,
            TestContext.Current.CancellationToken);

        Assert.False(subscribed);
        Assert.Equal(0, coordinator.ActiveCount);
    }
}
