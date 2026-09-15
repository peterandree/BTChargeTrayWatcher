using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Covers the #156 additions: the laptop monitoring-exclusion flag and the device-id-aware
/// ignore/overlay-exclusion toggles used by the restored tray menus.
/// </summary>
public sealed class LaptopMonitoringExclusionTests
{
    // ── ExcludeLaptopFromMonitoring ─────────────────────────────────────────

    [Fact]
    public void Laptop_monitoring_exclusion_defaults_to_false()
    {
        var s = new ThresholdSettings();
        Assert.False(s.ExcludeLaptopFromMonitoring);
    }

    [Fact]
    public void Setting_laptop_monitoring_exclusion_fires_Changed_once()
    {
        var s = new ThresholdSettings();
        int count = 0;
        s.Changed += () => count++;

        s.ExcludeLaptopFromMonitoring = true;
        s.ExcludeLaptopFromMonitoring = true; // no-op

        Assert.True(s.ExcludeLaptopFromMonitoring);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Laptop_monitoring_exclusion_survives_snapshot_round_trip()
    {
        var s = new ThresholdSettings();
        s.ExcludeLaptopFromMonitoring = true;
        s.ExcludeLaptopFromTrayIconOverlay = true;

        var snapshot = s.Snapshot();

        var restored = new ThresholdSettings();
        restored.ApplySnapshot(snapshot);

        Assert.True(restored.ExcludeLaptopFromMonitoring);
        Assert.True(restored.ExcludeLaptopFromTrayIconOverlay);
    }

    [Fact]
    public void Laptop_monitoring_exclusion_is_independent_of_overlay_exclusion()
    {
        var s = new ThresholdSettings();
        s.ExcludeLaptopFromMonitoring = true;

        Assert.False(s.ExcludeLaptopFromTrayIconOverlay);
    }

    // ── Device-id-aware ignore toggle ───────────────────────────────────────

    [Fact]
    public void SetIgnored_adds_device_id()
    {
        var s = new ThresholdSettings();
        s.SetIgnored("ble-1", "Headset", ignored: true);

        Assert.True(s.IsIgnored("ble-1", "Headset"));
        Assert.Contains("ble-1", s.IgnoredDevices);
    }

    [Fact]
    public void SetIgnored_migrates_legacy_name_keyed_entry_to_device_id()
    {
        var s = new ThresholdSettings();
        s.SetIgnoredDevices(["Headset"]); // legacy name-keyed entry

        s.SetIgnored("ble-1", "Headset", ignored: true);

        Assert.Contains("ble-1", s.IgnoredDevices);
        Assert.DoesNotContain("Headset", s.IgnoredDevices);
    }

    [Fact]
    public void SetIgnored_false_removes_both_id_and_name_entries()
    {
        var s = new ThresholdSettings();
        s.SetIgnoredDevices(["Headset"]);
        s.SetIgnored("ble-1", "Headset", ignored: true);

        s.SetIgnored("ble-1", "Headset", ignored: false);

        Assert.False(s.IsIgnored("ble-1", "Headset"));
        Assert.Empty(s.IgnoredDevices);
    }

    // ── Device-id-aware overlay exclusion toggle ────────────────────────────

    [Fact]
    public void SetTrayIconOverlayExcluded_adds_and_removes_device_id()
    {
        var s = new ThresholdSettings();

        s.SetTrayIconOverlayExcluded("ble-1", "Headset", excluded: true);
        Assert.True(s.IsTrayIconOverlayExcluded("ble-1", "Headset"));
        Assert.Contains("ble-1", s.TrayIconOverlayExcludedDevices);

        s.SetTrayIconOverlayExcluded("ble-1", "Headset", excluded: false);
        Assert.False(s.IsTrayIconOverlayExcluded("ble-1", "Headset"));
        Assert.Empty(s.TrayIconOverlayExcludedDevices);
    }

    [Fact]
    public void SetTrayIconOverlayExcluded_migrates_legacy_name_keyed_entry()
    {
        var s = new ThresholdSettings();
        s.SetTrayIconOverlayExcludedDevices(["Headset"]);

        s.SetTrayIconOverlayExcluded("ble-1", "Headset", excluded: true);

        Assert.Contains("ble-1", s.TrayIconOverlayExcludedDevices);
        Assert.DoesNotContain("Headset", s.TrayIconOverlayExcludedDevices);
    }

    // ── Tray view-model alert suppression (pure decision) ───────────────────

    private static LaptopBatteryInfo Laptop(int pct) =>
        new(HasBattery: true, BatteryPercent: pct, IsCharging: false, IsOnAcPower: false);

    [Fact]
    public void Monitoring_exclusion_suppresses_the_laptop_alert_marker()
    {
        bool alert = TrayViewModel.IsLaptopAlert(
            Laptop(10), low: 20, high: 80, excludeFromMonitoring: true, excludeFromOverlay: false);

        Assert.False(alert);
    }

    [Fact]
    public void Overlay_exclusion_still_suppresses_the_laptop_alert_marker()
    {
        bool alert = TrayViewModel.IsLaptopAlert(
            Laptop(10), low: 20, high: 80, excludeFromMonitoring: false, excludeFromOverlay: true);

        Assert.False(alert);
    }

    [Fact]
    public void Monitored_laptop_below_threshold_raises_the_alert_marker()
    {
        bool alert = TrayViewModel.IsLaptopAlert(
            Laptop(10), low: 20, high: 80, excludeFromMonitoring: false, excludeFromOverlay: false);

        Assert.True(alert);
    }

    [Fact]
    public void Unknown_laptop_level_never_raises_the_alert_marker()
    {
        bool alert = TrayViewModel.IsLaptopAlert(
            Laptop(-1), low: 20, high: 80, excludeFromMonitoring: false, excludeFromOverlay: false);

        Assert.False(alert);
    }
}
