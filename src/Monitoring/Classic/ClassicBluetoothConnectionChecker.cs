using System.Diagnostics;
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
            Debug.WriteLine(ex);
            return false;
        }
    }
}
