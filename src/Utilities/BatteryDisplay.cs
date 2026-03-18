namespace BTChargeTrayWatcher;

public static class BatteryDisplay
{
    public static string Bar(int pct)
    {
        int clamped = Math.Clamp(pct, 0, 100);
        int filled = (int)Math.Round(clamped / 10.0, MidpointRounding.AwayFromZero);
        return "[" + new string('\u2588', filled) + new string('\u2591', 10 - filled) + "]";
    }
}
