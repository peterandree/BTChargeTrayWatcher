using BTChargeTrayWatcher.Monitoring.Logging;
using BTChargeTrayWatcher.Utilities;

namespace BTChargeTrayWatcher;

/// <summary>
/// Reads batteries from all available sources with protocol fallback.
/// GATT is attempted first for BLE devices (per-device via <see cref="GattConnectionManager"/>),
/// then the classic reader delegate runs as fallback.
/// Results are merged with GATT winning on conflicts.
/// Capability cache prevents repeated GATT attempts on devices known to lack the battery service.
/// </summary>
internal sealed class BatteryReaderOrchestrator
{
    private const string ReaderName = "BatteryReaderOrchestrator";

    private const double FuzzyThreshold = 0.92;

    private static readonly HashSet<DeviceCategory> AllowedCategories =
        [
            DeviceCategory.Audio,
            DeviceCategory.Hid,
            DeviceCategory.Controller,
        ];

    private readonly GattConnectionManager _gattManager;
    private readonly Func<CancellationToken, Task<List<DeviceBatteryInfo>>> _readClassic;
    private readonly DeviceCapabilityCache _capabilityCache;
    private readonly ThresholdSettings? _settings;
    private readonly DeviceProfileClassifier _classifier = new();

    internal event Action<AliasSuggestion>? AliasSuggested;

    internal BatteryReaderOrchestrator(
        GattConnectionManager gattManager,
        Func<CancellationToken, Task<List<DeviceBatteryInfo>>> readClassic,
        DeviceCapabilityCache capabilityCache,
        ThresholdSettings? settings = null)
    {
        _gattManager    = gattManager;
        _readClassic    = readClassic;
        _capabilityCache = capabilityCache;
        _settings       = settings;
    }

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

        // ADR-016: stamp DeviceCategory from CoD before filtering.
        StampCategories(gattResults, watchedDevices, isGatt: true);
        StampCategories(classicTask.Result, watchedDevices, isGatt: false);

        return MergeResults(gattResults, classicTask.Result);
    }

    private string? ResolveCanonicalId(DeviceBatteryInfo device)
    {
        if (_settings is null) return device.DeviceId;

        var aliasMap = _settings.AliasMap;
        if (aliasMap.Count == 0) return device.DeviceId;

        if (aliasMap.TryGetValue(device.Name, out var exactCanonical))
            return exactCanonical;

        var normalizedDeviceName = DeviceNameNormalizer.Normalize(device.Name);
        foreach (var kv in aliasMap)
        {
            if (DeviceNameNormalizer.Normalize(kv.Key) == normalizedDeviceName)
                return kv.Value;
        }

        if (_settings.IsAliasSuggestionSuppressed(device.DeviceId))
            return device.DeviceId;

        string? bestKey      = null;
        string? bestCanonical = null;
        double  bestScore    = 0.0;
        foreach (var kv in aliasMap)
        {
            double score = JaroWinkler.Similarity(
                normalizedDeviceName,
                DeviceNameNormalizer.Normalize(kv.Key));
            if (score > bestScore)
            {
                bestScore     = score;
                bestKey       = kv.Key;
                bestCanonical = kv.Value;
            }
        }

        if (bestScore >= FuzzyThreshold && bestKey is not null && bestCanonical is not null)
        {
            AliasSuggested?.Invoke(new AliasSuggestion(
                DeviceId:          device.DeviceId,
                DeviceName:        device.Name,
                MatchedAliasKey:   bestKey,
                CanonicalDeviceId: bestCanonical,
                Score:             bestScore));
            return null;
        }

        return device.DeviceId;
    }

    private bool IsAllowedByFilter(DeviceBatteryInfo device)
    {
        if (_settings is null) return true;
        if (!_settings.CategoryFilterEnabled) return true;
        if (_settings.IsCategoryFilterOverridden(device.DeviceId)) return true;
        if (device.Category == DeviceCategory.Unknown) return true;
        return AllowedCategories.Contains(device.Category);
    }

    /// <summary>
    /// Classifies each battery result using the watched device's CoD and stamps
    /// <see cref="DeviceBatteryInfo.Category"/> so that <see cref="IsAllowedByFilter"/>
    /// can make informed decisions. Devices not found in the watched list keep
    /// <c>DeviceCategory.Unknown</c> (safe default — always allowed by filter).
    /// </summary>
    private void StampCategories(
        List<DeviceBatteryInfo> results,
        IReadOnlyList<WatchedDevice> watchedDevices,
        bool isGatt)
    {
        if (results.Count == 0) return;

        // Build lookup: DeviceId → WatchedDevice (primary key)
        var byId = new Dictionary<string, WatchedDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in watchedDevices)
            byId.TryAdd(w.DeviceId, w);

        // Secondary lookup: Name → WatchedDevice (for classic results whose
        // DeviceId may differ from the watcher's id, e.g. MAC vs SetupAPI path)
        var byName = new Dictionary<string, WatchedDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in watchedDevices)
            byName.TryAdd(w.Name, w);

        for (int i = 0; i < results.Count; i++)
        {
            var device = results[i];
            WatchedDevice? watched = null;
            if (!byId.TryGetValue(device.DeviceId, out watched))
                byName.TryGetValue(device.Name, out watched);

            if (watched is null) continue;

            var profile = _classifier.Classify(
                isBle: isGatt,
                isClassic: !isGatt,
                classOfDevice: watched.ClassOfDevice);

            if (profile.Category != DeviceCategory.Unknown)
                results[i] = device with { Category = profile.Category };
        }
    }

    private List<DeviceBatteryInfo> MergeResults(
        List<DeviceBatteryInfo> gattResults,
        List<DeviceBatteryInfo> classicResults)
    {
        var merged    = new List<DeviceBatteryInfo>();
        var seenIds   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in gattResults)
        {
            if (!IsAllowedByFilter(device)) continue;
            if (seenIds.Contains(device.DeviceId)) continue;

            var canonicalId = ResolveCanonicalId(device);
            if (canonicalId is null) continue;
            if (seenIds.Contains(canonicalId)) continue;

            seenIds.Add(canonicalId);
            seenNames.Add(device.Name);
            merged.Add(canonicalId == device.DeviceId ? device : device with { DeviceId = canonicalId });
        }

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
            merged.Add((canonicalId == device.DeviceId ? device : device with { DeviceId = canonicalId })
                with { Source = BatterySource.Classic });
        }

        return merged;
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
        catch (OperationCanceledException) { throw; }
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
            return await _readClassic(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
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
