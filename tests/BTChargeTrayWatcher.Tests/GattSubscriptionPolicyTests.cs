using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Pure policy tests for the bounded GATT Battery Level subscription (issue #160).
/// No WinRT, no hardware, no clock.
/// </summary>
public sealed class GattSubscriptionPolicyTests
{
    [Fact]
    public void Eligible_device_under_the_cap_may_subscribe()
    {
        Assert.True(GattSubscriptionPolicy.ShouldSubscribe(
            supportsNotify: true,
            isConnected: true,
            activeSubscriptions: 0,
            maxConcurrentSubscriptions: 2));
    }

    [Fact]
    public void Device_without_Notify_is_never_subscribed()
    {
        Assert.False(GattSubscriptionPolicy.ShouldSubscribe(
            supportsNotify: false,
            isConnected: true,
            activeSubscriptions: 0,
            maxConcurrentSubscriptions: 2));
    }

    [Fact]
    public void Disconnected_device_is_never_subscribed()
    {
        // A subscription must never force a connection (ADR-017).
        Assert.False(GattSubscriptionPolicy.ShouldSubscribe(
            supportsNotify: true,
            isConnected: false,
            activeSubscriptions: 0,
            maxConcurrentSubscriptions: 2));
    }

    [Fact]
    public void Third_device_is_refused_at_the_cap_of_two()
    {
        Assert.False(GattSubscriptionPolicy.ShouldSubscribe(
            supportsNotify: true,
            isConnected: true,
            activeSubscriptions: 2,
            maxConcurrentSubscriptions: 2));
    }

    [Fact]
    public void Cap_of_zero_disables_subscriptions_entirely()
    {
        // The baseline configuration for the #158 hardware measurement: the read path must behave
        // exactly as it did before the feature existed.
        Assert.False(GattSubscriptionPolicy.ShouldSubscribe(
            supportsNotify: true,
            isConnected: true,
            activeSubscriptions: 0,
            maxConcurrentSubscriptions: 0));
    }

    [Fact]
    public void Negative_cap_is_treated_as_disabled()
    {
        Assert.False(GattSubscriptionPolicy.ShouldSubscribe(
            supportsNotify: true,
            isConnected: true,
            activeSubscriptions: 0,
            maxConcurrentSubscriptions: -1));
    }

    // ── Settling window ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Silent_subscription_past_the_settling_window_has_failed()
    {
        var subscribedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(GattSubscriptionPolicy.HasFailedSettling(
            hasNotified: false,
            subscribedAtUtc: subscribedAt,
            nowUtc: subscribedAt + TimeSpan.FromMinutes(10),
            settlingWindow: TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Silent_subscription_inside_the_settling_window_has_not_failed()
    {
        var subscribedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(GattSubscriptionPolicy.HasFailedSettling(
            hasNotified: false,
            subscribedAtUtc: subscribedAt,
            nowUtc: subscribedAt + TimeSpan.FromMinutes(9),
            settlingWindow: TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Notifying_subscription_never_fails_the_settling_rule()
    {
        var subscribedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(GattSubscriptionPolicy.HasFailedSettling(
            hasNotified: true,
            subscribedAtUtc: subscribedAt,
            nowUtc: subscribedAt + TimeSpan.FromHours(5),
            settlingWindow: TimeSpan.FromMinutes(10)));
    }
}
