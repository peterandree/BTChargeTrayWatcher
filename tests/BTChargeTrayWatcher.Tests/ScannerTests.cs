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

    private (Scanner scanner,
             ConcurrentDictionary<string, DeviceBatteryInfo> lastKnown,
             List<DeviceBatteryInfo> gattResults,
             List<(string name, int? pct)> batteryReads,
             List<IReadOnlyList<DeviceBatteryInfo>> scanCompletions,
             List<bool> scanStarted)
        Build()
    {
        var gattResults  = new List<DeviceBatteryInfo>();
        var lastKnown    = new ConcurrentDictionary<string, DeviceBatteryInfo>(
            StringComparer.OrdinalIgnoreCase);

        var batteryReads    = new List<(string, int?)>();
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

        var opts = new ScannerOptions(
            ReadGatt:      _ => Task.FromResult(gattResults),
            ReadClassic:   _ => Task.FromResult(new List<DeviceBatteryInfo>()),
            LastKnown:     lastKnown,
            Poller:        poller,
            Tracker:       tracker,
            ShutdownToken: shutdownCts.Token,
            Callbacks:     new ScannerCallbacks(
                OnDeviceFound:   (_, _, _) => { },
                OnBatteryRead:   (n, p) => batteryReads.Add((n, p)),
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

        return (scanner, lastKnown, gattResults, batteryReads, scanCompletions, scanStarted);
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
        var (scanner, _, gattResults, _, _, scanStarted) = Build();
        gattResults.Add(Dev("A", "Mouse", 60));

        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.Single(scanStarted);
    }

    [Fact]
    public async Task OnScanCompleted_fires_after_scan_with_merged_results()
    {
        var (scanner, _, gattResults, _, scanCompletions, _) = Build();
        gattResults.Add(Dev("A", "Mouse", 60));
        gattResults.Add(Dev("B", "Keyboard", 75));

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
        var (scanner, lastKnown, gattResults, _, _, _) = Build();
        gattResults.Add(Dev("A", "Mouse", 55));

        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.True(lastKnown.ContainsKey("A"));
        Assert.Equal(55, lastKnown["A"].Battery);
    }

    [Fact]
    public async Task Device_with_null_battery_not_added_to_lastKnown()
    {
        var (scanner, lastKnown, gattResults, _, _, _) = Build();
        gattResults.Add(Dev("A", "Ghost", null));

        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.False(lastKnown.ContainsKey("A"));
    }

    [Fact]
    public async Task OnBatteryRead_fires_for_each_device_with_battery()
    {
        var (scanner, _, gattResults, batteryReads, _, _) = Build();
        gattResults.Add(Dev("A", "Mouse",    60));
        gattResults.Add(Dev("B", "Keyboard", null));
        gattResults.Add(Dev("C", "Headset",  40));

        await scanner.StartTrackedScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, batteryReads.Count);
        Assert.Contains(batteryReads, r => r.name == "Mouse"   && r.pct == 60);
        Assert.Contains(batteryReads, r => r.name == "Headset" && r.pct == 40);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // IsScanning
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task IsScanning_false_after_scan_completes()
    {
        var (scanner, _, gattResults, _, _, _) = Build();
        gattResults.Add(Dev("A", "Mouse", 50));

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
