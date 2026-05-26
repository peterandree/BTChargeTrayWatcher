using System.Collections.Concurrent;
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
            ShutdownToken: TestContext.Current.CancellationToken,
            Callbacks: new PollingOrchestratorCallbacks(
                OnBatteryRead:       (_, _) => { },
                OnScanCompleted:     _     => { },
                OnAlertStateChanged: v     => alertStates.Add(v)));

        return new BuildResult(new PollingOrchestrator(opts), spy, lastKnown, alertStates);
    }

    // ── Test 1: _lastProcessed regression (#101) ─────────────────────────────

    [Fact]
    public async Task Evicted_device_reappearing_is_processed_immediately_not_skipped()
    {
        var settings = new ThresholdSettings();
        settings.SetPollIntervalForDevice("id1", 3600);

        int call = 0;
        var r = Build(ct =>
        {
            call++;
            bool absent = call > 1 && call <= 1 + PollingDefaults.MissCountThreshold;
            return Task.FromResult(absent
                ? new List<DeviceBatteryInfo>()
                : new List<DeviceBatteryInfo> { Dev("id1", "Headset", 50) });
        }, settings);

        await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(r.LastKnown.ContainsKey("id1"));
        r.Spy.Calls.Clear();

        for (int i = 0; i < PollingDefaults.MissCountThreshold; i++)
            await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.False(r.LastKnown.ContainsKey("id1"), "Device must be evicted after threshold misses.");

        await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);

        Assert.True(r.LastKnown.ContainsKey("id1"),
            "Re-appeared device must be processed immediately after eviction (regression for #101: " +
            "_lastProcessed was not pruned on eviction).");
    }

    // ── Test 2: SignalThresholdsChanged resets _missCount ────────────────────

    [Fact]
    public async Task ThresholdsChanged_resets_miss_count_preventing_spurious_eviction()
    {
        int call = 0;
        var r = Build(ct =>
        {
            call++;
            bool absent = call > 1 && call <= PollingDefaults.MissCountThreshold;
            return Task.FromResult(absent
                ? new List<DeviceBatteryInfo>()
                : new List<DeviceBatteryInfo> { Dev("id1", "Keyboard", 50) });
        });

        await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(r.LastKnown.ContainsKey("id1"));

        for (int i = 0; i < PollingDefaults.MissCountThreshold - 1; i++)
            await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(r.LastKnown.ContainsKey("id1"), "Device must not be evicted yet.");

        r.Orchestrator.SignalThresholdsChanged();
        await r.Orchestrator.PollLock
            .WaitAsync(TestContext.Current.CancellationToken)
            .ContinueWith(_ => r.Orchestrator.PollLock.Release());

        await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(r.LastKnown.ContainsKey("id1"),
            "Device must not be evicted after threshold reset cleared miss count.");
    }

    // ── Test 3: OnAlertStateChanged is false after sole alerting device evicted ─

    [Fact]
    public async Task OnAlertStateChanged_false_after_sole_alerting_device_evicted()
    {
        int call = 0;
        var r = Build(ct =>
        {
            call++;
            if (call == 1) return Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Mouse", 10) });
            return Task.FromResult(new List<DeviceBatteryInfo>());
        });

        await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(r.AlertStates.Last(), "Expected alert=true while Low device is present.");

        for (int i = 0; i < PollingDefaults.MissCountThreshold; i++)
            await r.Orchestrator.PollAsync(TestContext.Current.CancellationToken);

        Assert.False(r.LastKnown.ContainsKey("id1"), "Device must be evicted.");

        Assert.False(r.AlertStates.Last(),
            "OnAlertStateChanged must emit false after the sole alerting device is evicted.");
    }
}
