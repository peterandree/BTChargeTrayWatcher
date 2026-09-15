using System.Windows.Forms;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Regression coverage for the tray context menu restored in #156: the laptop submenu
/// (thresholds + exclusions), the global low/high pickers, and the per-device submenus
/// that replaced the bare <c>Enabled = false</c> device labels.
/// </summary>
public sealed class TrayMenuBuilderTests
{
    // ── Laptop submenu ──────────────────────────────────────────────────────

    [StaFact]
    public void Laptop_menu_exposes_thresholds_and_exclusion_toggles()
    {
        var settings = new ThresholdSettings();
        var builder = new TrayMenuBuilder(settings);

        var laptop = builder.BuildLaptopMenuItem();

        Assert.NotNull(laptop.DropDown);
        Assert.Contains(laptop.DropDownItems.OfType<ToolStripMenuItem>(),
            i => i.Text == "Low threshold");
        Assert.Contains(laptop.DropDownItems.OfType<ToolStripMenuItem>(),
            i => i.Text == "High threshold");
        Assert.Contains(laptop.DropDownItems.OfType<ToolStripMenuItem>(),
            i => i.Text == "Exclude from tray icon alert");
        Assert.Contains(laptop.DropDownItems.OfType<ToolStripMenuItem>(),
            i => i.Text == "Exclude from monitoring and alerts");
    }

    [StaFact]
    public void Laptop_low_threshold_entry_updates_LaptopLow()
    {
        var settings = new ThresholdSettings();
        var laptop = new TrayMenuBuilder(settings).BuildLaptopMenuItem();

        ClickThreshold(laptop, "Low threshold", 15);

        Assert.Equal(15, settings.LaptopLow);
    }

    [StaFact]
    public void Laptop_high_threshold_entry_updates_LaptopHigh()
    {
        var settings = new ThresholdSettings();
        var laptop = new TrayMenuBuilder(settings).BuildLaptopMenuItem();

        ClickThreshold(laptop, "High threshold", 90);

        Assert.Equal(90, settings.LaptopHigh);
    }

    [StaFact]
    public void Laptop_monitoring_toggle_writes_the_monitoring_flag()
    {
        var settings = new ThresholdSettings();
        var laptop = new TrayMenuBuilder(settings).BuildLaptopMenuItem();

        var toggle = FindItem(laptop, "Exclude from monitoring and alerts");
        toggle.Checked = true;

        Assert.True(settings.ExcludeLaptopFromMonitoring);

        toggle.Checked = false;
        Assert.False(settings.ExcludeLaptopFromMonitoring);
    }

    [StaFact]
    public void Laptop_overlay_toggle_writes_the_overlay_flag()
    {
        var settings = new ThresholdSettings();
        var laptop = new TrayMenuBuilder(settings).BuildLaptopMenuItem();

        var toggle = FindItem(laptop, "Exclude from tray icon alert");
        toggle.Checked = true;

        Assert.True(settings.ExcludeLaptopFromTrayIconOverlay);
    }

    // ── Global threshold pickers ────────────────────────────────────────────

    [StaFact]
    public void Global_low_menu_writes_the_global_low_threshold()
    {
        var settings = new ThresholdSettings();
        var builder = new TrayMenuBuilder(settings);

        ClickThreshold(builder.BuildLowMenu(), "Low threshold", 10);

        Assert.Equal(10, settings.Low);
    }

    [StaFact]
    public void Global_high_menu_writes_the_global_high_threshold()
    {
        var settings = new ThresholdSettings();
        var builder = new TrayMenuBuilder(settings);

        ClickThreshold(builder.BuildHighMenu(), "High threshold", 85);

        Assert.Equal(85, settings.High);
    }

    // ── Per-device submenu ──────────────────────────────────────────────────

    [StaFact]
    public void Device_menu_shows_current_level_and_display_name_alias()
    {
        var settings = new ThresholdSettings();
        settings.SetDisplayNameAlias("ble-1", "My Headset");

        var item = TrayMenuBuilder.BuildDeviceMenuItem(
            settings, new DeviceBatteryInfo("ble-1", "Headset", 55, IsCharging: true));

        Assert.Contains("My Headset", item.Text);
        Assert.Contains("55%", item.Text);
        Assert.Contains("\u26a1", item.Text);
    }

    [StaFact]
    public void Device_menu_low_threshold_writes_a_per_device_override()
    {
        var settings = new ThresholdSettings();
        var item = TrayMenuBuilder.BuildDeviceMenuItem(
            settings, new DeviceBatteryInfo("ble-1", "Headset", 55));

        ClickThreshold(item, "Low threshold", 25);

        Assert.Equal(25, settings.GetLowForDevice("ble-1", "Headset"));
        Assert.Equal(20, settings.Low); // global default untouched
    }

    [StaFact]
    public void Device_menu_high_threshold_writes_a_per_device_override()
    {
        var settings = new ThresholdSettings();
        var item = TrayMenuBuilder.BuildDeviceMenuItem(
            settings, new DeviceBatteryInfo("ble-1", "Headset", 55));

        ClickThreshold(item, "High threshold", 90);

        Assert.Equal(90, settings.GetHighForDevice("ble-1", "Headset"));
    }

    [StaFact]
    public void Device_menu_ignore_toggle_uses_the_device_id()
    {
        var settings = new ThresholdSettings();
        var item = TrayMenuBuilder.BuildDeviceMenuItem(
            settings, new DeviceBatteryInfo("ble-1", "Headset", 55));

        var ignore = FindItem(item, "Ignore device (no notifications)");
        ignore.Checked = true;

        Assert.True(settings.IsIgnored("ble-1", "Headset"));
        Assert.Contains("ble-1", settings.IgnoredDevices);
    }

    [StaFact]
    public void Device_menu_overlay_toggle_uses_the_device_id()
    {
        var settings = new ThresholdSettings();
        var item = TrayMenuBuilder.BuildDeviceMenuItem(
            settings, new DeviceBatteryInfo("ble-1", "Headset", 55));

        var toggle = FindItem(item, "Exclude from tray icon alert");
        toggle.Checked = true;

        Assert.True(settings.IsTrayIconOverlayExcluded("ble-1", "Headset"));
    }

    [StaFact]
    public void Device_menu_shows_NA_when_battery_is_unknown()
    {
        var item = TrayMenuBuilder.BuildDeviceMenuItem(
            new ThresholdSettings(), new DeviceBatteryInfo("ble-1", "Headset", null));

        Assert.Contains("N/A", item.Text);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ToolStripMenuItem FindItem(ToolStripMenuItem parent, string text) =>
        parent.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == text);

    private static void ClickThreshold(ToolStripMenuItem parent, string submenuText, int candidate)
    {
        var submenu = FindItem(parent, submenuText);
        var entry = submenu.DropDownItems.OfType<ToolStripMenuItem>()
            .Single(i => i.Text == $"{candidate} %");
        entry.PerformClick();
    }
}
