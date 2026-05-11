using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher.Monitoring.Gatt;

/// <summary>
/// Lightweight GATT connection helper that performs a single-device battery read using the
/// existing <see cref="GattBatteryProcessor"/>. This implementation is intentionally
/// short-lived and enforces a per-call timeout so it is safe to call from polling code.
/// </summary>
public sealed class GattConnectionManager
{
    public GattConnectionManager()
    {
    }

    public async Task<int?> TryReadBatteryAsync(string deviceId, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("deviceId must be provided", nameof(deviceId));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        var ct = timeoutCts.Token;

        try
        {
            var cache = new GattConnectionCache();
            var processor = new GattBatteryProcessor(cache);
            var result = await processor.ProcessDeviceAsync(deviceId, deviceId, ct).ConfigureAwait(false);
            return result.Battery;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // timed out
            return null;
        }
        catch (Exception ex) when (GattBatteryProcessor.IsExpectedBluetoothException(ex))
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}
