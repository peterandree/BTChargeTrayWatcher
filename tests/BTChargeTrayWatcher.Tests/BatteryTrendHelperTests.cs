using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class BatteryTrendHelperTests
{
    [Fact]
    public void No_previous_returns_empty()
    {
        Assert.Equal(string.Empty, BatteryTrendHelper.GetArrow(null, 50));
    }

    [Fact]
    public void Current_greater_returns_up_arrow()
    {
        Assert.Equal("↑", BatteryTrendHelper.GetArrow(40, 50));
    }

    [Fact]
    public void Current_lower_returns_down_arrow()
    {
        Assert.Equal("↓", BatteryTrendHelper.GetArrow(75, 50));
    }

    [Fact]
    public void Equal_values_return_empty()
    {
        Assert.Equal(string.Empty, BatteryTrendHelper.GetArrow(60, 60));
    }
}
