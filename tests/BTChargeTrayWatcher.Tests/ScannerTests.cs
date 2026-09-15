using System.Collections.Concurrent;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Exercises Scanner.ScanNowAsync via StartTrackedScanAsync.
/// All hardware dependencies replaced with stubs.
/// </summary>
public sealed class ScannerTests : IAsyncDisposable
{
    // ── Stubs ──────────────────────────────────────────────────────────────────────────────

    private static DeviceBatteryInfo Dev(string id, string name, int? pct, bool? charging = null) =>
        new(DeviceId: id, Name: name, Battery: pct, IsCharging: charging);

    // ── Factory ───────────────────────────────────────────────────────────────────────

    private readonly List<IAsyncDisposable> _teardown = [];

    /// <summary>
    /// Which read mode each delegate invocation represented, in call order. Asserted by the mode
    /// routing tests below: #164 was exactly a case of two callers sharing one mode.
    /// </summary>
    private readonly List<BatteryReadMode> _readModes = [];

    private sealed record BatteryRead(string Name, int? Pct);

    private sealed record ScannerBuildResult(
        Scanner scanner,
        ConcurrentDictionary<string, DeviceBatteryInfo> lastKnown,
        List<DeviceBatteryInfo> deviceResults,
        List<BatteryRead> batteryReads,
        List<IReadOnlyList<DeviceBatteryInfo>> scanCompletions,
        List<bool> scanStarted);

    private ScannerBuildResult Build()
    {
        _readModes.Clear();
        var deviceResults = new List<DeviceBatteryInfo>();
        var lastKnown    = new ConcurrentDictionary<string, DeviceBatteryInfo>(
            StringComparer.OrdinalIgnoreCase);
        var batteryReads    = new List<BatteryRead>();
        var scanCompletions = new List<IReadOnlyList<DeviceBatteryInfo>>();
        var scanStarted     = new List<bool>();

        var settings    = new ThresholdSettings();
        var notifier    = NullNotificationService.Instance;
        var tracker     = new TaskTracker();
        var shutdownCts = new CancellationTokenSource();

        var pollerOpts = new PollingOrchestratorOptions(
            Settings:      settings,
            Notifier:      notifier,
            LastKnown:     lastKnown,
            Tracker:       tracker,
            ReadDevices:   _ => Task.FromResult(new List<DeviceBatteryInfo>()),
            ShutdownToken: TestContext.Current.CancellationToken,
            Callbacks:     new PollingOrchestratorCallbacks(
                OnBatteryRead:       (_, _) => { },
                OnScanCompleted:     _ => { },
                OnAlertStateChanged: _ => { }));

        var poller = new PollingOrchestrator(pollerOpts);

        // #157: the Scanner consumes one already-merged source (the orchestrator's output) per read
        // mode; the two are separate delegates so a scan can never silently run on the quiet path
        // (issue #164).
        var opts = new ScannerOptions(
            ReadDevices:     _ =>
            {
                _readModes.Add(BatteryReadMode.Background);
                return Task.FromResult(deviceResults);
            },
            DeepReadDevices: _ =>
            {
                _readModes.Add(BatteryReadMode.DeepScan);
                return Task.FromResult(deviceResults);
            },
            LastKnown:       lastKnown,
            Poller:          poller,
            Tracker:         tracker,
            ShutdownToken:   shutdownCts.Token,
            Callbacks:       new ScannerCallbacks(
                OnDeviceFound:   (_, _, _) => { },
                OnBatteryRead:   (n, p) => batteryReads.Add(new BatteryRead(n, p)),
                OnScanStarted:   () => scanStarted.Add(true),
                OnScanCompleted: list => scanCompletions.Add(list)));

        tracker.Start(_ => Task.CompletedTask, TestContext.Current.CancellationToken);

        var scanner = new Scanner(opts);

        _teardown.Add(new AsyncDisposableAction(async () =>
        {
            scanner.Dispose();
            tracker.Stop();
            await shutdownCts.CancelAsync();
            shutdownCts.Dispose();
            poller.Dispose();
        }));

        return new ScannerBuildResult(scanner, lastKnown, deviceResults, batteryReads, scanCompletions, scanStarted);
    }

    private sealed class AsyncDisposableAction(Func<Task> action) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await action();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _teardown)
            await d.DisposeAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // OnScanStarted / OnScanCompleted
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OnScanStarted_fires_before_results_available()
    {
        var (scanner, _, deviceResults, _, _, scanStarted) = Build();
        deviceResults.Add(Dev("A", "Mouse", 60));

        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.Single(scanStarted);
    }

    [Fact]
    public async Task OnScanCompleted_fires_after_scan_with_merged_results()
    {
        var (scanner, _, deviceResults, _, scanCompletions, _) = Build();
        deviceResults.Add(Dev("A", "Mouse", 60));
        deviceResults.Add(Dev("B", "Keyboard", 75));

        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.Single(scanCompletions);
        Assert.Equal(2, scanCompletions[0].Count);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // lastKnown and onBatteryRead
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Device_with_battery_added_to_lastKnown()
    {
        var (scanner, lastKnown, deviceResults, _, _, _) = Build();
        deviceResults.Add(Dev("A", "Mouse", 55));

        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.True(lastKnown.ContainsKey("A"));
        Assert.Equal(55, lastKnown["A"].Battery);
    }

    [Fact]
    public async Task Device_with_null_battery_not_added_to_lastKnown()
    {
        var (scanner, lastKnown, deviceResults, _, _, _) = Build();
        deviceResults.Add(Dev("A", "Ghost", null));

        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.False(lastKnown.ContainsKey("A"));
    }

    [Fact]
    public async Task OnBatteryRead_fires_for_each_device_with_battery()
    {
        var (scanner, _, deviceResults, batteryReads, _, _) = Build();
        deviceResults.Add(Dev("A", "Mouse",    60));
        deviceResults.Add(Dev("B", "Keyboard", null));
        deviceResults.Add(Dev("C", "Headset",  40));

        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, batteryReads.Count);
        Assert.Contains(batteryReads, r => r.Name == "Mouse"   && r.Pct == 60);
        Assert.Contains(batteryReads, r => r.Name == "Headset" && r.Pct == 40);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Read-mode routing (#164)
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task User_initiated_deep_scan_uses_the_deep_read_delegate()
    {
        var (scanner, _, deviceResults, _, _, _) = Build();
        deviceResults.Add(Dev("A", "Mouse", 60));

        await scanner.ScanNowAsync(BatteryReadMode.DeepScan, TestContext.Current.CancellationToken);

        Assert.Equal([BatteryReadMode.DeepScan], _readModes);
    }

    [Fact]
    public async Task Scan_without_an_explicit_mode_stays_passive()
    {
        // The automatic startup scan uses this overload; it must never become active probing
        // without an explicit caller decision (ADR-017, issue #164).
        var (scanner, _, deviceResults, _, _, _) = Build();
        deviceResults.Add(Dev("A", "Mouse", 60));

        await scanner.ScanNowAsync(TestContext.Current.CancellationToken);
        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal([BatteryReadMode.Background, BatteryReadMode.Background], _readModes);
    }

    [Fact]
    public async Task Quiet_read_uses_the_background_delegate()
    {
        var (scanner, _, _, _, _, _) = Build();

        await scanner.QuietReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal([BatteryReadMode.Background], _readModes);
    }

    [Fact]
    public async Task Scan_and_quiet_read_do_not_share_a_delegate()
    {
        var (scanner, _, _, _, _, _) = Build();
        var ct = TestContext.Current.CancellationToken;

        await scanner.QuietReadAsync(ct);
        await scanner.StartTrackedScanAsync(BatteryReadMode.DeepScan, ct);
        await scanner.QuietReadAsync(ct);

        Assert.Equal(
            [BatteryReadMode.Background, BatteryReadMode.DeepScan, BatteryReadMode.Background],
            _readModes);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // IsScanning
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task IsScanning_false_after_scan_completes()
    {
        var (scanner, _, deviceResults, _, _, _) = Build();
        deviceResults.Add(Dev("A", "Mouse", 50));

        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.False(scanner.IsScanning);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Cancellation
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cancelled_token_propagates_as_OperationCanceledException()
    {
        var (scanner, _, _, _, _, _) = Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanNowAsync(cts.Token));
    }
}
