namespace BTChargeTrayWatcher;

/// <summary>
/// Legacy battery reader interface.
/// </summary>
/// <remarks>
/// The cooperation-stack (production) path no longer uses this interface directly;
/// <see cref="BatteryReaderOrchestrator"/> owns the aggregation pipeline.
/// <see cref="ClassicBatteryReader"/> is the only remaining implementation and is
/// injected only via the legacy constructor path that is scheduled for removal.
/// New code must not add implementations of this interface.
/// </remarks>
[Obsolete(
    "IBatteryReader is retained for the legacy ClassicBatteryReader injection path only. " +
    "All new aggregation code must go through BatteryReaderOrchestrator. " +
    "This interface will be removed once the legacy constructor is deleted.")]
public interface IBatteryReader
{
    Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken);
}
