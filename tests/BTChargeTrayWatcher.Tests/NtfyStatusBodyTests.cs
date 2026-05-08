using System.Collections.Generic;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Tests for NtfyNotificationChannel.BuildStatusBody.
/// No HTTP; tests pure string-formatting logic.
/// </summary>
public sealed class NtfyStatusBodyTests
{
    private static DeviceBatteryInfo BtDev(string name, int battery, bool? charging = null)
        => new(name, name, battery, charging);

    // ── BT devices only ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Single_BT_device_formats_name_and_percent()
    {
        var body = NtfyNotificationChannel.BuildStatusBody(
            [BtDev("Headphones", 75)], null);
        Assert.Equal("Headphones 75%", body);
    }

    [Fact]
    public void Charging_BT_device_appends_lightning_symbol()
    {
        var body = NtfyNotificationChannel.BuildStatusBody(
            [BtDev("Headphones", 75, charging: true)], null);
        Assert.Equal("Headphones 75% \u26a1", body);
    }

    [Fact]
    public void Not_charging_BT_device_has_no_lightning_symbol()
    {
        var body = NtfyNotificationChannel.BuildStatusBody(
            [BtDev("Headphones", 75, charging: false)], null);
        Assert.Equal("Headphones 75%", body);
    }

    [Fact]
    public void Multiple_BT_devices_are_newline_separated()
    {
        var body = NtfyNotificationChannel.BuildStatusBody(
            [BtDev("A", 50), BtDev("B", 60)], null);
        Assert.Equal("A 50%\nB 60%", body);
    }

    [Fact]
    public void BT_device_with_null_battery_is_skipped()
    {
        var body = NtfyNotificationChannel.BuildStatusBody(
            [new DeviceBatteryInfo("id", "Ghost", null), BtDev("Real", 50)], null);
        Assert.Equal("Real 50%", body);
    }

    [Fact]
    public void All_BT_devices_null_battery_and_no_laptop_returns_fallback()
    {
        var body = NtfyNotificationChannel.BuildStatusBody(
            [new DeviceBatteryInfo("id", "Ghost", null)], null);
        Assert.Equal("No devices currently known.", body);
    }

    // ── Laptop only ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Laptop_only_discharging_formats_percent()
    {
        var laptop = new LaptopBatteryInfo(HasBattery: true, BatteryPercent: 55,
            IsCharging: false, IsOnAcPower: false);
        var body = NtfyNotificationChannel.BuildStatusBody([], laptop);
        Assert.Equal("Laptop 55%", body);
    }

    [Fact]
    public void Laptop_charging_appends_charging_label()
    {
        var laptop = new LaptopBatteryInfo(HasBattery: true, BatteryPercent: 55,
            IsCharging: true, IsOnAcPower: true);
        var body = NtfyNotificationChannel.BuildStatusBody([], laptop);
        Assert.Equal("Laptop 55% (charging)", body);
    }

    [Fact]
    public void Laptop_on_AC_but_not_charging_appends_plugged_in_label()
    {
        var laptop = new LaptopBatteryInfo(HasBattery: true, BatteryPercent: 80,
            IsCharging: false, IsOnAcPower: true);
        var body = NtfyNotificationChannel.BuildStatusBody([], laptop);
        Assert.Equal("Laptop 80% (plugged in)", body);
    }

    [Fact]
    public void Laptop_HasBattery_false_is_omitted()
    {
        var laptop = new LaptopBatteryInfo(HasBattery: false, BatteryPercent: 0,
            IsCharging: false, IsOnAcPower: false);
        var body = NtfyNotificationChannel.BuildStatusBody([], laptop);
        Assert.Equal("No devices currently known.", body);
    }

    // ── Mixed ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BT_and_laptop_are_newline_separated()
    {
        var laptop = new LaptopBatteryInfo(HasBattery: true, BatteryPercent: 90,
            IsCharging: false, IsOnAcPower: false);
        var body = NtfyNotificationChannel.BuildStatusBody(
            [BtDev("Headphones", 60)], laptop);
        Assert.Equal("Headphones 60%\nLaptop 90%", body);
    }

    // ── Empty ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_devices_no_laptop_returns_fallback_message()
    {
        var body = NtfyNotificationChannel.BuildStatusBody([], null);
        Assert.Equal("No devices currently known.", body);
    }
}
