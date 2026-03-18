using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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

        IReadOnlyList<ClassicBluetoothCandidate> candidates;
        try
        {
            candidates = _deviceEnumerator.EnumerateCandidates();
        }
        catch (Exception ex) when (IsExpectedBluetoothException(ex))
        {
            System.Diagnostics.Debug.WriteLine($"[ClassicBatteryReader] Radio unavailable: {ex.Message}");
            return [];
        }

        if (candidates.Count == 0)
            return new();

        cancellationToken.ThrowIfCancellationRequested();

        var connectTasks = candidates.Select(async c =>
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

                bool connected = await _connectionChecker.IsConnectedAsync(c.Address, timeoutCts.Token).ConfigureAwait(false);
                return (c, connected);
            }
            catch (OperationCanceledException)
            {
                return (c, connected: false);
            }
            catch (Exception ex) when (IsExpectedBluetoothException(ex))
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
            try
            {
                int batteryVal = _batteryPropertyReader.ReadBatteryProperty(candidate.InstanceId);
                return (candidate.Name, Battery: batteryVal);
            }
            catch (Exception ex) when (IsExpectedBluetoothException(ex))
            {
                return (candidate.Name, Battery: -1);
            }
        }, cancellationToken));

        var readings = await Task.WhenAll(batteryTasks).ConfigureAwait(false);

        return readings
            .Where(r => r.Battery >= 0)
            .Select(r => (r.Name, r.Battery))
            .ToList();
    }

    private static bool IsExpectedBluetoothException(Exception ex)
    {
        return ex is COMException ||
               ex is UnauthorizedAccessException ||
               ex is InvalidOperationException;
    }
}
