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
