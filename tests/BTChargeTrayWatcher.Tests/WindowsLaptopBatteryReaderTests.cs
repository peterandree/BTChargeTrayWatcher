using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Unit tests for <see cref="WindowsLaptopBatteryReader.ToBatteryPercent"/>.
/// The reader itself is Tier 3 (requires a WinForms message pump and a physical
/// battery, see TESTING.md), so the pure raw-percent conversion is extracted as
/// an internal static and tested here — same pattern as NtfyStatusBodyTests.
/// Covers #144: Windows reports an unknown level as byte 255, which the framework
/// surfaces as 1.0 (indistinguishable from a genuinely full battery), so it must
/// map to -1 (unknown) instead of a fabricated 100 %.
/// </summary>
public sealed class WindowsLaptopBatteryReaderTests
{
    [Theory]
    [InlineData(0.0f, 0)]     // empty battery
    [InlineData(0.1f, 10)]
    [InlineData(0.25f, 25)]
    [InlineData(0.5f, 50)]
    [InlineData(0.99f, 99)]
    [InlineData(1.0f, -1)]    // #144: byte 100 (full) OR byte 255 (unknown) — both 1.0 → unknown
    [InlineData(2.55f, -1)]   // defensive: raw 255/100 before clamping
    [InlineData(-0.1f, -1)]   // defensive: out of range
    public void ToBatteryPercent_maps_raw_values(float rawPercent, int expected)
    {
        Assert.Equal(expected, WindowsLaptopBatteryReader.ToBatteryPercent(rawPercent));
    }
}
