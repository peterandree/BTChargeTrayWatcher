namespace BTChargeTrayWatcher;

public static class BatteryDisplay
{
    public static string Bar(int pct)
    {
        int clamped = Math.Clamp(pct, 0, 100);
        int filled = (int)Math.Round(clamped / 10.0, MidpointRounding.AwayFromZero);
        return "[" + new string('\u2588', filled) + new string('\u2591', 10 - filled) + "]";
    }

    /// <summary>
    /// Formats the battery percentage cell text.
    /// Appends " \u26a1" (⚡) only when charging is confirmed true.
    /// A negative value (the -1 sentinel used by <see cref="LaptopBatteryInfo"/> when the
    /// level is unknown, see #144) renders as "unknown" instead of a fabricated percentage.
    /// </summary>
    public static string FormatBattery(int battery, bool? isCharging)
    {
        if (battery < 0) return "unknown";
        return isCharging == true ? $"{battery}% \u26a1" : $"{battery}%";
    }
}
