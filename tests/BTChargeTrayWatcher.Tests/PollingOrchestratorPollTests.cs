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
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private sealed class NotificationSpy : INotificationService
    {
        public List<string> Calls { get; } = [];
        public void NotifyLow(string deviceName, int battery)   => Calls.Add($"Low:{deviceName}:{battery}");
        public void NotifyHigh(string deviceName, int battery)  => Calls.Add($"High:{deviceName}:{battery}");
        public void NotifyLaptopLow(int battery)                => Calls.Add($"LaptopLow:{battery}");
        public void NotifyLaptopHigh(int battery)               => Calls.Add($"LaptopHigh:{battery}");
        public event Action? OnNotificationClicked { add { } remove { } }
    }

    private static DeviceBatteryInfo Dev(string id, string name, int battery, bool? charging = null)
        => new(id, name, battery, charging);

    private static Task<List<DeviceBatteryInfo>> Result(params DeviceBatteryInfo[] devices)
        => Task.FromResult(new List<DeviceBatteryInfo>(devices));

    /// <param name="pollIntervalSec">
    /// Per-device poll interval in seconds applied to ALL devices via a global override.
    /// Use 0 in tests that call PollAsync multiple times and need every poll to be processed.
    /// Omit (null) to use the production default, which is seconds-scale and will skip
    /// back-to-back polls within the same test.
    /// </param>
    private static (PollingOrchestrator orchestrator, NotificationSpy spy, ConcurrentDictionary<string, DeviceBatteryInfo> lastKnown)
        Build(
            Func<CancellationToken, Task<List<DeviceBatteryInfo>>> readDevices,
            ThresholdSettings? settings = null,
            int? pollIntervalSec = null,
            Action<string, int?>? onBatteryRead = null,
            Action<IReadOnlyList<DeviceBatteryInfo>>? onScanCompleted = null,
            Action<bool>? onAlertStateChanged = null)
    {
        var spy  = new NotificationSpy();
        var last = new ConcurrentDictionary<string, DeviceBatteryInfo>(StringComparer.OrdinalIgnoreCase);
        var s    = settings ?? new ThresholdSettings();
        if (pollIntervalSec.HasValue)
            s.SetGlobalPollInterval(pollIntervalSec.Value);
        var opts = new PollingOrchestratorOptions(
            Settings:      s,
            Notifier:      spy,
            LastKnown:     last,
            Tracker:       new TaskTracker(),
            ReadDevices:   readDevices,
            Callbacks:     new PollingOrchestratorCallbacks(
                OnBatteryRead:       onBatteryRead ?? ((_, _) => { }),
                OnScanCompleted:     onScanCompleted ?? (_ => { }),
                OnAlertStateChanged: onAlertStateChanged ?? (_ => { })),
            ShutdownToken: TestContext.Current.CancellationToken);
        return (new PollingOrchestrator(opts), spy, last);
    }

    // ── Alert routing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task New_device_below_low_fires_NotifyLow()
    {
        var (o, spy, _) = Build(_ => Result(Dev("id1", "Headphones", 10)));
        await o.PollAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Low:Headphones:10", spy.Calls);
    }

    [Fact]
    public async Task New_device_above_high_fires_NotifyHigh()
    {
        var (o, spy, _) = Build(_ => Result(Dev("id1", "Headphones", 90)));
        await o.PollAsync(TestContext.Current.CancellationToken);
        Assert.Contains("High:Headphones:90", spy.Calls);
    }

    [Fact]
    public async Task New_device_in_normal_range_fires_no_notification()
    {
        var (o, spy, _) = Build(_ => Result(Dev("id1", "Headphones", 50)));
        await o.PollAsync(TestContext.Current.CancellationToken);
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task Same_battery_value_on_second_poll_fires_no_notification()
    {
        var (o, spy, _) = Build(_ => Result(Dev("id1", "Headphones", 50)), pollIntervalSec: 0);
        await o.PollAsync(TestContext.Current.CancellationToken);
        spy.Calls.Clear();
        await o.PollAsync(TestContext.Current.CancellationToken);
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
            return Result(Dev("id1", "Keyboard", battery));
        }, pollIntervalSec: 0);

        await o.PollAsync(TestContext.Current.CancellationToken);
        spy.Calls.Clear();
        await o.PollAsync(TestContext.Current.CancellationToken);

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
            return Result(Dev("id1", "Mouse", battery, charging: false));
        }, pollIntervalSec: 0);

        await o.PollAsync(TestContext.Current.CancellationToken);
        spy.Calls.Clear();
        await o.PollAsync(TestContext.Current.CancellationToken);

        Assert.Contains("High:Mouse:90", spy.Calls);
    }

    [Fact]
    public async Task Ignored_device_below_low_fires_no_notification()
    {
        var settings = new ThresholdSettings();
        settings.ToggleIgnoreDevice("Headphones");
        var (o, spy, _) = Build(
            _ => Result(Dev("id1", "Headphones", 5)),
            settings: settings);

        await o.PollAsync(TestContext.Current.CancellationToken);
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task Device_with_null_battery_is_skipped()
    {
        var (o, spy, last) = Build(_ => Result(new DeviceBatteryInfo("id1", "Dev", null)));

        await o.PollAsync(TestContext.Current.CancellationToken);
        Assert.Empty(spy.Calls);
        Assert.Empty(last);
    }

    // ── Threshold-change reset ───────────────────────────────────────────────────

    [Fact]
    public async Task ThresholdsChanged_re_evaluates_alert_state_from_scratch()
    {
        int call = 0;
        var (o, spy, _) = Build(_ =>
        {
            call++;
            return Result(Dev("id1", "Dev", 85, charging: false));
        });

        await o.PollAsync(TestContext.Current.CancellationToken);
        Assert.Contains("High:Dev:85", spy.Calls);
        spy.Calls.Clear();

        o.SignalThresholdsChanged();
        await Task.WhenAll(o.PollLock.WaitAsync(TestContext.Current.CancellationToken).ContinueWith(_ => o.PollLock.Release()));

        await o.PollAsync(TestContext.Current.CancellationToken);

        Assert.Contains("High:Dev:85", spy.Calls);
    }

    // ── Miss-count eviction ──────────────────────────────────────────────────────

    [Fact]
    public async Task Device_absent_for_MissCountThreshold_polls_is_evicted()
    {
        int call = 0;
        var (o, _, last) = Build(_ =>
        {
            call++;
            if (call == 1) return Result(Dev("id1", "Dev", 50));
            return Task.FromResult(new List<DeviceBatteryInfo>());
        });

        await o.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(last.ContainsKey("id1"));

        for (int i = 0; i < PollingDefaults.MissCountThreshold; i++)
            await o.PollAsync(TestContext.Current.CancellationToken);

        Assert.False(last.ContainsKey("id1"));
    }

    [Fact]
    public async Task Device_absent_fewer_than_threshold_polls_is_retained()
    {
        int call = 0;
        var (o, _, last) = Build(_ =>
        {
            call++;
            if (call == 1) return Result(Dev("id1", "Dev", 50));
            return Task.FromResult(new List<DeviceBatteryInfo>());
        });

        await o.PollAsync(TestContext.Current.CancellationToken);
        for (int i = 0; i < PollingDefaults.MissCountThreshold - 1; i++)
            await o.PollAsync(TestContext.Current.CancellationToken);

        Assert.True(last.ContainsKey("id1"));
    }

    // ── Callbacks ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnScanCompleted_receives_all_known_devices()
    {
        IReadOnlyList<DeviceBatteryInfo>? received = null;
        var (o, _, _) = Build(
            _ => Result(Dev("id1", "A", 50), Dev("id2", "B", 60)),
            onScanCompleted: list => received = list);

        await o.PollAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.Equal(2, received!.Count);
    }

    [Fact]
    public async Task OnAlertStateChanged_true_when_any_device_in_alert()
    {
        bool? lastAlert = null;
        var (o, _, _) = Build(
            _ => Result(Dev("id1", "Dev", 10)),
            onAlertStateChanged: v => lastAlert = v);

        await o.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(lastAlert);
    }

    [Fact]
    public async Task OnAlertStateChanged_false_when_all_devices_normal()
    {
        bool? lastAlert = null;
        var (o, _, _) = Build(
            _ => Result(Dev("id1", "Dev", 50)),
            onAlertStateChanged: v => lastAlert = v);

        await o.PollAsync(TestContext.Current.CancellationToken);
        Assert.False(lastAlert);
    }

    [Fact]
    public async Task Per_device_poll_interval_is_respected()
    {
        // A 3600-second per-device interval means the second PollAsync call
        // (within the same second) must skip processing and not invoke OnBatteryRead.
        var settings = new ThresholdSettings();
        settings.SetPollIntervalForDevice("id1", 3600);
        int batteryReadCount = 0;
        var (o, _, _) = Build(
            _ => Result(Dev("id1", "Dev", 50)),
            settings: settings,
            onBatteryRead: (_, _) => batteryReadCount++);

        await o.PollAsync(TestContext.Current.CancellationToken); // poll 1: no prior timestamp → processed
        int afterFirst = batteryReadCount;
        await o.PollAsync(TestContext.Current.CancellationToken); // poll 2: interval not elapsed → skipped
        Assert.Equal(afterFirst, batteryReadCount);
    }
}
