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
    [InlineData(-1, false, "unknown")]   // #144: unknown level sentinel
    [InlineData(-1, true, "unknown")]    // #144: unknown level sentinel (charging state irrelevant)
    public void FormatBattery_formats_correctly(int pct, bool? charging, string expected)
    {
        Assert.Equal(expected, BatteryDisplay.FormatBattery(pct, charging));
    }

    [Theory]
    [InlineData(null,       "unknown")]
    [InlineData(0f,         "unknown")]
    [InlineData(45f,        "45 m")]
    [InlineData(130f,       "2 h 10 m")]
    [InlineData(59.5f,      "1 h 0 m")]
    [InlineData(1f,         "1 m")]
    public void FormatDuration_formats_correctly(float? minutes, string expected)
    {
        Assert.Equal(expected, BatteryDisplay.FormatDuration(minutes));
    }

    [Theory]
    [InlineData(null,   "unknown")]
    [InlineData(8.4f,   "8.4 W")]
    [InlineData(0f,     "0.0 W")]
    public void FormatPowerRate_formats_correctly(float? watts, string expected)
    {
        Assert.Equal(expected, BatteryDisplay.FormatPowerRate(watts));
    }

    [Theory]
    [InlineData(null,   "unknown")]
    [InlineData(90.5f,  "91%")]
    [InlineData(100f,   "100%")]
    public void FormatHealth_formats_correctly(float? health, string expected)
    {
        Assert.Equal(expected, BatteryDisplay.FormatHealth(health));
    }
}
