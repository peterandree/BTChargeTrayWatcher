using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Tests for ADR-019 §2's operational rules (single run per invocation, global 30 s budget, early
/// cancel) and §3's summary counts. The radio is replaced by a delegate, so nothing here touches
/// Bluetooth hardware.
/// </summary>
public sealed class DeepScanRunnerTests
{
    private static DeviceBatteryInfo Device(string id, int? battery) =>
        new(id, $"Device {id}", battery);

    private static DeepScanRunner NeverFinishing(TimeSpan budget) =>
        new(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return [];
        }, budget);

    [Fact]
    public async Task Completed_run_reports_the_devices_it_read()
    {
        var runner = new DeepScanRunner(
            _ => Task.FromResult(new List<DeviceBatteryInfo>
            {
                Device("ble-1", 55),
                Device("ble-2", 80),
                Device("classic-1", null)
            }),
            TimeSpan.FromSeconds(30));

        DeepScanResult? result = await runner.RunAsync();

        Assert.True(result.HasValue);
        Assert.Equal(DeepScanOutcome.Completed, result!.Value.Outcome);
        Assert.Equal(3, result.Value.DevicesFound);
        Assert.Equal(2, result.Value.DevicesWithBattery);
        Assert.Null(result.Value.Error);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task Run_with_no_devices_still_completes()
    {
        var runner = new DeepScanRunner(_ => Task.FromResult(new List<DeviceBatteryInfo>()), TimeSpan.FromSeconds(30));

        DeepScanResult? result = await runner.RunAsync();

        Assert.Equal(DeepScanOutcome.Completed, result!.Value.Outcome);
        Assert.Equal(0, result.Value.DevicesFound);
    }

    [Fact]
    public async Task A_second_run_while_one_is_in_progress_is_refused_not_queued()
    {
        var gate = new TaskCompletionSource<List<DeviceBatteryInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int scansStarted = 0;
        var runner = new DeepScanRunner(_ => { scansStarted++; return gate.Task; }, TimeSpan.FromSeconds(30));

        Task<DeepScanResult?> first = runner.RunAsync();
        Assert.True(runner.IsRunning);

        DeepScanResult? second = await runner.RunAsync();

        // Refused, not queued: no result, and the radio was not touched a second time.
        Assert.False(second.HasValue);
        Assert.True(runner.IsRunning);
        Assert.Equal(1, scansStarted);

        gate.SetResult([Device("ble-1", 42)]);
        DeepScanResult? firstResult = await first;

        Assert.Equal(DeepScanOutcome.Completed, firstResult!.Value.Outcome);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task The_slot_is_free_again_as_soon_as_a_run_ends()
    {
        var runner = new DeepScanRunner(_ => Task.FromResult(new List<DeviceBatteryInfo>()), TimeSpan.FromSeconds(30));

        await runner.RunAsync();
        DeepScanResult? second = await runner.RunAsync();

        Assert.True(second.HasValue);
    }

    [Fact]
    public async Task Expiring_the_budget_stops_the_scan_and_reports_the_budget()
    {
        var runner = NeverFinishing(TimeSpan.FromMilliseconds(60));

        DeepScanResult? result = await runner.RunAsync();

        Assert.Equal(DeepScanOutcome.BudgetExceeded, result!.Value.Outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Value.Error);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task A_cancelled_scan_reports_a_user_cancel_not_a_budget_stop()
    {
        var runner = NeverFinishing(TimeSpan.FromSeconds(30));

        Task<DeepScanResult?> run = runner.RunAsync();
        Assert.True(runner.TryCancel());

        DeepScanResult? result = await run;

        Assert.Equal(DeepScanOutcome.CancelledByUser, result!.Value.Outcome);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public void Cancelling_when_nothing_is_running_reports_that_nothing_was_running()
    {
        var runner = NeverFinishing(TimeSpan.FromSeconds(30));

        Assert.False(runner.TryCancel());
    }

    [Fact]
    public async Task A_failing_scan_is_reported_with_its_exception_instead_of_being_swallowed()
    {
        var failure = new InvalidOperationException("radio unavailable");
        var runner = new DeepScanRunner(_ => Task.FromException<List<DeviceBatteryInfo>>(failure), TimeSpan.FromSeconds(30));

        DeepScanResult? result = await runner.RunAsync();

        Assert.Equal(DeepScanOutcome.Failed, result!.Value.Outcome);
        Assert.Same(failure, result.Value.Error);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task The_scan_receives_a_token_that_the_budget_cancels()
    {
        CancellationToken observed = default;
        var runner = new DeepScanRunner(async token =>
        {
            observed = token;
            await Task.Delay(Timeout.Infinite, token);
            return [];
        }, TimeSpan.FromMilliseconds(60));

        await runner.RunAsync();

        Assert.True(observed.IsCancellationRequested);
    }
}
