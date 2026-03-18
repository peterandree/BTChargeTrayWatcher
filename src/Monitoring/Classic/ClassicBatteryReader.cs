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
    private const int MaxConcurrentBatteryReads = 2;

    private readonly ClassicBluetoothDeviceEnumerator _deviceEnumerator = new();
    private readonly ClassicBluetoothConnectionChecker _connectionChecker = new();
    private readonly ClassicBatteryPropertyReader _batteryPropertyReader = new();
    private readonly SemaphoreSlim _batteryReadGate = new(MaxConcurrentBatteryReads, MaxConcurrentBatteryReads);

    public Task<List<(string Name, int Battery)>> ReadAllAsync() =>
        ReadAllAsync(CancellationToken.None);

    public async Task<List<(string Name, int Battery)>> ReadAllAsync(CancellationToken cancellationToken)
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

        Task<(string Name, int Battery)>[] batteryTasks = connected
            .Select(candidate => ReadBatteryAsync(candidate, cancellationToken))
            .ToArray();

        (string Name, int Battery)[] readings = await Task.WhenAll(batteryTasks).ConfigureAwait(false);

        return readings
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && r.Battery >= 0)
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
            System.Diagnostics.Debug.WriteLine(
                $"[ClassicBatteryReader] Connection check failed for '{candidate.Name}': {ex.Message}");
            return (candidate, false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ClassicBatteryReader] Unexpected connection check fault for '{candidate.Name}': {ex}");
            return (candidate, false);
        }
    }

    private async Task<(string Name, int Battery)> ReadBatteryAsync(
        ClassicBluetoothCandidate candidate,
        CancellationToken cancellationToken)
    {
        await _batteryReadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            int battery = _batteryPropertyReader.ReadBatteryProperty(candidate.InstanceId);
            return (candidate.Name, battery);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedBluetoothException(ex))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ClassicBatteryReader] Battery read failed for '{candidate.Name}': {ex.Message}");
            return (candidate.Name, -1);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ClassicBatteryReader] Unexpected battery read fault for '{candidate.Name}': {ex}");
            return (candidate.Name, -1);
        }
        finally
        {
            _batteryReadGate.Release();
        }
    }

    private static bool IsExpectedBluetoothException(Exception ex)
    {
        return ex is COMException ||
               ex is UnauthorizedAccessException ||
               ex is InvalidOperationException;
    }
}
