using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
        {
            try
            {
                // Enforce a strict 3-second limit per device to prevent OS connection stalls
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

                bool connected = await _connectionChecker.IsConnectedAsync(c.Address, timeoutCts.Token).ConfigureAwait(false);
                return (c, connected);
            }
            catch (OperationCanceledException)
            {
                return (c, connected: false);
            }
            catch
            {
                return (c, connected: false);
            }
        });

        var connectedResults = await Task.WhenAll(connectTasks).ConfigureAwait(false);

        var connected = connectedResults
            .Where(r => r.connected)
            .Select(r => r.c)
            .ToList();

        if (connected.Count == 0)
            return new();

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
