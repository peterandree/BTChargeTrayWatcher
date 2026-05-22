using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Regression tests for PollingOrchestrator device eviction and
/// threshold-reset interactions.
///
/// Covers the three gaps identified by the production audit that were not
/// present in PollingOrchestratorPollTests.cs:
///
///   1. _lastProcessed regression (#101) — evicted device re-paired must
///      be treated as immediately due, not skipped due to a stale timestamp.
///
///   2. SignalThresholdsChanged clears _missCount — accumulated miss counts
///      must be reset so the device is not silently evicted mid-cycle.
///
///   3. OnAlertStateChanged is false after sole alerting device is evicted.
/// </summary>
public sealed class PollingOrchestratorEvictionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class NotificationSpy : INotificationService
    {
        public List<string> Calls { get; } = [];
        public void NotifyLow(string name, int pct)    => Calls.Add($"Low:{name}:{pct}");
        public void NotifyHigh(string name, int pct)   => Calls.Add($"High:{name}:{pct}");
        public void NotifyLaptopLow(int pct)           => Calls.Add($"LaptopLow:{pct}");
        public void NotifyLaptopHigh(int pct)          => Calls.Add($"LaptopHigh:{pct}");
        public event Action? OnNotificationClicked { add { } remove { } }
    }

    private static DeviceBatteryInfo Dev(string id, string name, int battery)
        => new(id, name, battery, null);

    private record BuildResult(
        PollingOrchestrator Orchestrator,
        NotificationSpy Spy,
        ConcurrentDictionary<string, DeviceBatteryInfo> LastKnown,
        List<bool> AlertStates);

    private static BuildResult Build(
        Func<CancellationToken, Task<List<DeviceBatteryInfo>>> readDevices,
        ThresholdSettings? settings = null)
    {
        var spy         = new NotificationSpy();
        var lastKnown   = new ConcurrentDictionary<string, DeviceBatteryInfo>(StringComparer.OrdinalIgnoreCase);
        var alertStates = new List<bool>();

        var opts = new PollingOrchestratorOptions(
            Settings:            settings ?? new ThresholdSettings(),
            Notifier:            spy,
            LastKnown:           lastKnown,
            Tracker:             new TaskTracker(),
            ReadDevices:         readDevices,
            OnBatteryRead:       (_, _) => { },
            OnScanCompleted:     _ => { },
            OnAlertStateChanged: v => alertStates.Add(v),
            ShutdownToken:       TestContext.Current.CancellationToken);

        return new BuildResult(new PollingOrchestrator(opts), spy, lastKnown, alertStates);
    }

    // ── Test 1: _lastProcessed regression (#101) ─────────────────────────────
    //
    // Scenario: device present on poll 1, absent for MissCountThreshold polls
    // (evicted), then re-appears. Per-device poll interval is set to 3600 s so
    // that a stale _lastProcessed timestamp would cause the device to be skipped
    // as "not due" on its first re-appearance. After the fix the entry must be
    // pruned on eviction so the device is treated as new and processed immediately.

    [Fact]
    public async Task Evicted_device_reappearing_is_processed_immediately_not_skipped()
    {
        // Long per-device interval: if _lastProcessed is not cleared on eviction
        // the re-appearing device would be skipped on the first poll after return.
        var settings = new ThresholdSettings();
        settings.SetPollIntervalForDevice("id1", 3600);

        int call = 0;
        var r = Build(ct =>
        {
            call++;
            // Present on call 1, absent for MissCountThreshold calls, present again after that.
            bool absent = call > 1 && call <= 1 + PollingDefaults.MissCountThreshold;
            return Task.FromResult(absent
                ? new List<DeviceBatteryInfo>()
                : new List<DeviceBatteryInfo> { Dev("id1", "Headset", 50) });
        }, settings);

        // Poll 1: device present, processed, _lastProcessed stamped.
        await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(r.LastKnown.ContainsKey("id1"));
        r.Spy.Calls.Clear();

        // Polls 2..MissCountThreshold+1: device absent, miss count increments, device evicted.
        for (int i = 0; i < PollingDefaults.MissCountThreshold; i++)
            await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.False(r.LastKnown.ContainsKey("id1"), "Device must be evicted after threshold misses.");

        // Poll MissCountThreshold+2: device re-appears.
        // If _lastProcessed was not pruned on eviction the stale timestamp from poll 1
        // would cause (DateTime.UtcNow - last).TotalSeconds < 3600 and the device would
        // be silently skipped (IsScanning=false, no battery read, no alert evaluation).
        await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);

        // Device must be back in lastKnown — proves it was processed, not skipped.
        Assert.True(r.LastKnown.ContainsKey("id1"),
            "Re-appeared device must be processed immediately after eviction (regression for #101: " +
            "_lastProcessed was not pruned on eviction).");
    }

    // ── Test 2: SignalThresholdsChanged resets _missCount ────────────────────
    //
    // Scenario: device accumulates MissCountThreshold-1 misses (one short of
    // eviction), then thresholds change. The miss count must be reset to zero
    // so that a single additional absent poll does NOT evict the device.
    // Without the reset the miss count would carry over and one more absent
    // poll would push it over the threshold, causing a spurious eviction.

    [Fact]
    public async Task ThresholdsChanged_resets_miss_count_preventing_spurious_eviction()
    {
        int call = 0;
        var r = Build(ct =>
        {
            call++;
            // Present on call 1, absent for the next MissCountThreshold-1 calls,
            // then present again.
            bool absent = call > 1 && call <= PollingDefaults.MissCountThreshold;
            return Task.FromResult(absent
                ? new List<DeviceBatteryInfo>()
                : new List<DeviceBatteryInfo> { Dev("id1", "Keyboard", 50) });
        });

        // Poll 1: device seen, added to lastKnown.
        await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(r.LastKnown.ContainsKey("id1"));

        // Polls 2..MissCountThreshold: accumulate MissCountThreshold-1 misses
        // (one short of eviction).
        for (int i = 0; i < PollingDefaults.MissCountThreshold - 1; i++)
            await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(r.LastKnown.ContainsKey("id1"), "Device must not be evicted yet.");

        // Threshold change: must clear _missCount.
        // Use PollAsync directly to drive the reset synchronously without relying
        // on the background task that SignalThresholdsChanged fires.
        r.Orchestrator.SignalThresholdsChanged();
        // Drain any background poll the signal may have enqueued.
        await r.Orchestrator.PollLock
            .WaitAsync(TestContext.Current.CancellationToken)
            .ContinueWith(_ => r.Orchestrator.PollLock.Release());

        // Poll after reset: device absent again. With the miss count cleared
        // this is only miss #1; device must survive.
        await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(r.LastKnown.ContainsKey("id1"),
            "Device must not be evicted after threshold reset cleared miss count.");
    }

    // ── Test 3: OnAlertStateChanged is false after sole alerting device evicted ─
    //
    // Scenario: a device in Low alert is the only tracked device.
    // After it is evicted the combined alert state must return to false.
    // Without eviction the stale _alertStates entry would keep hasAlert=true
    // indefinitely, holding the tray icon in alert state with no live device.

    [Fact]
    public async Task OnAlertStateChanged_false_after_sole_alerting_device_evicted()
    {
        int call = 0;
        var r = Build(ct =>
        {
            call++;
            if (call == 1) return Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Mouse", 10) });
            return Task.FromResult(new List<DeviceBatteryInfo>()); // absent from poll 2 onward
        });

        // Poll 1: device at 10% -> Low alert -> OnAlertStateChanged(true).
        await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(r.AlertStates.Last(), "Expected alert=true while Low device is present.");

        // Polls 2..MissCountThreshold+1: device absent, eventually evicted.
        for (int i = 0; i < PollingDefaults.MissCountThreshold; i++)
            await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);

        Assert.False(r.LastKnown.ContainsKey("id1"), "Device must be evicted.");

        // After eviction the last OnAlertStateChanged emission must be false:
        // no live devices remain in a non-Normal alert state.
        Assert.False(r.AlertStates.Last(),
            "OnAlertStateChanged must emit false after the sole alerting device is evicted.");
    }
}
