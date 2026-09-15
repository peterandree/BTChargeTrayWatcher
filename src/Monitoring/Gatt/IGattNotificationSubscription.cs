namespace BTChargeTrayWatcher;

/// <summary>
/// Seam over the WinRT Battery Level (0x2A19) notification subscription (issue #160).
/// The production implementation is <see cref="WinRtGattNotificationSubscription"/>;
/// tests use a fake and therefore never touch WinRT or hardware.
/// </summary>
/// <remarks>
/// A live subscription holds a <c>BluetoothLEDevice</c> and a <c>GattCharacteristic</c> open —
/// exactly what ADR-003 and ADR-017 originally forbade. That deviation is bounded and revocable:
/// the owner (<see cref="GattSubscriptionCoordinator"/>) must call <see cref="UnsubscribeAsync"/>
/// on every teardown path, and the implementation must release its references in the same call.
/// </remarks>
internal interface IGattNotificationSubscription : IAsyncDisposable
{
    /// <summary>Raised for every Battery Level notification received from a subscribed device.</summary>
    event EventHandler<GattNotificationReceivedEventArgs>? NotificationReceived;

    /// <summary>
    /// Raised when a live subscription ends without the owner asking — notably a peripheral
    /// disconnect. The implementation must have released its WinRT references before raising.
    /// </summary>
    event EventHandler<GattSubscriptionLostEventArgs>? SubscriptionLost;

    /// <summary>
    /// Enables Battery Level notifications for one device by writing the Client Characteristic
    /// Configuration Descriptor. Returns <c>false</c> when the device cannot be subscribed
    /// (not connected, no battery service, no <c>Notify</c> property, or a WinRT fault) — in which
    /// case the caller keeps using the existing uncached read path and nothing is left open.
    /// </summary>
    Task<bool> SubscribeAsync(string deviceId, string fallbackName, CancellationToken ct);

    /// <summary>
    /// Stops notifications for one device and releases the WinRT references held for it.
    /// Writes the CCCD back to <c>None</c> on a best-effort basis; must be safe to call for a
    /// device that is not subscribed.
    /// </summary>
    Task UnsubscribeAsync(string deviceId, CancellationToken ct);

    /// <summary>Whether a live subscription is currently held for <paramref name="deviceId"/>.</summary>
    bool IsSubscribed(string deviceId);
}
