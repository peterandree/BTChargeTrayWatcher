namespace BTChargeTrayWatcher;

/// <summary>
/// Why a battery read is happening. Replaces the bare <c>skipConnectionCheck</c> boolean at the
/// reader boundary: that flag was a negative name covering two distinct modes, and one call site
/// hardcoded it, which is how the manual scan ended up on the background path (issue #164).
/// </summary>
internal enum BatteryReadMode
{
    /// <summary>
    /// The 60 s poll cycle (and the scanner's quiet read). Passive by policy (ADR-017): the Classic
    /// reader reuses <see cref="DeviceWatcherService"/>'s <c>IsConnected</c> data instead of issuing
    /// per-device radio queries, a GATT device that advertises <c>Notify</c> may hold a bounded
    /// notification subscription, and a subscribed device is read from the Windows GATT cache.
    /// </summary>
    Background = 0,

    /// <summary>
    /// A user-initiated diagnostic scan (ADR-019). Active by design: the Classic reader verifies
    /// each candidate on the live radio, and a GATT read is always uncached and never creates a
    /// notification subscription — ADR-019 §2 forbids subscribing during a deep scan, and a
    /// diagnostic read must not mutate background state. Existing subscriptions are left alone.
    /// </summary>
    DeepScan = 1,
}
