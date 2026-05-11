using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;

namespace BTChargeTrayWatcher.Monitoring.Gatt;

/// <summary>
/// Lightweight GATT connection helper that performs a single-device battery read using the
/// existing <see cref="GattBatteryProcessor"/>. This implementation is intentionally
/// short-lived and enforces a per-call timeout so it is safe to call from polling code.
/// </summary>
public sealed class GattConnectionManager
{
    private static readonly SemaphoreSlim s_gattSemaphore = new(PollingDefaults.GattMaxConcurrentReads);

    // Optional test override for the read operation. When non-null the manager will call
    // this delegate instead of creating a real GATT processor. This enables deterministic
    // integration-style tests without requiring hardware.
    private readonly Func<string, int, CancellationToken, Task<int?>>? _testReadOverride;

    public GattConnectionManager() : this(null)
    {
    }

    internal GattConnectionManager(Func<string, int, CancellationToken, Task<int?>>? testReadOverride)
    {
        _testReadOverride = testReadOverride;
    }

    public async Task<int?> TryReadBatteryAsync(string deviceId, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("deviceId must be provided", nameof(deviceId));

        // honor caller cancellation when waiting for the concurrency slot
        await s_gattSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationCts.CancelAfter(timeoutMs);
            var ct = operationCts.Token;

            try
            {
                if (_testReadOverride is not null)
                {
                    return await _testReadOverride(deviceId, timeoutMs, ct).ConfigureAwait(false);
                }

                using var cache = new GattConnectionCache();
                var processor = new GattBatteryProcessor(cache);

                var result = await processor.ProcessDeviceAsync(deviceId, deviceId, ct).ConfigureAwait(false);
                return result.Battery;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // operationCts timed out — treat as transient no-result
                return null;
            }
            catch (OperationCanceledException)
            {
                // caller requested cancellation - propagate
                throw;
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
        finally
        {
            s_gattSemaphore.Release();
        }
    }
}
