namespace BTChargeTrayWatcher;

/// <summary>
/// Why a GATT Battery Level subscription ended. Every value is logged through
/// <see cref="Monitoring.Logging.DiscoveryLogger"/> (ADR-018, code 1012) so the #158 field
/// measurement can answer "why did each subscription end?" after the fact.
/// </summary>
internal enum GattSubscriptionDropReason
{
    /// <summary>No notification arrived within <see cref="GattSubscriptionDefaults.SettlingWindow"/>.
    /// The device will not be subscribed again in this session.</summary>
    SettlingTimeout = 0,

    /// <summary>The peripheral disconnected (its <c>ConnectionStatus</c> left <c>Connected</c>).</summary>
    Disconnected = 1,

    /// <summary>The machine is going to sleep (<c>PowerModes.Suspend</c>).</summary>
    Suspend = 2,

    /// <summary>The device was evicted from the known-device cache after repeated misses.</summary>
    Evicted = 3,

    /// <summary>The owning manager is shutting down.</summary>
    Disposed = 4,
}
