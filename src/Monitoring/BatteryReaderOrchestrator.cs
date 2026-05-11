using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher.Monitoring;

/// <summary>
/// Orchestrates multiple <see cref="IBatteryReader"/> implementations and merges
/// their results. GATT results win when device IDs collide. Exceptions from
/// individual readers are isolated so a failing reader does not prevent the
/// other from supplying results.
/// </summary>
public sealed class BatteryReaderOrchestrator : IBatteryReader
{
    private readonly IBatteryReader _gattReader;
    private readonly IBatteryReader _classicReader;
    private readonly DeviceCapabilityCache _capabilityCache;

    public BatteryReaderOrchestrator(IBatteryReader gattReader, IBatteryReader classicReader, DeviceCapabilityCache? capabilityCache = null)
    {
        _gattReader = gattReader ?? throw new ArgumentNullException(nameof(gattReader));
        _classicReader = classicReader ?? throw new ArgumentNullException(nameof(classicReader));
        _capabilityCache = capabilityCache ?? new DeviceCapabilityCache();
    }

    public async Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var gattTask = SafeReadAsync(_gattReader, cancellationToken);
        var classicTask = SafeReadAsync(_classicReader, cancellationToken);

        await Task.WhenAll(gattTask, classicTask).ConfigureAwait(false);

        var gattResults = gattTask.Result;
        var classicResults = classicTask.Result;

        var results = new List<DeviceBatteryInfo>(gattResults.Count + classicResults.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // GATT wins on duplicate device id
        foreach (var d in gattResults)
        {
            if (d is null) continue;
            if (!seen.Add(d.DeviceId)) continue;
            results.Add(d);
            _capabilityCache.RecordSuccess(d.DeviceId);
        }

        foreach (var d in classicResults)
        {
            if (d is null) continue;
            if (!seen.Add(d.DeviceId)) continue;
            results.Add(d);
            _capabilityCache.RecordSuccess(d.DeviceId);
        }

        return results;
    }

    private static async Task<List<DeviceBatteryInfo>> SafeReadAsync(IBatteryReader reader, CancellationToken ct)
    {
        try
        {
            var res = await reader.ReadAllAsync(ct).ConfigureAwait(false);
            return res ?? new List<DeviceBatteryInfo>();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Swallow reader exceptions and return empty results to allow partial progress
            return new List<DeviceBatteryInfo>();
        }
    }
}
