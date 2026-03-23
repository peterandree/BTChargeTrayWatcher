using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

public sealed class ClassicBatteryReader : IBatteryReader
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(3);

    private readonly ClassicBluetoothDeviceEnumerator _deviceEnumerator = new();
    private readonly ClassicBluetoothConnectionChecker _connectionChecker = new();
    private readonly ClassicBatteryPropertyReader _batteryPropertyReader = new();

    public Task<List<DeviceBatteryInfo>> ReadAllAsync() =>
        ReadAllAsync(CancellationToken.None);

    public async Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken)
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

        Task<(ClassicBluetoothCandidate Candidate, bool Connected)>[] connectionTasks = candidates
            .Select(candidate => CheckConnectedAsync(candidate, cancellationToken))
            .ToArray();

        (ClassicBluetoothCandidate Candidate, bool Connected)[] connectionResults =
            await Task.WhenAll(connectionTasks).ConfigureAwait(false);

        List<ClassicBluetoothCandidate> connected = connectionResults
            .Where(r => r.Connected)
            .Select(r => r.Candidate)
            .ToList();

        if (connected.Count == 0)
            return new();

        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<string, int> batteryMap = await Task.Run(() =>
        {
            var instanceIds = connected.Select(c => c.InstanceId);
            return _batteryPropertyReader.ReadBatteryProperties(instanceIds);
        }, cancellationToken).ConfigureAwait(false);

        return connected
            .Select(c => new DeviceBatteryInfo(
                c.Name,
                batteryMap.TryGetValue(c.InstanceId, out int b) ? b : -1))
            .Where(d => !string.IsNullOrWhiteSpace(d.Name) && d.Battery >= 0 && d.Battery <= 100)
            .ToList();
    }

    private async Task<(ClassicBluetoothCandidate Candidate, bool Connected)> CheckConnectedAsync(
        ClassicBluetoothCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectionTimeout);

            bool connected = await _connectionChecker
                .IsConnectedAsync(candidate.Address, timeoutCts.Token)
                .ConfigureAwait(false);

            return (candidate, connected);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (candidate, false);
        }
        catch (Exception ex) when (IsExpectedBluetoothException(ex))
        {
            System.Diagnostics.Debug.WriteLine($"[ClassicBatteryReader] Connection check failed for '{candidate.Name}': {ex.Message}");
            return (candidate, false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClassicBatteryReader] Unexpected connection check fault for '{candidate.Name}': {ex}");
            return (candidate, false);
        }
    }

    private static bool IsExpectedBluetoothException(Exception ex)
    {
        return ex is COMException ||
               ex is UnauthorizedAccessException ||
               ex is InvalidOperationException;
    }
}
