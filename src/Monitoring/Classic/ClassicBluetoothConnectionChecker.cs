using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;

namespace BTChargeTrayWatcher;

internal sealed class ClassicBluetoothConnectionChecker
{
    public async Task<bool> IsConnectedAsync(
        ulong bluetoothAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using BluetoothDevice? device =
                await BluetoothDevice.FromBluetoothAddressAsync(bluetoothAddress)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

            return device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClassicBluetoothConnectionChecker] Connection check fault: {ex.Message}");
            return false;
        }
    }
}
