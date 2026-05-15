using BTChargeTrayWatcher;
using Xunit;

public sealed class BatteryDisplayTests
{
    [Theory]
    [InlineData(50, true, "50% \u26a1")]
    [InlineData(50, false, "50%")]
    [InlineData(50, null, "50%")]
    [InlineData(100, true, "100% \u26a1")]
    [InlineData(0, false, "0%")]
    public void FormatBattery_formats_correctly(int pct, bool? charging, string expected)
    {
        Assert.Equal(expected, BatteryDisplay.FormatBattery(pct, charging));
    }
}
