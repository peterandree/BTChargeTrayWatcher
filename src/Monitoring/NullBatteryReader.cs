namespace BTChargeTrayWatcher;

/// <summary>
/// No-op <see cref="IBatteryReader"/> that always returns an empty list.
/// Used when one reader slot in <see cref="DeviceAggregationPipeline"/> is
/// handled by another component (e.g. <see cref="BatteryReaderOrchestrator"/>).
/// </summary>
internal sealed class NullBatteryReader : IBatteryReader
{
    internal static readonly NullBatteryReader Instance = new();

    public Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new List<DeviceBatteryInfo>());
}
