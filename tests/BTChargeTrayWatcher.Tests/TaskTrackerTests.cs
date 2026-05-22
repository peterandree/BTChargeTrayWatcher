using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class TaskTrackerTests
{
    // ── Basic lifecycle ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_is_empty_before_any_Start()
    {
        var tracker = new TaskTracker();
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public async Task Started_task_appears_in_Snapshot_while_running()
    {
        var tracker = new TaskTracker();
        var gate = new TaskCompletionSource();

        tracker.Start(_ => gate.Task, TestContext.Current.CancellationToken); // fire-and-forget by design

        // Give the runner a moment to register
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.NotEmpty(tracker.Snapshot());

        gate.SetResult();
        await Task.WhenAll(tracker.Snapshot());
    }

    [Fact]
    public async Task Completed_task_is_removed_from_Snapshot()
    {
        var tracker = new TaskTracker();
        var tcs = new TaskCompletionSource();

        tracker.Start(_ => tcs.Task, TestContext.Current.CancellationToken); // fire-and-forget by design
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.NotEmpty(tracker.Snapshot());

        tcs.SetResult();
        // Allow continuation to run
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(tracker.Snapshot());
    }

    // ── Stop ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void After_Stop_Start_does_not_schedule_new_work()
    {
        var tracker = new TaskTracker();
        tracker.Stop();

        bool ran = false;
        tracker.Start(_ => { ran = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.False(ran);
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public async Task Stop_does_not_cancel_already_running_task()
    {
        var tracker = new TaskTracker();
        var tcs = new TaskCompletionSource();

        tracker.Start(_ => tcs.Task, TestContext.Current.CancellationToken);
        await Task.Delay(20, TestContext.Current.CancellationToken);

        tracker.Stop();

        // task is still running (not cancelled by Stop)
        Assert.NotEmpty(tracker.Snapshot());

        tcs.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    // ── Cancelled token ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Already_cancelled_token_causes_Start_to_short_circuit()
    {
        var tracker = new TaskTracker();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool ran = false;
        tracker.Start(_ => { ran = true; return Task.CompletedTask; }, cts.Token);

        Assert.False(ran);
        Assert.Empty(tracker.Snapshot());
    }

    // ── Snapshot isolation ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Snapshot_returns_a_copy_not_a_live_view()
    {
        var tracker = new TaskTracker();
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();

        tracker.Start(_ => tcs1.Task, TestContext.Current.CancellationToken); // fire-and-forget by design
        await Task.Delay(20, TestContext.Current.CancellationToken);

        var snap = tracker.Snapshot();
        _ = Assert.Single(snap);

        // Add a second task AFTER taking the snapshot
        tracker.Start(_ => tcs2.Task, TestContext.Current.CancellationToken); // fire-and-forget by design
        await Task.Delay(20, TestContext.Current.CancellationToken);

        // Original snapshot is unaffected
        _ = Assert.Single(snap);
        // Live snapshot now has two
        Assert.Equal(2, tracker.Snapshot().Length);

        tcs1.SetResult();
        tcs2.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    // ── Multiple concurrent tasks ────────────────────────────────────────────────────────

    [Fact]
    public async Task Multiple_concurrent_tasks_all_tracked()
    {
        var tracker = new TaskTracker();
        var gates = Enumerable.Range(0, 5).Select(_ => new TaskCompletionSource()).ToArray();

        foreach (var g in gates)
            tracker.Start(_ => g.Task, TestContext.Current.CancellationToken); // fire-and-forget by design

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(5, tracker.Snapshot().Length);

        foreach (var g in gates) g.SetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(tracker.Snapshot());
    }

    // ── Race condition regression (ADR-007 / issue #43) ───────────────────────────────────

    /// <summary>
    /// Verifies the add-before-schedule ordering constraint from ADR-007.
    /// Schedules a large number of near-instant tasks so that many complete
    /// before or concurrently with the TCS registration path, then asserts
    /// that _active is fully drained — no completed Task leaks in the set.
    /// </summary>
    [Fact]
    public async Task Start_race_condition_no_task_leaks_in_active_set()
    {
        const int iterations = 500;

        for (int i = 0; i < iterations; i++)
        {
            var tracker = new TaskTracker();

            // work completes synchronously (returns Task.CompletedTask), maximising
            // the chance that ContinueWith fires before _active.Add in a buggy impl.
            tracker.Start(_ => Task.CompletedTask, TestContext.Current.CancellationToken); // fire-and-forget by design

            // Allow all continuations to drain.
            await Task.Yield();
            await Task.Delay(10, TestContext.Current.CancellationToken);

            var leaked = tracker.Snapshot().Where(t => t.IsCompleted).ToArray();
            Assert.Empty(leaked);
        }
    }
}
