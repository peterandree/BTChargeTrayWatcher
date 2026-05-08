using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Exercises Scanner.ScanNowAsync via StartTrackedScanAsync.
/// All hardware dependencies replaced with stubs.
/// </summary>
public sealed class ScannerTests : IAsyncDisposable
{
    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubBatteryReader : IBatteryReader
    {
        public List<DeviceBatteryInfo> Results { get; set; } = [];

        public Task<List<DeviceBatteryInfo>> ReadAsync(
            Action<string, string, int?>? onDeviceFound,
            CancellationToken ct) => Task.FromResult(Results);
    }

    private static DeviceBatteryInfo Dev(string id, string name, int? pct, bool? charging = null) =>
        new(DeviceId: id, Name: name, Battery: pct, IsCharging: charging);

    // ── Factory ───────────────────────────────────────────────────────────────

    private readonly List<IAsyncDisposable> _teardown = [];

    private (Scanner scanner,
             ConcurrentDictionary<string, DeviceBatteryInfo> lastKnown,
             StubBatteryReader gatt,
             List<(string name, int? pct)> batteryReads,
             List<IReadOnlyList<DeviceBatteryInfo>> scanCompletions,
             List<bool> scanStarted)
        Build()
    {
        var gatt    = new StubBatteryReader();
        var classic = new StubBatteryReader();
        var lastKnown = new ConcurrentDictionary<string, DeviceBatteryInfo>(
            StringComparer.OrdinalIgnoreCase);

        var batteryReads    = new List<(string, int?)>();
        var scanCompletions = new List<IReadOnlyList<DeviceBatteryInfo>>();
        var scanStarted     = new List<bool>();

        var settings  = new ThresholdSettings();
        var notifier  = NullNotificationService.Instance;
        var tracker   = new TaskTracker();
        var shutdownCts = new CancellationTokenSource();

        var pollerOpts = new PollingOrchestratorOptions(
            Settings:           settings,
            Notifier:           notifier,
            LastKnown:          lastKnown,
            Tracker:            tracker,
            ReadDevices:        _ => Task.FromResult(new List<DeviceBatteryInfo>()),
            ShutdownToken:      shutdownCts.Token,
            OnBatteryRead:      (_, _) => { },
            OnScanCompleted:    _ => { },
            OnAlertStateChanged:_ => { });

        var poller = new PollingOrchestrator(pollerOpts);

        var opts = new ScannerOptions(
            GattReader:      gatt,
            ClassicReader:   classic,
            LastKnown:       lastKnown,
            Poller:          poller,
            Tracker:         tracker,
            OnDeviceFound:   (_, _, _) => { },
            OnBatteryRead:   (n, p) => batteryReads.Add((n, p)),
            OnScanStarted:   () => scanStarted.Add(true),
            OnScanCompleted: list => scanCompletions.Add(list),
            ShutdownToken:   shutdownCts.Token);

        tracker.Start(_ => Task.CompletedTask, CancellationToken.None);

        var scanner = new Scanner(opts);

        _teardown.Add(new AsyncDisposableAction(async () =>
        {
            scanner.Dispose();
            tracker.Stop();
            await shutdownCts.CancelAsync();
            shutdownCts.Dispose();
            poller.Dispose();
        }));

        return (scanner, lastKnown, gatt, batteryReads, scanCompletions, scanStarted);
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

    // ══════════════════════════════════════════════════════════════════════════
    // OnScanStarted / OnScanCompleted
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OnScanStarted_fires_before_results_available()
    {
        var (scanner, _, gatt, _, _, scanStarted) = Build();
        gatt.Results = [Dev("A", "Mouse", 60)];

        await scanner.StartTrackedScanAsync();

        Assert.Single(scanStarted);
    }

    [Fact]
    public async Task OnScanCompleted_fires_after_scan_with_merged_results()
    {
        var (scanner, _, gatt, _, scanCompletions, _) = Build();
        gatt.Results = [Dev("A", "Mouse", 60), Dev("B", "Keyboard", 75)];

        await scanner.StartTrackedScanAsync();

        Assert.Single(scanCompletions);
        Assert.Equal(2, scanCompletions[0].Count);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // lastKnown and onBatteryRead
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Device_with_battery_added_to_lastKnown()
    {
        var (scanner, lastKnown, gatt, _, _, _) = Build();
        gatt.Results = [Dev("A", "Mouse", 55)];

        await scanner.StartTrackedScanAsync();

        Assert.True(lastKnown.ContainsKey("A"));
        Assert.Equal(55, lastKnown["A"].Battery);
    }

    [Fact]
    public async Task Device_with_null_battery_not_added_to_lastKnown()
    {
        var (scanner, lastKnown, gatt, _, _, _) = Build();
        gatt.Results = [Dev("A", "Ghost", null)];

        await scanner.StartTrackedScanAsync();

        Assert.False(lastKnown.ContainsKey("A"));
    }

    [Fact]
    public async Task OnBatteryRead_fires_for_each_device_with_battery()
    {
        var (scanner, _, gatt, batteryReads, _, _) = Build();
        gatt.Results =
        [
            Dev("A", "Mouse",    60),
            Dev("B", "Keyboard", null),
            Dev("C", "Headset",  40),
        ];

        await scanner.StartTrackedScanAsync();

        Assert.Equal(2, batteryReads.Count);
        Assert.Contains(batteryReads, r => r.name == "Mouse"    && r.pct == 60);
        Assert.Contains(batteryReads, r => r.name == "Headset"  && r.pct == 40);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // IsScanning
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task IsScanning_false_after_scan_completes()
    {
        var (scanner, _, gatt, _, _, _) = Build();
        gatt.Results = [Dev("A", "Mouse", 50)];

        await scanner.StartTrackedScanAsync();

        Assert.False(scanner.IsScanning);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Cancellation
    // ══════════════════════════════════════════════════════════════════════════

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
