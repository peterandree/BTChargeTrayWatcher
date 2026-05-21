using BTChargeTrayWatcher.Monitoring.Logging;

namespace BTChargeTrayWatcher;

/// <summary>
/// Reads batteries from all available sources with protocol fallback.
/// GATT is attempted first for BLE devices (per-device via <see cref="GattConnectionManager"/>),
/// then Classic batch reader runs as fallback. Results are merged with GATT winning on conflicts.
/// Capability cache prevents repeated GATT attempts on devices known to lack the battery service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Execution path: cooperation-stack (production) path only.</b>
/// This class is the sole production-path aggregator. It is used exclusively by
/// <see cref="OrchestratorBatteryReaderAdapter"/>, which is injected into <see cref="Scanner"/>
/// via the 6-argument internal <see cref="BluetoothBatteryMonitor"/> constructor wired in
/// <c>Program.cs</c>.
/// </para>
/// <para>
/// All ADR-015 (alias resolution), ADR-016 (device class filtering), and ADR-018
/// (discovery logging) implementations that affect aggregation live here, not in
/// <see cref="DeviceAggregationPipeline"/> (which is legacy-path only and not reached
/// by <c>Program.cs</c>).
/// </para>
/// </remarks>
internal sealed class BatteryReaderOrchestrator
{
    private const string ReaderName = "BatteryReaderOrchestrator";

    /// <summary>
    /// Device categories that are allowed through the filter by default (ADR-016).
    /// Devices whose <see cref="DeviceBatteryInfo.Category"/> is not in this set
    /// and is not <see cref="DeviceCategory.Unknown"/> will be silently excluded
    /// from merge results unless the device ID appears in
    /// <see cref="ThresholdSettings.CategoryFilterOverrides"/>.
    /// </summary>
    private static readonly IReadOnlySet<DeviceCategory> AllowedCategories =
        new HashSet<DeviceCategory>
        {
            DeviceCategory.Audio,
            DeviceCategory.Hid,
            DeviceCategory.Controller,
        };

    private readonly GattConnectionManager _gattManager;
    private readonly IBatteryReader _classicReader;
    private readonly DeviceCapabilityCache _capabilityCache;
    private readonly ThresholdSettings? _settings;

    internal BatteryReaderOrchestrator(
        GattConnectionManager gattManager,
        IBatteryReader classicReader,
        DeviceCapabilityCache capabilityCache,
        ThresholdSettings? settings = null)
    {
        _gattManager = gattManager;
        _classicReader = classicReader;
        _capabilityCache = capabilityCache;
        _settings = settings;
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
                DiscoveryLogger.Log(
                    reader:     ReaderName,
                    operation:  "SkipSleeping",
                    outcome:    "WARN",
                    errorCode:  DiscoveryLogger.Codes.GattDisconnected,
                    message:    $"Skipping '{dev.Name}' — not connected (sleeping)",
                    deviceId:   dev.DeviceId,
                    deviceName: dev.Name);
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

    /// <summary>
    /// Returns <c>true</c> when the device should be included in merge results.
    /// Filter logic (ADR-016):
    /// <list type="bullet">
    ///   <item>Filter disabled globally → always pass.</item>
    ///   <item>Device ID in <see cref="ThresholdSettings.CategoryFilterOverrides"/> → always pass.</item>
    ///   <item><see cref="DeviceCategory.Unknown"/> → pass (reader did not classify; no penalisation).</item>
    ///   <item>Category in <see cref="AllowedCategories"/> → pass.</item>
    ///   <item>All other cases → exclude silently.</item>
    /// </list>
    /// </summary>
    private bool IsAllowedByFilter(DeviceBatteryInfo device)
    {
        if (_settings is null) return true;
        if (!_settings.CategoryFilterEnabled) return true;
        if (_settings.IsCategoryFilterOverridden(device.DeviceId)) return true;
        if (device.Category == DeviceCategory.Unknown) return true;
        return AllowedCategories.Contains(device.Category);
    }

    private async Task<GattReadOutcome> SafeGattReadAsync(WatchedDevice device, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
            sw.Stop();
            DiscoveryLogger.Log(
                reader:     ReaderName,
                operation:  "ReadBattery",
                outcome:    "ERROR",
                errorCode:  DiscoveryLogger.Codes.GattTimeout,
                message:    ex.Message,
                deviceId:   device.DeviceId,
                deviceName: device.Name,
                durationMs: (int)sw.ElapsedMilliseconds);
            return new GattReadOutcome(device.DeviceId, null);
        }
    }

    private async Task<List<DeviceBatteryInfo>> SafeClassicReadAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
            sw.Stop();
            DiscoveryLogger.Log(
                reader:     ReaderName,
                operation:  "ReadBattery",
                outcome:    "ERROR",
                errorCode:  DiscoveryLogger.Codes.ClassicSetupApiFault,
                message:    ex.ToString(),
                durationMs: (int)sw.ElapsedMilliseconds);
            return [];
        }
    }

    private List<DeviceBatteryInfo> MergeResults(
        List<DeviceBatteryInfo> gattResults,
        List<DeviceBatteryInfo> classicResults)
    {
        var merged = new List<DeviceBatteryInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // GATT results first (higher priority)
        foreach (var device in gattResults)
        {
            if (!IsAllowedByFilter(device)) continue;
            seenIds.Add(device.DeviceId);
            seenNames.Add(device.Name);
            merged.Add(device);
        }

        // Classic results, skip duplicates by name or ID
        foreach (var device in classicResults)
        {
            if (!IsAllowedByFilter(device)) continue;
            if (seenIds.Contains(device.DeviceId)) continue;
            if (seenNames.Contains(device.Name)) continue;
            merged.Add(device with { Source = BatterySource.Classic });
        }

        return merged;
    }

    private sealed record GattReadOutcome(string DeviceId, DeviceBatteryInfo? Result);
}
