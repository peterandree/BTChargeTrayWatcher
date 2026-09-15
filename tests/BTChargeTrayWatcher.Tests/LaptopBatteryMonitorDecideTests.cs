using Xunit;
using static BTChargeTrayWatcher.LaptopBatteryMonitor;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Covers the pure threshold decision extracted from <see cref="LaptopBatteryMonitor"/> (#156).
/// The monitor itself owns a <see cref="System.Threading.Timer"/> and system-event
/// subscriptions, so the alert rules are tested through <c>Decide</c> instead.
/// </summary>
public sealed class LaptopBatteryMonitorDecideTests
{
    private const int Low  = 20;
    private const int High = 80;

    private static LaptopBatteryInfo Battery(int pct, bool charging, bool onAc) =>
        new(HasBattery: true, BatteryPercent: pct, IsCharging: charging, IsOnAcPower: onAc);

    // ── Exclude from monitoring (#156) ──────────────────────────────────────

    [Fact]
    public void Excluded_laptop_below_low_threshold_never_alerts()
    {
        var decision = Decide(10, Low, High, AlertState.Normal, Battery(10, false, false),
            excludeFromMonitoring: true);

        Assert.Equal(AlertState.Normal, decision.State);
        Assert.False(decision.NotifyLow);
        Assert.False(decision.NotifyHigh);
    }

    [Fact]
    public void Excluded_laptop_charging_above_high_threshold_never_alerts()
    {
        var decision = Decide(95, Low, High, AlertState.Normal, Battery(95, true, true),
            excludeFromMonitoring: true);

        Assert.Equal(AlertState.Normal, decision.State);
        Assert.False(decision.NotifyLow);
        Assert.False(decision.NotifyHigh);
    }

    [Fact]
    public void Excluded_laptop_clears_a_previous_alert_state()
    {
        var decision = Decide(10, Low, High, AlertState.Low, Battery(10, false, false),
            excludeFromMonitoring: true);

        Assert.Equal(AlertState.Normal, decision.State);
        Assert.False(decision.NotifyLow);
    }

    [Fact]
    public void Not_excluded_below_low_threshold_on_battery_notifies_low()
    {
        var decision = Decide(10, Low, High, AlertState.Normal, Battery(10, false, false),
            excludeFromMonitoring: false);

        Assert.Equal(AlertState.Low, decision.State);
        Assert.True(decision.NotifyLow);
        Assert.False(decision.NotifyHigh);
    }

    // ── Alert rules ─────────────────────────────────────────────────────────

    [Fact]
    public void Charging_above_high_threshold_notifies_high()
    {
        var decision = Decide(90, Low, High, AlertState.Normal, Battery(90, true, true),
            excludeFromMonitoring: false);

        Assert.Equal(AlertState.High, decision.State);
        Assert.True(decision.NotifyHigh);
        Assert.False(decision.NotifyLow);
    }

    [Fact]
    public void Full_battery_not_charging_does_not_notify_high()
    {
        // The high-threshold alert is charge-direction aware: a machine sitting on AC with
        // a full battery that is not charging is not a "charged up" event.
        var decision = Decide(100, Low, High, AlertState.Normal, Battery(100, false, true),
            excludeFromMonitoring: false);

        Assert.Equal(AlertState.Normal, decision.State);
        Assert.False(decision.NotifyHigh);
    }

    [Fact]
    public void Low_state_inside_hysteresis_band_is_retained_without_re_alerting()
    {
        var decision = Decide(Low + 1, Low, High, AlertState.Low, Battery(Low + 1, false, false),
            excludeFromMonitoring: false);

        Assert.Equal(AlertState.Low, decision.State);
        Assert.False(decision.NotifyLow);
    }

    [Fact]
    public void Low_state_leaving_hysteresis_band_returns_to_normal()
    {
        var decision = Decide(Low + PollingDefaults.Hysteresis + 1, Low, High, AlertState.Low,
            Battery(Low + PollingDefaults.Hysteresis + 1, false, false), excludeFromMonitoring: false);

        Assert.Equal(AlertState.Normal, decision.State);
        Assert.False(decision.NotifyLow);
    }

    [Fact]
    public void Charging_alert_is_not_repeated_while_state_is_unchanged()
    {
        var decision = Decide(85, Low, High, AlertState.High, Battery(85, true, true),
            excludeFromMonitoring: false);

        Assert.Equal(AlertState.High, decision.State);
        Assert.False(decision.NotifyHigh);
    }
}
