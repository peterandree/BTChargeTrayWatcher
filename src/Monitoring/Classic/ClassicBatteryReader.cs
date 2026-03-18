namespace BTChargeTrayWatcher;

public class ClassicBatteryReader : IBatteryReader
{
    private readonly ClassicBluetoothDeviceEnumerator _deviceEnumerator = new();
    private readonly ClassicBluetoothConnectionChecker _connectionChecker = new();
    private readonly ClassicBatteryPropertyReader _batteryPropertyReader = new();

    public Task<List<(string Name, int Battery)>> ReadAllAsync() =>
        ReadAllAsync(CancellationToken.None);

    public Task<List<(string Name, int Battery)>> ReadAllAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadAllInternalAsync(cancellationToken), cancellationToken);

    private async Task<List<(string Name, int Battery)>> ReadAllInternalAsync(
       CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = _deviceEnumerator.EnumerateCandidates();
        if (candidates.Count == 0)
            return new();

        cancellationToken.ThrowIfCancellationRequested();

        var connectTasks = candidates.Select(async c =>
            (c, connected: await _connectionChecker.IsConnectedAsync(c.Address, cancellationToken).ConfigureAwait(false)));

        var connected = (await Task.WhenAll(connectTasks).ConfigureAwait(false))
            .Where(r => r.connected)
            .Select(r => r.c)
            .ToList();

        if (connected.Count == 0)
            return new();

        // Parallelise the synchronous SetupAPI battery reads across connected devices.
        var batteryTasks = connected.Select(candidate => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int battery = _batteryPropertyReader.ReadBatteryProperty(candidate.InstanceId);
            return (candidate.Name, battery);
        }, cancellationToken));

        var readings = await Task.WhenAll(batteryTasks).ConfigureAwait(false);

        return readings
            .Where(r => r.battery >= 0)
            .Select(r => (r.Name, r.battery))
            .ToList();
    }
}
