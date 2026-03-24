namespace BTChargeTrayWatcher;

internal sealed class DeviceAggregationPipeline
{
    private readonly IBatteryReader _gattReader;
    private readonly IBatteryReader _classicReader;
    private readonly Action<string, int>? _onDeviceFound;

    public DeviceAggregationPipeline(
        IBatteryReader gattReader,
        IBatteryReader classicReader,
        Action<string, int>? onDeviceFound)
    {
        _gattReader = gattReader;
        _classicReader = classicReader;
        _onDeviceFound = onDeviceFound;
    }

    public async Task<List<DeviceBatteryInfo>> ReadMergedAsync(
        CancellationToken ct,
        bool raiseDeviceFound)
    {
        ct.ThrowIfCancellationRequested();

        var gattTask = SafeReadAsync(_gattReader, ct);
        var classicTask = SafeReadAsync(_classicReader, ct);
        await Task.WhenAll(gattTask, classicTask).ConfigureAwait(false);

        var (gattResults, gattError) = gattTask.Result;
        var (classicResults, classicError) = classicTask.Result;

        ReportReaderErrors(gattError, classicError);
        return MergeResults(gattResults, classicResults, raiseDeviceFound);
    }

    private static async Task<(IReadOnlyList<DeviceBatteryInfo> Results, Exception? Error)>
        SafeReadAsync(IBatteryReader reader, CancellationToken ct)
    {
        try
        {
            var results = await reader.ReadAllAsync(ct).ConfigureAwait(false);
            return (results, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (Array.Empty<DeviceBatteryInfo>(), ex);
        }
    }

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
            if (raiseDeviceFound) _onDeviceFound?.Invoke(device.Name, device.Battery);
            results.Add(device);
        }

        foreach (var device in second)
        {
            if (!seen.Add(device.DeviceId)) continue;
            if (raiseDeviceFound) _onDeviceFound?.Invoke(device.Name, device.Battery);
            results.Add(device);
        }

        return results;
    }
}