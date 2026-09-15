namespace BTChargeTrayWatcher;

/// <summary>
/// Pure decision logic for the bounded GATT Battery Level subscription (issue #160).
/// No I/O, no WinRT, no clock — the three inputs are supplied by the caller, which makes the
/// whole policy unit-testable without Bluetooth hardware.
/// </summary>
internal static class GattSubscriptionPolicy
{
    /// <summary>
    /// Decides whether a device may be subscribed right now.
    /// </summary>
    /// <param name="supportsNotify">
    /// The Battery Level characteristic (0x2A19) advertises <c>CharacteristicProperties.Notify</c>.
    /// A device that can only be read is never subscribed.
    /// </param>
    /// <param name="isConnected">
    /// The device is currently <c>BluetoothConnectionStatus.Connected</c>. A subscription must never
    /// force a connection (ADR-017), so a sleeping peripheral is left alone.
    /// </param>
    /// <param name="activeSubscriptions">Number of subscriptions currently held.</param>
    /// <param name="maxConcurrentSubscriptions">
    /// Cap from <see cref="GattSubscriptionDefaults.MaxConcurrentSubscriptions"/>. <c>0</c> disables
    /// subscriptions entirely (baseline configuration for the #158 measurement).
    /// </param>
    /// <returns><c>true</c> only when all three conditions hold.</returns>
    public static bool ShouldSubscribe(
        bool supportsNotify,
        bool isConnected,
        int activeSubscriptions,
        int maxConcurrentSubscriptions)
    {
        if (!supportsNotify) return false;
        if (!isConnected) return false;
        if (maxConcurrentSubscriptions <= 0) return false;
        return activeSubscriptions < maxConcurrentSubscriptions;
    }

    /// <summary>
    /// Whether a GATT read may be served from the Windows GATT cache. Only a subscribed device on
    /// the background poll path may: a deep scan must return a value read from the device itself
    /// (ADR-019), and a device without a subscription has no push source to keep a cache fresh.
    /// </summary>
    public static bool ShouldReadBatteryFromCache(bool subscribed, BatteryReadMode mode)
        => subscribed && mode == BatteryReadMode.Background;

    /// <summary>
    /// Whether a GATT read may create a notification subscription. Subscriptions belong to the
    /// background poll only: ADR-019 §2 rules them out during a deep scan, and an already-
    /// subscribed device does not need a second one.
    /// </summary>
    public static bool ShouldAttemptSubscribe(bool subscribed, BatteryReadMode mode)
        => !subscribed && mode == BatteryReadMode.Background;

    /// <summary>
    /// A subscription that has produced no notification within the settling window is dropped and
    /// must not be retried for the rest of the session. A subscription that has produced at least
    /// one notification is never dropped by the settling rule.
    /// </summary>
    /// <param name="hasNotified">Whether at least one notification was received for this device.</param>
    /// <param name="subscribedAtUtc">When the subscription became active.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="settlingWindow">Window from <see cref="GattSubscriptionDefaults.SettlingWindow"/>.</param>
    public static bool HasFailedSettling(
        bool hasNotified,
        DateTime subscribedAtUtc,
        DateTime nowUtc,
        TimeSpan settlingWindow)
    {
        if (hasNotified) return false;
        return nowUtc - subscribedAtUtc >= settlingWindow;
    }
}
