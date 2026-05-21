using BTChargeTrayWatcher.Monitoring.Logging;
using BTChargeTrayWatcher.Utilities;

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
/// the legacy pipeline (deleted in issue #100).
/// </para>
/// </remarks>
internal sealed class BatteryReaderOrchestrator
{
    private const string ReaderName = "BatteryReaderOrchestrator";

    /// <summary>Jaro-Winkler threshold above which a fuzzy match becomes an alias suggestion (ADR-015 Stage 4).</summary>
    private const double FuzzyThreshold = 0.92;

    /// <summary>
    /// Device categories that are allowed through the filter by default (ADR-016).
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

    /// <summary>
    /// Raised when Stage 4 (Jaro-Winkler fuzzy match) finds a high-confidence alias
    /// candidate that is not yet confirmed by the user. The UI should present the
    /// suggestion for confirmation before calling <see cref="ThresholdSettings.AddAlias"/>.
    /// The device is NOT merged into results until the user confirms.
    /// </summary>
    internal event Action<AliasSuggestion>? AliasSuggested;

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

        var classicTask = SafeClassicReadAsync(ct);

        await Task.WhenAll(
            Task.WhenAll(gattTasks),
            classicTask
        ).ConfigureAwait(false);

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

        return MergeResults(gattResults, classicResults);
    }

    // ── ADR-015: alias resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to resolve a canonical DeviceId for <paramref name="device"/> via the
    /// 4-stage ADR-015 alias pipeline.
    /// Returns <c>null</c> when Stage 4 fires a fuzzy suggestion (device must not be merged).
    /// Returns the (possibly remapped) DeviceId otherwise.
    /// </summary>
    private string? ResolveCanonicalId(DeviceBatteryInfo device)
    {
        if (_settings is null) return device.DeviceId;

        var aliasMap = _settings.AliasMap;
        if (aliasMap.Count == 0) return device.DeviceId;

        // Stage 2: exact name lookup (OrdinalIgnoreCase via dictionary comparer)
        if (aliasMap.TryGetValue(device.Name, out var exactCanonical))
            return exactCanonical;

        // Stage 3: normalised name lookup
        var normalizedDeviceName = DeviceNameNormalizer.Normalize(device.Name);
        foreach (var kv in aliasMap)
        {
            if (DeviceNameNormalizer.Normalize(kv.Key) == normalizedDeviceName)
                return kv.Value;
        }

        // Stage 4: Jaro-Winkler fuzzy match
        string? bestKey = null;
        string? bestCanonical = null;
        double bestScore = 0.0;
        foreach (var kv in aliasMap)
        {
            double score = JaroWinkler.Similarity(
                normalizedDeviceName,
                DeviceNameNormalizer.Normalize(kv.Key));
            if (score > bestScore)
            {
                bestScore = score;
                bestKey = kv.Key;
                bestCanonical = kv.Value;
            }
        }

        // If the user has suppressed alias suggestions for this device, treat as no alias.
        if (_settings.IsAliasSuggestionSuppressed(device.DeviceId))
            return device.DeviceId;

        if (bestScore >= FuzzyThreshold && bestKey is not null && bestCanonical is not null)
        {
            AliasSuggested?.Invoke(new AliasSuggestion(
                DeviceId:         device.DeviceId,
                DeviceName:       device.Name,
                MatchedAliasKey:  bestKey,
                CanonicalDeviceId: bestCanonical,
                Score:            bestScore));
            // Do NOT merge — return null to signal skip
            return null;
        }

        // No alias match — use the device's own ID
        return device.DeviceId;
    }

    // ── ADR-016: category filter ─────────────────────────────────────────────────────

    private bool IsAllowedByFilter(DeviceBatteryInfo device)
    {
        if (_settings is null) return true;
        if (!_settings.CategoryFilterEnabled) return true;
        if (_settings.IsCategoryFilterOverridden(device.DeviceId)) return true;
        if (device.Category == DeviceCategory.Unknown) return true;
        return AllowedCategories.Contains(device.Category);
    }

    // ── Merge ────────────────────────────────────────────────────────────────────────

    private List<DeviceBatteryInfo> MergeResults(
        List<DeviceBatteryInfo> gattResults,
        List<DeviceBatteryInfo> classicResults)
    {
        var merged = new List<DeviceBatteryInfo>();
        // seenIds tracks canonical IDs after alias resolution
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // GATT first (higher priority)
        foreach (var device in gattResults)
        {
            if (!IsAllowedByFilter(device)) continue;

            // Stage 1: DeviceId dedup
            if (seenIds.Contains(device.DeviceId)) continue;

            // Stages 2-4: alias resolution
            var canonicalId = ResolveCanonicalId(device);
            if (canonicalId is null) continue;          // Stage 4 fuzzy suggestion — skip
            if (seenIds.Contains(canonicalId)) continue; // already have this physical device

            seenIds.Add(canonicalId);
            seenNames.Add(device.Name);

            // Emit with canonical ID if remapped
            merged.Add(canonicalId == device.DeviceId
                ? device
                : device with { DeviceId = canonicalId });
        }

        // Classic fallback
        foreach (var device in classicResults)
        {
            if (!IsAllowedByFilter(device)) continue;
            if (seenIds.Contains(device.DeviceId)) continue;
            if (seenNames.Contains(device.Name)) continue;

            var canonicalId = ResolveCanonicalId(device);
            if (canonicalId is null) continue;
            if (seenIds.Contains(canonicalId)) continue;

            seenIds.Add(canonicalId);
            seenNames.Add(device.Name);

            merged.Add((canonicalId == device.DeviceId
                ? device
                : device with { DeviceId = canonicalId })
                with { Source = BatterySource.Classic });
        }

        return merged;
    }

    // ── GATT / Classic helpers ───────────────────────────────────────────────────────

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

    private sealed record GattReadOutcome(string DeviceId, DeviceBatteryInfo? Result);
}
