namespace BTChargeTrayWatcher;

/// <summary>
/// Adapts <see cref="BatteryReaderOrchestrator"/> to the <see cref="IBatteryReader"/>
/// interface so it can be plugged into the existing <see cref="DeviceAggregationPipeline"/>
/// and <see cref="Scanner"/> without modifying those classes.
/// </summary>
internal sealed class OrchestratorBatteryReaderAdapter : IBatteryReader
{
    private readonly BatteryReaderOrchestrator _orchestrator;
    private readonly DeviceWatcherService _watcher;

    internal OrchestratorBatteryReaderAdapter(
        BatteryReaderOrchestrator orchestrator,
        DeviceWatcherService watcher)
    {
        _orchestrator = orchestrator;
        _watcher = watcher;
    }

    public Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken) =>
        _orchestrator.ReadAllAsync(_watcher.CurrentDevices, cancellationToken);
}
