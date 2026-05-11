using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BTChargeTrayWatcher.Core;

/// <summary>
/// Provides short-lived, per-call GATT connection and battery read (no object caching).
/// </summary>
public sealed class GattConnectionManager : IDisposable
{
    private bool _disposed;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BatteryReadTimeout = TimeSpan.FromSeconds(3);

    public async Task<int?> ReadBatteryLevelAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GattConnectionManager));
        cancellationToken.ThrowIfCancellationRequested();

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(ConnectTimeout);

        BluetoothLEDevice? device = null;
        try
        {
            device = await BluetoothLEDevice.FromIdAsync(deviceId).AsTask(connectCts.Token).ConfigureAwait(false);
            if (device == null) return null;

            var servicesResult = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
                .AsTask(connectCts.Token).ConfigureAwait(false);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                return null;

            var batteryService = servicesResult.Services[0];
            var charsResult = await batteryService.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                .AsTask(connectCts.Token).ConfigureAwait(false);
            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
                return null;

            var batteryChar = charsResult.Characteristics[0];
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(BatteryReadTimeout);
            var valueResult = await batteryChar.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(readCts.Token).ConfigureAwait(false);
            if (valueResult.Status != GattCommunicationStatus.Success)
                return null;

            var reader = Windows.Storage.Streams.DataReader.FromBuffer(valueResult.Value);
            if (reader.UnconsumedBufferLength < 1) return null;
            return reader.ReadByte();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (IsExpectedBluetoothException(ex))
        {
            System.Diagnostics.Debug.WriteLine($"[GattConnectionManager] Bluetooth error: {ex.Message}");
            return null;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static bool IsExpectedBluetoothException(Exception ex)
    {
        return ex is UnauthorizedAccessException ||
               ex is InvalidOperationException ||
               ex is System.Runtime.InteropServices.COMException;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
