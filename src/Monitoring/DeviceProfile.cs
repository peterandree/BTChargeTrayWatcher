namespace BTChargeTrayWatcher;

/// <summary>
/// Small value type holding device transport and category classification.
/// Replaces tuple return values like `(DeviceTransport, DeviceCategory)`.
/// </summary>
internal readonly record struct DeviceProfile(DeviceTransport Transport, DeviceCategory Category);
