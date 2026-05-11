using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Tests for PollingOrchestrator.PollAsync: alert routing, miss-count
/// eviction, threshold-change reset, callback guarantees.
/// </summary>
public sealed class PollingOrchestratorPollTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private sealed class NotificationSpy : INotificationService
    {
        public List<string> Calls { get; } = new List<string>();
        public void NotifyLow(string deviceName, int battery)   => Calls.Add($"Low:{deviceName}:{battery}");
        public void NotifyHigh(string deviceName, int battery)  => Calls.Add($"High:{deviceName}:{battery}");
        public void NotifyLaptopLow(int battery)                => Calls.Add($"LaptopLow:{battery}");
        public void NotifyLaptopHigh(int battery)               => Calls.Add($"LaptopHigh:{battery}");
    }

    private static DeviceBatteryInfo Dev(string id, string name, int battery, bool? charging = null)
        => new(id, name, battery, charging);

    private static (PollingOrchestrator orchestrator, NotificationSpy spy, ConcurrentDictionary<string, DeviceBatteryInfo> lastKnown)
        Build(
            Func<CancellationToken, Task<List<DeviceBatteryInfo>>> readDevices,
            ThresholdSettings? settings = null,
            Action<IReadOnlyList<DeviceBatteryInfo>>? onScanCompleted = null,
            Action<bool>? onAlertStateChanged = null)
    {
        var spy      = new NotificationSpy();
        var last     = new ConcurrentDictionary<string, DeviceBatteryInfo>(StringComparer.OrdinalIgnoreCase);
        var opts     = new PollingOrchestratorOptions(
            Settings:            settings ?? new ThresholdSettings(),
            Notifier:            spy,
            LastKnown:           last,
            Tracker:             new TaskTracker(),
            ReadDevices:         readDevices,
            OnBatteryRead:       (_, _) => { },
            OnScanCompleted:     onScanCompleted ?? (_ => { }),
            OnAlertStateChanged: onAlertStateChanged ?? (_ => { }),
            ShutdownToken:       CancellationToken.None);
        return (new PollingOrchestrator(opts), spy, last);
    }

    // ── Alert routing ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task New_device_below_low_fires_NotifyLow()
    {
        var (o, spy, _) = Build(_ => Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Headphones", 10) }));
        await o.PollAsync();
        Assert.Contains("Low:Headphones:10", spy.Calls);
    }

    [Fact]
    public async Task New_device_above_high_fires_NotifyHigh()
    {
        var (o, spy, _) = Build(_ => Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Headphones", 90) }));
        await o.PollAsync();
        Assert.Contains("High:Headphones:90", spy.Calls);
    }

    [Fact]
    public async Task New_device_in_normal_range_fires_no_notification()
    {
        var (o, spy, _) = Build(_ => Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Headphones", 50) }));
        await o.PollAsync();
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task Same_battery_value_on_second_poll_fires_no_notification()
    {
        // First poll: normal range
        var (o, spy, _) = Build(_ => Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Headphones", 50) }));
        await o.PollAsync();
        spy.Calls.Clear();

        // Second poll: same value -> no transition -> no notification
        await o.PollAsync();
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task Transition_Normal_to_Low_fires_NotifyLow()
    {
        int call = 0;
        var (o, spy, _) = Build(_ =>
        {
            call++;
            int battery = call == 1 ? 50 : 15;
            return Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Keyboard", battery) });
        });

        await o.PollAsync(); // battery=50, Normal
        spy.Calls.Clear();
        await o.PollAsync(); // battery=15, Low -> alert

        Assert.Contains("Low:Keyboard:15", spy.Calls);
    }

    [Fact]
    public async Task Transition_Normal_to_High_fires_NotifyHigh()
    {
        int call = 0;
        var (o, spy, _) = Build(_ =>
        {
            call++;
            int battery = call == 1 ? 50 : 90;
            return Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Mouse", battery, charging: false) });
        });

        await o.PollAsync();
        spy.Calls.Clear();
        await o.PollAsync();

        Assert.Contains("High:Mouse:90", spy.Calls);
    }

    [Fact]
    public async Task Ignored_device_below_low_fires_no_notification()
    {
        var settings = new ThresholdSettings();
        settings.ToggleIgnoreDevice("Headphones");
        var (o, spy, _) = Build(
            _ => Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Headphones", 5) }),
            settings: settings);

        await o.PollAsync();
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task Device_with_null_battery_is_skipped()
    {
        var (o, spy, last) = Build(_ => Task.FromResult(
            new List<DeviceBatteryInfo> { new DeviceBatteryInfo("id1", "Dev", null) }));

        await o.PollAsync();
        Assert.Empty(spy.Calls);
        Assert.Empty(last); // not added to lastKnown
    }

    // ── Threshold-change reset ────────────────────────────────────────────────────────

    [Fact]
    public async Task ThresholdsChanged_re_evaluates_alert_state_from_scratch()
    {
        // First poll: device at 85% — High alert fires, state = High.
        int call = 0;
        var (o, spy, _) = Build(_ =>
        {
            call++;
            return Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Dev", 85, charging: false) });
        });

        await o.PollAsync();
        Assert.Contains("High:Dev:85", spy.Calls);
        spy.Calls.Clear();

        // Signal threshold change (clears alert state cache).
        o.SignalThresholdsChanged();
        // Wait for the background task that SignalThresholdsChanged fires.
        await Task.WhenAll(o.PollLock.WaitAsync(CancellationToken.None).ContinueWith(_ => o.PollLock.Release()));

        // Run another explicit poll to get a deterministic result.
        await o.PollAsync();

        // After reset the device is treated as new — High fires again.
        Assert.Contains("High:Dev:85", spy.Calls);
    }

    // ── Miss-count eviction ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Device_absent_for_MissCountThreshold_polls_is_evicted()
    {
        // Poll 1: device present -> added to lastKnown
        int call = 0;
        var (o, _, last) = Build(_ =>
        {
            call++;
            if (call == 1) return Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Dev", 50) });
            return Task.FromResult(new List<DeviceBatteryInfo>()); // absent
        });

        await o.PollAsync(); // call=1, device added
        Assert.True(last.ContainsKey("id1"));

        // Poll MissCountThreshold times while absent
        for (int i = 0; i < PollingDefaults.MissCountThreshold; i++)
            await o.PollAsync();

        Assert.False(last.ContainsKey("id1"));
    }

    [Fact]
    public async Task Device_absent_fewer_than_threshold_polls_is_retained()
    {
        int call = 0;
        var (o, _, last) = Build(_ =>
        {
            call++;
            if (call == 1) return Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Dev", 50) });
            return Task.FromResult(new List<DeviceBatteryInfo>());
        });

        await o.PollAsync();
        // Only MissCountThreshold - 1 absent polls: not yet evicted
        for (int i = 0; i < PollingDefaults.MissCountThreshold - 1; i++)
            await o.PollAsync();

        Assert.True(last.ContainsKey("id1"));
    }

    // ── Callbacks ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnScanCompleted_receives_all_known_devices()
    {
        IReadOnlyList<DeviceBatteryInfo>? received = null;
        var (o, _, _) = Build(
            _ => Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "A", 50), Dev("id2", "B", 60) }),
            onScanCompleted: list => received = list);

        await o.PollAsync();

        Assert.NotNull(received);
        Assert.Equal(2, received!.Count);
    }

    [Fact]
    public async Task OnAlertStateChanged_true_when_any_device_in_alert()
    {
        bool? lastAlert = null;
        var (o, _, _) = Build(
            _ => Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Dev", 10) }),
            onAlertStateChanged: v => lastAlert = v);

        await o.PollAsync();
        Assert.True(lastAlert);
    }

    [Fact]
    public async Task OnAlertStateChanged_false_when_all_devices_normal()
    {
        bool? lastAlert = null;
        var (o, _, _) = Build(
            _ => Task.FromResult(new List<DeviceBatteryInfo> { Dev("id1", "Dev", 50) }),
            onAlertStateChanged: v => lastAlert = v);

        await o.PollAsync();
        Assert.False(lastAlert);
    }
}
