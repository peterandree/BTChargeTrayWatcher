namespace BTChargeTrayWatcher;

/// <summary>
/// Policy constants for the bounded GATT Battery Level notification subscription
/// (issue #158, approved amendment to ADR-003 and ADR-017).
/// Deliberately separate from <see cref="PollingDefaults"/> so the subscription policy can be
/// re-tuned after the field measurement without touching <see cref="GattConnectionManager"/> logic
/// — the same convention ADR-008 uses for option records.
/// </summary>
internal static class GattSubscriptionDefaults
{
    /// <summary>
    /// Maximum number of devices that may hold a live Battery Level subscription at once.
    /// Mirrors <see cref="PollingDefaults.GattMaxConcurrentReads"/> so a machine with many
    /// peripherals cannot accumulate permanent radio connections.
    /// Set to <c>0</c> to disable subscriptions entirely (the baseline configuration for the
    /// #158 hardware measurement) — the read path then behaves exactly as it did before.
    /// </summary>
    public const int MaxConcurrentSubscriptions = 2;

    /// <summary>
    /// How long a subscription may produce no notification before it is dropped and the device
    /// falls back to uncached polling for the rest of the session. Guards against peripherals
    /// that advertise <c>Notify</c> on 0x2A19 but never publish a value.
    /// </summary>
    public static readonly TimeSpan SettlingWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Upper bound for a single WinRT subscribe/unsubscribe round-trip (device open + CCCD write).
    /// Matches the per-call budget used by the existing read path.
    /// </summary>
    public static readonly TimeSpan WinRtTimeout = TimeSpan.FromSeconds(5);
}
