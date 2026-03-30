namespace BTChargeTrayWatcher;

/// <summary>
/// Operational policy constants for the background polling loop.
/// Change these to tune timing and alert-state hysteresis without touching logic.
/// </summary>
internal static class PollingDefaults
{
    /// <summary>Delay before the very first poll after startup.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.Zero;

    /// <summary>Regular interval between background polls.</summary>
    public static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(60);

    /// <summary>Delay before resuming polls after a system resume (wake from sleep).</summary>
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Battery percentage points of hysteresis around a threshold before the
    /// alert state flips back to Normal.
    /// </summary>
    public const int Hysteresis = 2;

    /// <summary>
    /// Number of consecutive polls on which a device must be absent before it is
    /// removed from the known-device cache.
    /// </summary>
    public const int MissCountThreshold = 3;

    /// <summary>
    /// Maximum number of GATT device reads that can proceed concurrently.
    /// </summary>
    public const int GattMaxConcurrentReads = 2;
}
