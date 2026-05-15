using System.Diagnostics;

namespace BTChargeTrayWatcher;

/// <summary>
/// Reads batteries from all available sources with protocol fallback.
/// GATT is attempted first for BLE devices (per-device via <see cref="GattConnectionManager"/>),
/// then Classic batch reader runs as fallback. Results are merged with GATT winning on conflicts.
/// Capability cache prevents repeated GATT attempts on devices known to lack the battery service.
/// </summary>
internal sealed class BatteryReaderOrchestrator
{
    private readonly GattConnectionManager _gattManager;
    private readonly IBatteryReader _classicReader;
    private readonly DeviceCapabilityCache _capabilityCache;

    internal BatteryReaderOrchestrator(
        GattConnectionManager gattManager,
        IBatteryReader classicReader,
        DeviceCapabilityCache capabilityCache)
    {
        _gattManager = gattManager;
        _classicReader = classicReader;
        _capabilityCache = capabilityCache;
    }

    /// <summary>
    /// Reads batteries for all watched BLE devices (GATT) and all Classic devices (batch),
    /// merging results with GATT taking priority.
    /// </summary>
    internal async Task<List<DeviceBatteryInfo>> ReadAllAsync(
        IReadOnlyList<WatchedDevice> watchedDevices,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Phase 1: GATT per-device reads (only for connected BLE devices that should be attempted)
        var gattTasks = new List<Task<GattReadOutcome>>();
        foreach (var dev in watchedDevices)
        {
            if (dev.IsBle && dev.IsConnected && _capabilityCache.ShouldAttempt(dev.DeviceId))
            {
                gattTasks.Add(SafeGattReadAsync(dev, ct));
            }
            else if (dev.IsBle && !dev.IsConnected)
            {
                Debug.WriteLine(
                    $"[BatteryReaderOrchestrator] Skipping '{dev.Name}' — not connected (sleeping)");
            }
        }

        // Phase 2: Classic batch read (runs in parallel with GATT reads)
        var classicTask = SafeClassicReadAsync(ct);

        // Wait for both phases
        await Task.WhenAll(
            Task.WhenAll(gattTasks),
            classicTask
        ).ConfigureAwait(false);

        // Collect GATT results and update capability cache
        var gattResults = new List<DeviceBatteryInfo>();
        foreach (var task in gattTasks)
        {
            var outcome = task.Result;
            if (outcome.Result is { Battery: not null } result)
            {
                gattResults.Add(result);
                _capabilityCache.RecordSuccess(outcome.DeviceId, BatterySource.Gatt);
            }
            else
            {
                _capabilityCache.RecordFailure(outcome.DeviceId);
            }
        }

        var classicResults = classicTask.Result;

        // Merge: GATT wins, dedup by device name (case-insensitive) since
        // GATT and Classic use different ID schemes.
        return MergeResults(gattResults, classicResults);
    }

    private async Task<GattReadOutcome> SafeGattReadAsync(WatchedDevice device, CancellationToken ct)
    {
        try
        {
            var result = await _gattManager
                .TryReadBatteryAsync(device.DeviceId, device.Name, ct)
                .ConfigureAwait(false);
            return new GattReadOutcome(device.DeviceId, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BatteryReaderOrchestrator] GATT read fault for '{device.Name}': {ex.Message}");
            return new GattReadOutcome(device.DeviceId, null);
        }
    }

    private async Task<List<DeviceBatteryInfo>> SafeClassicReadAsync(CancellationToken ct)
    {
        try
        {
            return await _classicReader.ReadAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[BatteryReaderOrchestrator] Classic reader fault: {ex}");
            return [];
        }
    }

    private static List<DeviceBatteryInfo> MergeResults(
        List<DeviceBatteryInfo> gattResults,
        List<DeviceBatteryInfo> classicResults)
    {
        var merged = new List<DeviceBatteryInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // GATT results first (higher priority)
        foreach (var device in gattResults)
        {
            seenIds.Add(device.DeviceId);
            seenNames.Add(device.Name);
            merged.Add(device);
        }

        // Classic results, skip duplicates by name or ID
        foreach (var device in classicResults)
        {
            if (seenIds.Contains(device.DeviceId)) continue;
            if (seenNames.Contains(device.Name)) continue;
            merged.Add(device with { Source = BatterySource.Classic });
        }

        return merged;
    }

    private sealed record GattReadOutcome(string DeviceId, DeviceBatteryInfo? Result);
}
