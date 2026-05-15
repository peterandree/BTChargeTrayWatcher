using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BTChargeTrayWatcher;

/// <summary>
/// Long-lived service that reads GATT Battery Level (0x2A19) from BLE devices.
/// Caches <em>knowledge</em> (which device IDs support the battery service), not
/// WinRT objects. All WinRT references are dropped immediately after each read
/// so peripherals can enter low-power sleep states.
/// </summary>
internal sealed class GattConnectionManager : IDisposable
{
    private static readonly Guid BatterySvcUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");
    private static readonly TimeSpan WinRtTimeout = TimeSpan.FromSeconds(2);

    private readonly HashSet<string> _knownGattDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly Lock _lock = new();

    internal GattConnectionManager(int maxConcurrency)
    {
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    internal GattConnectionManager()
        : this(PollingDefaults.GattMaxConcurrentReads) { }

    /// <summary>
    /// Reads the battery level of a single BLE device via GATT 0x2A19.
    /// Returns <c>null</c> if the device doesn't expose the battery service or the read fails.
    /// All WinRT references are dropped before returning.
    /// </summary>
    internal async Task<DeviceBatteryInfo?> TryReadBatteryAsync(
        string deviceId, string fallbackName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadBatteryCorAsync(deviceId, fallbackName, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DeviceBatteryInfo?> ReadBatteryCorAsync(
        string deviceId, string fallbackName, CancellationToken ct)
    {
        try
        {
            var bleDevice = await BluetoothLEDevice.FromIdAsync(deviceId)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (bleDevice is null)
                return null;

            string name = !string.IsNullOrWhiteSpace(bleDevice.Name) ? bleDevice.Name : fallbackName;

            if (bleDevice.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return null;

            var servicesResult = await bleDevice
                .GetGattServicesForUuidAsync(BatterySvcUuid, BluetoothCacheMode.Cached)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (servicesResult.Status != GattCommunicationStatus.Success ||
                servicesResult.Services.Count == 0)
                return null;

            var service = servicesResult.Services[0];
            var charsResult = await service
                .GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Cached)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (charsResult.Status != GattCommunicationStatus.Success ||
                charsResult.Characteristics.Count == 0)
                return null;

            var readResult = await charsResult.Characteristics[0]
                .ReadValueAsync(BluetoothCacheMode.Uncached)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (readResult.Status != GattCommunicationStatus.Success ||
                readResult.Value.Length == 0)
                return null;

            using var reader = DataReader.FromBuffer(readResult.Value);
            byte value = reader.ReadByte();
            if (value > 100) return null;

            // Cache knowledge — this device supports GATT battery.
            lock (_lock) { _knownGattDevices.Add(deviceId); }

            return new DeviceBatteryInfo(deviceId, name, value, IsCharging: null, Source: BatterySource.Gatt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Debug.WriteLine($"[GattConnectionManager] Timeout reading '{deviceId}'");
            return null;
        }
        catch (Exception ex) when (
            ex is COMException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            Debug.WriteLine($"[GattConnectionManager] Device unavailable '{deviceId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Returns <c>true</c> if <paramref name="deviceId"/> was previously read successfully.</summary>
    internal bool IsKnownGattDevice(string deviceId)
    {
        lock (_lock) { return _knownGattDevices.Contains(deviceId); }
    }

    /// <summary>Clears all cached knowledge (e.g. on sleep/resume).</summary>
    internal void InvalidateAll()
    {
        lock (_lock) { _knownGattDevices.Clear(); }
    }

    public void Dispose() => _gate.Dispose();
}
