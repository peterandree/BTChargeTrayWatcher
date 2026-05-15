using System;

namespace BTChargeTrayWatcher;

public static class BatteryTrendHelper
{
    /// <summary>
    /// Returns the trend arrow for a battery reading compared to a previous reading.
    /// Returns "↑" when current &gt; previous, "↓" when current &lt; previous, and
    /// an empty string when there is no previous value or values are equal.
    /// </summary>
    public static string GetArrow(int? previous, int current)
    {
        if (!previous.HasValue) return string.Empty;
        if (current > previous.Value) return "↑";
        if (current < previous.Value) return "↓";
        return string.Empty;
    }
}
