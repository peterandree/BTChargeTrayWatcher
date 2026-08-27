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

    /// <summary>Formats a duration in minutes as "2 h 10 m" or "45 m" (#154).</summary>
    public static string FormatDuration(float? minutes)
    {
        if (minutes is null or <= 0) return "unknown";
        int totalMinutes = (int)Math.Round(minutes.Value);
        int h = totalMinutes / 60;
        int m = totalMinutes % 60;
        return h > 0 ? $"{h} h {m} m" : $"{m} m";
    }

    /// <summary>Formats discharge/charge rate in watts (#154).</summary>
    public static string FormatPowerRate(float? watts)
    {
        if (watts is null) return "unknown";
        return $"{watts.Value:F1} W";
    }

    /// <summary>Formats battery health percentage (#154).</summary>
    public static string FormatHealth(float? healthPercent)
    {
        if (healthPercent is null) return "unknown";
        return $"{healthPercent.Value:F0}%";
    }
}
