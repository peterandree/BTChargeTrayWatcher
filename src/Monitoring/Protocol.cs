namespace BTChargeTrayWatcher;

/// <summary>Source protocol that provided the battery reading.</summary>
public enum BatterySource
{
    Unknown = 0,
    Gatt = 1,
    Classic = 2
}

/// <summary>Bluetooth transport type.</summary>
internal enum DeviceTransport
{
    Unknown = 0,
    Ble = 1,
    Classic = 2,
    DualMode = 3
}

/// <summary>Broad device category for protocol fallback selection.</summary>
public enum DeviceCategory
{
    Unknown = 0,
    Audio = 1,
    Hid = 2,
    Controller = 3
}
