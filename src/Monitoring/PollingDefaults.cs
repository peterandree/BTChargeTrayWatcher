namespace BTChargeTrayWatcher;

/// <summary>
/// Operational policy constants for the background polling loop.
/// Change these to tune timing and alert-state hysteresis without touching logic.
/// </summary>
internal static class PollingDefaults
{
    /// <summary>Delay before the very first poll after startup.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// Global time budget for a user-initiated diagnostic deep scan (ADR-019 §2): the scan is
    /// cancelled when it expires, and the UI reports that the budget — not the user — stopped it.
    /// This is the binding limit for a scan; per-device reader timeouts are deliberately left
    /// unchanged so a single slow device cannot push the run past the budget (see the ADR note).
    /// </summary>
    public static readonly TimeSpan DeepScanTimeBudget = TimeSpan.FromSeconds(30);
}
