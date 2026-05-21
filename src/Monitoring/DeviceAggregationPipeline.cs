namespace BTChargeTrayWatcher;

/// <summary>
/// Reads and merges battery results from a GATT reader and a Classic reader.
/// </summary>
/// <remarks>
/// <para>
/// <b>Execution path: legacy IBatteryReader path only.</b>
/// This class is used by <see cref="Scanner"/> when it is constructed with explicit
/// <see cref="IBatteryReader"/> instances — i.e. via the deprecated 2-argument and
/// 4-argument <see cref="BluetoothBatteryMonitor"/> constructors.
/// </para>
/// <para>
/// <b>This class is NOT on the production path.</b> <c>Program.cs</c> exclusively uses
/// the 6-argument internal cooperation-stack constructor, which wires
/// <see cref="OrchestratorBatteryReaderAdapter"/> as the GATT reader and
/// <see cref="NullBatteryReader"/> as the Classic reader. On that path,
/// <see cref="BatteryReaderOrchestrator"/> performs all aggregation and merging.
/// </para>
/// <para>
/// Future work (tracked in issue #100): once the legacy constructors are removed,
/// this class and its test file (<c>DeviceAggregationPipelineTests.cs</c>) can be
/// deleted. Until then, it is retained to keep <c>ScannerTests</c> green.
/// </para>
/// </remarks>
internal sealed class DeviceAggregationPipeline
{
    private readonly IBatteryReader _gattReader;
    private readonly IBatteryReader _classicReader;
    private readonly Action<string, string, int?>? _onDeviceFound;

    public DeviceAggregationPipeline(
        IBatteryReader gattReader,
        IBatteryReader classicReader,
        Action<string, string, int?>? onDeviceFound)
    {
        _gattReader = gattReader;
        _classicReader = classicReader;
        _onDeviceFound = onDeviceFound;
    }

    public async Task<List<DeviceBatteryInfo>> ReadMergedAsync(
        bool raiseDeviceFound,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var gattTask = SafeReadAsync(_gattReader, ct);
        var classicTask = SafeReadAsync(_classicReader, ct);
        await Task.WhenAll(gattTask, classicTask).ConfigureAwait(false);

        ReaderOutcome gattOutcome = gattTask.Result;
        ReaderOutcome classicOutcome = classicTask.Result;

        ReportReaderErrors(gattOutcome.Error, classicOutcome.Error);
        return MergeResults(gattOutcome.Results, classicOutcome.Results, raiseDeviceFound);
    }

    private static async Task<ReaderOutcome> SafeReadAsync(IBatteryReader reader, CancellationToken ct)
    {
        try
        {
            var results = await reader.ReadAllAsync(ct).ConfigureAwait(false);
            return new ReaderOutcome(results, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ReaderOutcome([], ex);
        }
    }

    private sealed record ReaderOutcome(IReadOnlyList<DeviceBatteryInfo> Results, Exception? Error);

    private static void ReportReaderErrors(Exception? gattError, Exception? classicError)
    {
        if (gattError is not null)
            System.Diagnostics.Debug.WriteLine(
                $"[BTChargeTrayWatcher] GATT reader failed (partial results used): {gattError}");
        if (classicError is not null)
            System.Diagnostics.Debug.WriteLine(
                $"[BTChargeTrayWatcher] Classic reader failed (partial results used): {classicError}");
    }

    private List<DeviceBatteryInfo> MergeResults(
        IReadOnlyList<DeviceBatteryInfo> first,
        IReadOnlyList<DeviceBatteryInfo> second,
        bool raiseDeviceFound)
    {
        List<DeviceBatteryInfo> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var device in first)
        {
            if (!seen.Add(device.DeviceId)) continue;
            if (raiseDeviceFound) _onDeviceFound?.Invoke(device.DeviceId, device.Name, device.Battery);
            results.Add(device);
        }

        foreach (var device in second)
        {
            if (!seen.Add(device.DeviceId)) continue;
            if (raiseDeviceFound) _onDeviceFound?.Invoke(device.DeviceId, device.Name, device.Battery);
            results.Add(device);
        }

        return results;
    }
}
