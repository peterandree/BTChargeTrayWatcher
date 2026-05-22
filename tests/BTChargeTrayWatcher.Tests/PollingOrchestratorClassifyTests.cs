using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static BTChargeTrayWatcher.PollingOrchestrator;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Tests for PollingOrchestrator.ClassifyBatteryState.
/// All cases exercise pure domain logic with no I/O.
/// Hysteresis = 2, Low default = 20, High default = 80 (PollingDefaults / ThresholdSettings defaults).
/// </summary>
public sealed class PollingOrchestratorClassifyTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static PollingOrchestrator BuildOrchestrator(
        ThresholdSettings? settings = null)
    {
        var s = settings ?? new ThresholdSettings();
        var opts = new PollingOrchestratorOptions(
            Settings:      s,
            Notifier:      NullNotificationService.Instance,
            LastKnown:     new ConcurrentDictionary<string, DeviceBatteryInfo>(StringComparer.OrdinalIgnoreCase),
            Tracker:       new TaskTracker(),
            ReadDevices:   _ => Task.FromResult(new List<DeviceBatteryInfo>()),
            Callbacks:     new PollingOrchestratorCallbacks(
                OnBatteryRead:       (_, _) => { },
                OnScanCompleted:     _ => { },
                OnAlertStateChanged: _ => { }),
            ShutdownToken: TestContext.Current.CancellationToken);
        return new PollingOrchestrator(opts);
    }

    // ── Low threshold ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Battery_at_low_threshold_is_Low()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Low,
            o.ClassifyBatteryState("Dev", "Dev", 20, BatteryAlertState.Normal, null));
    }

    [Fact]
    public void Battery_below_low_threshold_is_Low()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Low,
            o.ClassifyBatteryState("Dev", "Dev", 5, BatteryAlertState.Normal, null));
    }

    [Fact]
    public void Battery_one_above_low_threshold_without_prior_Low_is_Normal()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Dev", "Dev", 21, BatteryAlertState.Normal, null));
    }

    // ── High threshold ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Battery_at_high_threshold_not_charging_is_High()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.High,
            o.ClassifyBatteryState("Dev", "Dev", 80, BatteryAlertState.Normal, false));
    }

    [Fact]
    public void Battery_above_high_threshold_not_charging_is_High()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.High,
            o.ClassifyBatteryState("Dev", "Dev", 95, BatteryAlertState.Normal, false));
    }

    [Fact]
    public void Battery_at_high_threshold_unknown_charging_is_High()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.High,
            o.ClassifyBatteryState("Dev", "Dev", 80, BatteryAlertState.Normal, null));
    }

    [Fact]
    public void Battery_at_high_threshold_confirmed_charging_is_Normal()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Dev", "Dev", 80, BatteryAlertState.Normal, true));
    }

    [Fact]
    public void Battery_above_high_confirmed_charging_is_Normal()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Dev", "Dev", 95, BatteryAlertState.Normal, true));
    }

    // ── Normal zone (between thresholds) ───────────────────────────────────────────────

    [Fact]
    public void Battery_in_normal_zone_from_Normal_is_Normal()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Dev", "Dev", 50, BatteryAlertState.Normal, null));
    }

    // ── Hysteresis ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hysteresis_Low_stays_Low_within_band()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Low,
            o.ClassifyBatteryState("Dev", "Dev", 22, BatteryAlertState.Low, null));
    }

    [Fact]
    public void Hysteresis_Low_exits_to_Normal_above_band()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Dev", "Dev", 23, BatteryAlertState.Low, null));
    }

    [Fact]
    public void Hysteresis_High_stays_High_within_band_not_charging()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.High,
            o.ClassifyBatteryState("Dev", "Dev", 78, BatteryAlertState.High, false));
    }

    [Fact]
    public void Hysteresis_High_exits_to_Normal_below_band()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Dev", "Dev", 77, BatteryAlertState.High, null));
    }

    [Fact]
    public void Hysteresis_High_exits_to_Normal_when_confirmed_charging_within_band()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Dev", "Dev", 78, BatteryAlertState.High, true));
    }

    // ── Ignored devices ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Ignored_device_always_returns_Normal_regardless_of_battery()
    {
        var settings = new ThresholdSettings();
        settings.ToggleIgnoreDevice("Headphones");
        var o = BuildOrchestrator(settings);

        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Headphones", "Headphones", 5, BatteryAlertState.Normal, null));
        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Headphones", "Headphones", 95, BatteryAlertState.Normal, null));
    }

    [Fact]
    public void Ignored_device_check_is_case_insensitive()
    {
        var settings = new ThresholdSettings();
        settings.ToggleIgnoreDevice("headphones");
        var o = BuildOrchestrator(settings);

        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("HEADPHONES", "HEADPHONES", 5, BatteryAlertState.Normal, null));
    }

    // ── Negative battery (guard) ───────────────────────────────────────────────────────

    [Fact]
    public void Negative_battery_returns_Normal()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Dev", "Dev", -1, BatteryAlertState.Normal, null));
    }

    // ── Per-device overrides ───────────────────────────────────────────────────────────

    [Fact]
    public void Per_device_low_override_triggers_Low_at_custom_threshold()
    {
        var settings = new ThresholdSettings();
        settings.SetLowForDevice("Keyboard", 30);
        var o = BuildOrchestrator(settings);

        Assert.Equal(BatteryAlertState.Low,
            o.ClassifyBatteryState("Keyboard", "Keyboard", 25, BatteryAlertState.Normal, null));
    }

    [Fact]
    public void Per_device_high_override_triggers_High_at_custom_threshold()
    {
        var settings = new ThresholdSettings();
        settings.SetHighForDevice("Mouse", 70);
        var o = BuildOrchestrator(settings);

        Assert.Equal(BatteryAlertState.High,
            o.ClassifyBatteryState("Mouse", "Mouse", 75, BatteryAlertState.Normal, false));
    }

    // ── Threshold-boundary edge cases ─────────────────────────────────────────────────

    [Fact]
    public void Battery_zero_is_Low()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Low,
            o.ClassifyBatteryState("Dev", "Dev", 0, BatteryAlertState.Normal, null));
    }

    [Fact]
    public void Battery_100_not_charging_is_High()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.High,
            o.ClassifyBatteryState("Dev", "Dev", 100, BatteryAlertState.Normal, false));
    }

    [Fact]
    public void Battery_100_confirmed_charging_is_Normal()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.Normal,
            o.ClassifyBatteryState("Dev", "Dev", 100, BatteryAlertState.Normal, true));
    }

    [Fact]
    public void Transition_Low_to_High_without_hysteresis_gap_fires_High()
    {
        var o = BuildOrchestrator();
        Assert.Equal(BatteryAlertState.High,
            o.ClassifyBatteryState("Dev", "Dev", 95, BatteryAlertState.Low, false));
    }
}
