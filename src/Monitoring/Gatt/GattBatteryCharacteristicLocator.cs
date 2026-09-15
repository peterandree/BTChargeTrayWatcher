using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BTChargeTrayWatcher;

/// <summary>
/// Locates the Battery Level characteristic (0x2A19) on an open <see cref="BluetoothLEDevice"/>.
/// Shared by the polling read path (<see cref="GattConnectionManager"/>) and the notification
/// subscription path (<see cref="WinRtGattNotificationSubscription"/>) so the "classic Battery
/// Service first, then Common Battery Service" rule cannot drift between the two (issue #153).
/// </summary>
internal static class GattBatteryCharacteristicLocator
{
    /// <summary>Battery Service (0x180F).</summary>
    internal static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");

    /// <summary>Common Battery Service (0x182B) — used by many newer Windows 11-era devices.</summary>
    internal static readonly Guid CommonBatteryServiceUuid = new("0000182b-0000-1000-8000-00805f9b34fb");

    /// <summary>Battery Level characteristic (0x2A19).</summary>
    internal static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    private static readonly TimeSpan WinRtTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns the 0x2A19 characteristic from the classic Battery Service (0x180F) if present,
    /// otherwise from the Common Battery Service (0x182B), or <c>null</c> when neither exposes it.
    /// </summary>
    internal static async Task<GattCharacteristic?> FindBatteryLevelAsync(
        BluetoothLEDevice device, CancellationToken ct)
    {
        Guid[] serviceUuids = [BatteryServiceUuid, CommonBatteryServiceUuid];

        foreach (var svcUuid in serviceUuids)
        {
            var servicesResult = await device
                .GetGattServicesForUuidAsync(svcUuid, BluetoothCacheMode.Cached)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (servicesResult.Status != GattCommunicationStatus.Success ||
                servicesResult.Services.Count == 0)
                continue;

            var charsResult = await servicesResult.Services[0]
                .GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Cached)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (charsResult.Status == GattCommunicationStatus.Success &&
                charsResult.Characteristics.Count > 0)
                return charsResult.Characteristics[0];
        }

        return null;
    }
}
