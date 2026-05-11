using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher.Monitoring.Gatt;

/// <summary>
/// Minimal GATT connection manager stub. Real implementation must avoid caching BluetoothLEDevice objects
/// and must enforce hard timeouts on WinRT calls. This stub provides a compile-time placeholder.
/// </summary>
public sealed class GattConnectionManager
{
    public GattConnectionManager()
    {
    }

    public Task<int?> TryReadBatteryAsync(string deviceId, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        // Real implementation: WinRT calls with TaskExtensions.WaitAsync timeouts and no object caching.
        return Task.FromResult<int?>(null);
    }
}
