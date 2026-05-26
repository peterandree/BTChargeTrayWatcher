namespace BTChargeTrayWatcher;

/// <summary>
/// Small value type representing a device identifier + display name.
/// Use instead of `(string Id, string Name)` tuples across API boundaries.
/// </summary>
internal readonly record struct DeviceIdentifier(string Id, string Name);
