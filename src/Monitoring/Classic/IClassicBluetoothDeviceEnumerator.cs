namespace BTChargeTrayWatcher;

public interface IClassicBluetoothDeviceEnumerator
{
    List<ClassicBluetoothCandidate> EnumerateCandidates();
}
