using System;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class ThresholdSettingsTests
{
    // ── Global thresholds ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_are_20_and_80()
    {
        var s = new ThresholdSettings();
        Assert.Equal(20, s.Low);
        Assert.Equal(80, s.High);
    }

    [Fact]
    public void Setting_Low_below_High_succeeds()
    {
        var s = new ThresholdSettings();
        s.Low = 15;
        Assert.Equal(15, s.Low);
    }

    [Fact]
    public void Setting_High_above_Low_succeeds()
    {
        var s = new ThresholdSettings();
        s.High = 90;
        Assert.Equal(90, s.High);
    }

    [Fact]
    public void Setting_Low_equal_to_High_throws()
    {
        var s = new ThresholdSettings(); // Low=20, High=80
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Low = 80);
    }

    [Fact]
    public void Setting_Low_above_High_throws()
    {
        var s = new ThresholdSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Low = 90);
    }

    [Fact]
    public void Setting_High_equal_to_Low_throws()
    {
        var s = new ThresholdSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => s.High = 20);
    }

    [Fact]
    public void Setting_same_value_does_not_fire_Changed()
    {
        var s = new ThresholdSettings();
        int count = 0;
        s.Changed += () => count++;
        s.Low = 20; // same as default
        Assert.Equal(0, count);
    }

    [Fact]
    public void Setting_new_value_fires_Changed()
    {
        var s = new ThresholdSettings();
        int count = 0;
        s.Changed += () => count++;
        s.Low = 15;
        Assert.Equal(1, count);
    }

    [Fact]
    public void LaptopLow_equal_to_LaptopHigh_throws()
    {
        var s = new ThresholdSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => s.LaptopLow = 80);
    }

    [Fact]
    public void LaptopHigh_equal_to_LaptopLow_throws()
    {
        var s = new ThresholdSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => s.LaptopHigh = 20);
    }

    // ── Per-device overrides ───────────────────────────────────────────────────────────

    [Fact]
    public void GetLow_returns_global_when_no_override()
    {
        var s = new ThresholdSettings();
        Assert.Equal(20, s.GetLow("Headphones"));
    }

    [Fact]
    public void GetHigh_returns_global_when_no_override()
    {
        var s = new ThresholdSettings();
        Assert.Equal(80, s.GetHigh("Headphones"));
    }

    [Fact]
    public void SetLow_override_is_returned_by_GetLow()
    {
        var s = new ThresholdSettings();
        s.SetLow("Headphones", 10);
        Assert.Equal(10, s.GetLow("Headphones"));
    }

    [Fact]
    public void SetHigh_override_is_returned_by_GetHigh()
    {
        var s = new ThresholdSettings();
        s.SetHigh("Headphones", 90);
        Assert.Equal(90, s.GetHigh("Headphones"));
    }

    [Fact]
    public void Device_override_is_case_insensitive()
    {
        var s = new ThresholdSettings();
        s.SetLow("headphones", 10);
        Assert.Equal(10, s.GetLow("HEADPHONES"));
    }

    [Fact]
    public void SetLow_null_clears_override_and_removes_entry()
    {
        var s = new ThresholdSettings();
        s.SetLow("Headphones", 10);
        s.SetLow("Headphones", null);
        Assert.False(s.HasCustomLow("Headphones"));
        Assert.Equal(20, s.GetLow("Headphones")); // falls back to global
    }

    [Fact]
    public void SetLow_override_above_effective_high_throws()
    {
        var s = new ThresholdSettings();
        // global High = 80; setting Low = 85 should throw
        Assert.Throws<ArgumentOutOfRangeException>(() => s.SetLow("Headphones", 85));
    }

    [Fact]
    public void SetHigh_override_below_effective_low_throws()
    {
        var s = new ThresholdSettings();
        // global Low = 20; setting High = 15 should throw
        Assert.Throws<ArgumentOutOfRangeException>(() => s.SetHigh("Headphones", 15));
    }

    [Fact]
    public void HasCustomLow_false_when_no_override()
    {
        var s = new ThresholdSettings();
        Assert.False(s.HasCustomLow("Headphones"));
    }

    [Fact]
    public void HasCustomHigh_true_after_override_set()
    {
        var s = new ThresholdSettings();
        s.SetHigh("Headphones", 90);
        Assert.True(s.HasCustomHigh("Headphones"));
    }

    // ── Ignore / exclude toggles ────────────────────────────────────────────────────

    [Fact]
    public void ToggleIgnoreDevice_adds_device()
    {
        var s = new ThresholdSettings();
        s.ToggleIgnoreDevice("Keyboard");
        Assert.Contains("Keyboard", s.IgnoredDevices);
    }

    [Fact]
    public void ToggleIgnoreDevice_removes_device_on_second_call()
    {
        var s = new ThresholdSettings();
        s.ToggleIgnoreDevice("Keyboard");
        s.ToggleIgnoreDevice("Keyboard");
        Assert.DoesNotContain("Keyboard", s.IgnoredDevices);
    }

    [Fact]
    public void ToggleIgnoreDevice_is_case_insensitive()
    {
        var s = new ThresholdSettings();
        s.ToggleIgnoreDevice("keyboard");
        Assert.Contains("KEYBOARD", s.IgnoredDevices);
    }

    [Fact]
    public void ToggleExcludeFromTrayIconOverlay_adds_device()
    {
        var s = new ThresholdSettings();
        s.ToggleExcludeFromTrayIconOverlay("Mouse");
        Assert.Contains("Mouse", s.TrayIconOverlayExcludedDevices);
    }

    [Fact]
    public void ToggleExcludeFromTrayIconOverlay_removes_on_second_call()
    {
        var s = new ThresholdSettings();
        s.ToggleExcludeFromTrayIconOverlay("Mouse");
        s.ToggleExcludeFromTrayIconOverlay("Mouse");
        Assert.DoesNotContain("Mouse", s.TrayIconOverlayExcludedDevices);
    }

    // ── ntfy settings ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateNtfySettings_mutation_is_visible_via_GetNtfySettings()
    {
        var s = new ThresholdSettings();
        s.UpdateNtfySettings(n => { n.Topic = "btcw-test"; n.IsEnabled = true; });
        var snap = s.GetNtfySettings();
        Assert.Equal("btcw-test", snap.Topic);
        Assert.True(snap.IsEnabled);
    }

    [Fact]
    public void GetNtfySettings_returns_snapshot_not_live_reference()
    {
        var s = new ThresholdSettings();
        s.UpdateNtfySettings(n => n.Topic = "btcw-original");
        var snap = s.GetNtfySettings();
        s.UpdateNtfySettings(n => n.Topic = "btcw-changed");
        Assert.Equal("btcw-original", snap.Topic); // snapshot is independent
    }

    [Fact]
    public void UpdateNtfySettings_fires_Changed()
    {
        var s = new ThresholdSettings();
        int count = 0;
        s.Changed += () => count++;
        s.UpdateNtfySettings(n => n.IsEnabled = true);
        Assert.Equal(1, count);
    }
}
