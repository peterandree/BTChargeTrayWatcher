using BTChargeTrayWatcher.Monitoring.Logging;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Asserts the ADR-018 logging contract of the bounded subscription (#162): every subscribe,
/// notification and drop decision is logged with the documented code, so the #158 hardware
/// measurement can be reconstructed from a log file alone.
/// </summary>
public sealed class GattSubscriptionLoggingTests
{
    private const string ReaderName = "GattSubscriptionCoordinator";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static (GattSubscriptionCoordinator Coordinator, FakeGattNotificationSubscription Fake, Action<TimeSpan> Advance)
        Create()
    {
        var fake = new FakeGattNotificationSubscription();
        DateTime now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var coordinator = new GattSubscriptionCoordinator(
            fake,
            maxConcurrentSubscriptions: 2,
            settlingWindow: Window,
            clock: () => now);

        return (coordinator, fake, delta => now += delta);
    }

    [Fact]
    public async Task Subscribe_is_logged_with_code_1010()
    {
        var (coordinator, _, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        List<DiscoveryLogEntry> entries = [];
        using (DiscoveryLogger.Capture(entries.Add))
        {
            Assert.True(await coordinator.TrySubscribeAsync("dev-1", "Headset", true, true, ct));
        }

        var entry = Assert.Single(entries, e => e.ErrorCode == DiscoveryLogger.Codes.GattSubscribed);
        Assert.Equal(ReaderName, entry.Reader);
        Assert.Equal("Subscribe", entry.Operation);
        Assert.Equal("SUBSCRIBED", entry.Outcome);
        Assert.Equal("dev-1", entry.DeviceId);
        Assert.Equal("Headset", entry.DeviceName);
    }

    [Fact]
    public async Task Notification_is_logged_with_code_1011()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "Headset", true, true, ct);

        List<DiscoveryLogEntry> entries = [];
        using (DiscoveryLogger.Capture(entries.Add))
        {
            fake.RaiseNotification("dev-1", 63);
        }

        var entry = Assert.Single(
            entries, e => e.ErrorCode == DiscoveryLogger.Codes.GattNotificationReceived);
        Assert.Equal("Notify", entry.Operation);
        Assert.Equal("NOTIFIED", entry.Outcome);
        Assert.Equal("dev-1", entry.DeviceId);
        Assert.Contains("battery=63", entry.Message);
    }

    [Fact]
    public async Task Settling_timeout_drop_is_logged_with_code_1012()
    {
        var (coordinator, _, advance) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "Headset", true, true, ct);
        advance(Window);

        List<DiscoveryLogEntry> entries = [];
        using (DiscoveryLogger.Capture(entries.Add))
        {
            await coordinator.PruneAsync(ct);
        }

        AssertDropReason(entries, "dev-1", GattSubscriptionDropReason.SettlingTimeout);
    }

    [Fact]
    public async Task Disconnect_drop_is_logged_with_code_1012()
    {
        var (coordinator, fake, _) = Create();
        using var _c = coordinator;

        await coordinator.TrySubscribeAsync(
            "dev-1", "Headset", true, true, TestContext.Current.CancellationToken);

        List<DiscoveryLogEntry> entries = [];
        using (DiscoveryLogger.Capture(entries.Add))
        {
            fake.RaiseLost("dev-1", GattSubscriptionDropReason.Disconnected);
        }

        AssertDropReason(entries, "dev-1", GattSubscriptionDropReason.Disconnected);
    }

    [Fact]
    public async Task Suspend_drop_is_logged_with_code_1012()
    {
        var (coordinator, _, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "Headset", true, true, ct);

        List<DiscoveryLogEntry> entries = [];
        using (DiscoveryLogger.Capture(entries.Add))
        {
            await coordinator.DropAllAsync(GattSubscriptionDropReason.Suspend, ct);
        }

        AssertDropReason(entries, "dev-1", GattSubscriptionDropReason.Suspend);
    }

    [Fact]
    public async Task Eviction_drop_is_logged_with_code_1012()
    {
        var (coordinator, _, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "Headset", true, true, ct);

        List<DiscoveryLogEntry> entries = [];
        using (DiscoveryLogger.Capture(entries.Add))
        {
            await coordinator.DropAsync("dev-1", GattSubscriptionDropReason.Evicted, ct);
        }

        AssertDropReason(entries, "dev-1", GattSubscriptionDropReason.Evicted);
    }

    [Fact]
    public async Task Dispose_drop_is_logged_with_code_1012()
    {
        var (coordinator, _, _) = Create();
        using var _c = coordinator;
        var ct = TestContext.Current.CancellationToken;

        await coordinator.TrySubscribeAsync("dev-1", "Headset", true, true, ct);

        List<DiscoveryLogEntry> entries = [];
        using (DiscoveryLogger.Capture(entries.Add))
        {
            await coordinator.DropAllAsync(GattSubscriptionDropReason.Disposed, ct);
        }

        AssertDropReason(entries, "dev-1", GattSubscriptionDropReason.Disposed);
    }

    private static void AssertDropReason(
        List<DiscoveryLogEntry> entries, string deviceId, GattSubscriptionDropReason reason)
    {
        var entry = Assert.Single(
            entries, e => e.ErrorCode == DiscoveryLogger.Codes.GattSubscriptionDropped);
        Assert.Equal(ReaderName, entry.Reader);
        Assert.Equal("Drop", entry.Operation);
        Assert.Equal(deviceId, entry.DeviceId);
        Assert.Contains($"reason={reason}", entry.Message);
    }
}
