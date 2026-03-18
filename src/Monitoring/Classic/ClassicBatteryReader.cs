namespace BTChargeTrayWatcher;

public class ClassicBatteryReader
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
        {
            return new();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var connectTasks = candidates.Select(async c =>
            (c, connected: await _connectionChecker.IsConnectedAsync(c.Address, cancellationToken).ConfigureAwait(false)));

        var connected = (await Task.WhenAll(connectTasks).ConfigureAwait(false))
            .Where(r => r.connected)
            .Select(r => r.c)
            .ToList();

        if (connected.Count == 0)
        {
            return new();
        }

        var results = new List<(string, int)>();
        foreach (var candidate in connected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int battery = _batteryPropertyReader.ReadBatteryProperty(candidate.InstanceId);
            if (battery >= 0)
            {
                results.Add((candidate.Name, battery));
            }
        }

        return results;
    }
}
