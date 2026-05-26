namespace BTChargeTrayWatcher;

public interface IClassicBluetoothConnectionChecker
{
    Task<bool> IsConnectedAsync(ulong bluetoothAddress, CancellationToken cancellationToken);
}
