using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Exercises PollingOrchestrator.UpdateAlertState (internal) directly.
/// </summary>
public sealed class PollingOrchestratorUpdateAlertStateTests : IDisposable
{
    private readonly PollingOrchestrator _poller;
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown = new();
    private readonly List<bool> _alertChanges = [];

    public PollingOrchestratorUpdateAlertStateTests()
    {
        _poller = new PollingOrchestrator(new PollingOrchestratorOptions(
            Settings:            new ThresholdSettings(),
            Notifier:            NullNotificationService.Instance,
            LastKnown:           _lastKnown,
            Tracker:             new TaskTracker(),
            ReadDevices:         _ => System.Threading.Tasks.Task.FromResult(new List<DeviceBatteryInfo>()),
            ShutdownToken:       CancellationToken.None,
            OnBatteryRead:       (_, _) => { },
            OnScanCompleted:     _ => { },
            OnAlertStateChanged: v => _alertChanges.Add(v)));
    }

    public void Dispose() => _poller.Dispose();

    private static DeviceBatteryInfo Dev(string id, string name, int pct) =>
        new(DeviceId: id, Name: name, Battery: pct, IsCharging: null);

    [Fact]
    public void UpdateAlertState_creates_new_entry_for_unknown_device()
    {
        _lastKnown["AA:BB"] = Dev("AA:BB", "Mouse", 50);

        _poller.UpdateAlertState("AA:BB", "Mouse", 50);

        // No alert expected at 50% with default thresholds (low=20, high=80)
        Assert.False(_poller.IsAnyDeviceInAlertState);
    }

    [Fact]
    public void UpdateAlertState_triggers_alert_for_low_battery()
    {
        _lastKnown["AA:BB"] = Dev("AA:BB", "Mouse", 10);

        _poller.UpdateAlertState("AA:BB", "Mouse", 10);

        Assert.True(_poller.IsAnyDeviceInAlertState);
    }
}
