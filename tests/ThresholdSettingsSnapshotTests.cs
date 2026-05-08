using System.Linq;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Verifies Snapshot/ApplySnapshot round-trip fidelity for all fields.
/// </summary>
public sealed class ThresholdSettingsSnapshotTests
{
    [Fact]
    public void Snapshot_round_trip_preserves_global_thresholds()
    {
        var original = new ThresholdSettings();
        original.Low  = 15;
        original.High = 85;
        original.LaptopLow  = 10;
        original.LaptopHigh = 90;

        var snapshot = original.Snapshot();

        var restored = new ThresholdSettings();
        restored.ApplySnapshot(snapshot);

        Assert.Equal(15, restored.Low);
        Assert.Equal(85, restored.High);
        Assert.Equal(10, restored.LaptopLow);
        Assert.Equal(90, restored.LaptopHigh);
    }

    [Fact]
    public void Snapshot_round_trip_preserves_ignored_devices()
    {
        var original = new ThresholdSettings();
        original.SetIgnoredDevices(["Mouse", "Keyboard"]);

        var restored = new ThresholdSettings();
        restored.ApplySnapshot(original.Snapshot());

        var ignored = restored.IgnoredDevices;
        Assert.Contains("Mouse",    ignored);
        Assert.Contains("Keyboard", ignored);
        Assert.Equal(2, ignored.Count);
    }

    [Fact]
    public void Snapshot_round_trip_preserves_device_overrides()
    {
        var original = new ThresholdSettings();
        original.SetLow("Headset",  15);
        original.SetHigh("Headset", 75);

        var restored = new ThresholdSettings();
        restored.ApplySnapshot(original.Snapshot());

        Assert.True(restored.HasCustomLow("Headset"));
        Assert.True(restored.HasCustomHigh("Headset"));
        Assert.Equal(15, restored.GetLow("Headset"));
        Assert.Equal(75, restored.GetHigh("Headset"));
    }

    [Fact]
    public void Snapshot_round_trip_preserves_ntfy_settings()
    {
        var original = new ThresholdSettings();
        original.UpdateNtfySettings(n =>
        {
            n.IsEnabled = true;
            n.Topic     = "my-topic";
            n.ServerUrl = "https://ntfy.example.com";
        });

        var restored = new ThresholdSettings();
        restored.ApplySnapshot(original.Snapshot());

        var ntfy = restored.GetNtfySettings();
        Assert.True(ntfy.IsEnabled);
        Assert.Equal("my-topic",              ntfy.Topic);
        Assert.Equal("https://ntfy.example.com", ntfy.ServerUrl);
    }

    [Fact]
    public void Snapshot_is_isolated_mutation_of_original_does_not_affect_snapshot()
    {
        var original = new ThresholdSettings();
        original.Low  = 15;
        original.High = 85;

        var snapshot = original.Snapshot();

        // Mutate original after snapshot
        original.Low  = 5;
        original.High = 95;

        Assert.Equal(15, snapshot.Low);
        Assert.Equal(85, snapshot.High);
    }

    [Fact]
    public void Snapshot_round_trip_preserves_ExcludeLaptopFromTrayIconOverlay()
    {
        var original = new ThresholdSettings();
        original.ExcludeLaptopFromTrayIconOverlay = true;

        var restored = new ThresholdSettings();
        restored.ApplySnapshot(original.Snapshot());

        Assert.True(restored.ExcludeLaptopFromTrayIconOverlay);
    }

    [Fact]
    public void Snapshot_round_trip_preserves_TrayIconOverlayExcludedDevices()
    {
        var original = new ThresholdSettings();
        original.ToggleExcludeFromTrayIconOverlay("Headset");
        original.ToggleExcludeFromTrayIconOverlay("Speaker");

        var restored = new ThresholdSettings();
        restored.ApplySnapshot(original.Snapshot());

        var excluded = restored.TrayIconOverlayExcludedDevices;
        Assert.Contains("Headset", excluded);
        Assert.Contains("Speaker", excluded);
    }
}
