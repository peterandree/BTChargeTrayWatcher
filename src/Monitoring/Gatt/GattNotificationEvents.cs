namespace BTChargeTrayWatcher;

/// <summary>
/// Raised by an <see cref="IGattNotificationSubscription"/> implementation when the peripheral
/// pushes a new Battery Level (0x2A19) value.
/// </summary>
internal sealed class GattNotificationReceivedEventArgs(string deviceId, int battery) : EventArgs
{
    public string DeviceId { get; } = deviceId;

    /// <summary>Battery percentage reported by the peripheral (0–100).</summary>
    public int Battery { get; } = battery;

    /// <summary>When the notification was observed (UTC).</summary>
    public DateTime TimestampUtc { get; } = DateTime.UtcNow;
}

/// <summary>
/// Raised by an <see cref="IGattNotificationSubscription"/> implementation when a live
/// subscription ends without the owner asking for it — the peripheral disconnected or the
/// WinRT characteristic faulted. The owner must release its bookkeeping for
/// <see cref="DeviceId"/>; the implementation has already dropped its own WinRT references.
/// </summary>
internal sealed class GattSubscriptionLostEventArgs(
    string deviceId,
    GattSubscriptionDropReason reason) : EventArgs
{
    public string DeviceId { get; } = deviceId;

    /// <summary>
    /// Why the subscription ended. Only device-driven reasons
    /// (<see cref="GattSubscriptionDropReason.Disconnected"/>) are raised from here;
    /// owner-driven teardown is initiated by the owner itself.
    /// </summary>
    public GattSubscriptionDropReason Reason { get; } = reason;
}
