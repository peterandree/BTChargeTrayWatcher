using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;

namespace BTChargeTrayWatcher;

internal sealed class GattConnectionCache : IDisposable
{
    private readonly ConcurrentDictionary<string, BluetoothLEDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedGattEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public BluetoothLEDevice? GetDevice(string deviceId) =>
        _devices.TryGetValue(deviceId, out var d) ? d : null;

    public void SetDevice(string deviceId, BluetoothLEDevice device) =>
        _devices[deviceId] = device;

    /// <summary>
    /// Removes a device from the cache and disposes the associated WinRT object.
    /// Called when a cached device is found to be in a disconnected state so that
    /// the next call to <see cref="GetOrCreateDeviceAsync"/> fetches a fresh instance.
    /// </summary>
    public void RemoveDevice(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var device))
        {
            // Also evict any endpoint associated with this device — it is stale by definition.
            RemoveEndpoint(deviceId);
            if (device is IDisposable d)
                try { d.Dispose(); } catch { }
        }
    }

    public CachedGattEndpoint? GetEndpoint(string deviceId) =>
        _endpoints.TryGetValue(deviceId, out var e) ? e : null;

    public void SetEndpoint(string deviceId, CachedGattEndpoint endpoint)
    {
        CachedGattEndpoint? toDispose = null;
        _endpoints.AddOrUpdate(
            deviceId,
            endpoint,
            (_, old) => { toDispose = old; return endpoint; });
        toDispose?.Dispose();
    }

    public void RemoveEndpoint(string deviceId)
    {
        if (_endpoints.TryRemove(deviceId, out var endpoint))
            endpoint.Dispose();
    }

    public void PruneStaleDevices(HashSet<string> activeDeviceIds)
    {
        foreach (var deviceId in _devices.Keys)
        {
            if (!activeDeviceIds.Contains(deviceId))
            {
                if (_devices.TryRemove(deviceId, out var device) && device is IDisposable d)
                    d.Dispose();
            }
        }

        foreach (var deviceId in _endpoints.Keys)
        {
            if (!activeDeviceIds.Contains(deviceId))
                RemoveEndpoint(deviceId);
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _devices)
        {
            if (kvp.Value is IDisposable d)
                try { d.Dispose(); } catch { }
        }

        foreach (var kvp in _endpoints)
            kvp.Value.Dispose();

        _devices.Clear();
        _endpoints.Clear();
    }
}
