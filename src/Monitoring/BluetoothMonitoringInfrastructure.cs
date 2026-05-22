namespace BTChargeTrayWatcher;

/// <summary>
/// Groups the cooperation-stack dependencies required to drive
/// <see cref="BluetoothBatteryMonitor"/>. Introduced to reduce the
/// constructor parameter count from 7 to 3 (ADR: constructor parameter objects).
/// </summary>
internal sealed record BluetoothMonitoringInfrastructure(
    DeviceWatcherService      DeviceWatcher,
    BatteryReaderOrchestrator Orchestrator,
    GattConnectionManager     GattConnectionManager,
    DeviceCapabilityCache     CapabilityCache,
    AliasSuggestionService    AliasSuggestionService);
