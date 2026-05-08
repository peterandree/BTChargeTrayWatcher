using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        tracker.Start(_ => gate.Task, CancellationToken.None);

        // Give the runner a moment to register
        await Task.Delay(20);
        Assert.NotEmpty(tracker.Snapshot());

        gate.SetResult();
        await Task.WhenAll(tracker.Snapshot());
    }

    [Fact]
    public async Task Completed_task_is_removed_from_Snapshot()
    {
        var tracker = new TaskTracker();
        var tcs = new TaskCompletionSource();

        tracker.Start(_ => tcs.Task, CancellationToken.None);
        await Task.Delay(20);
        Assert.NotEmpty(tracker.Snapshot());

        tcs.SetResult();
        // Allow continuation to run
        await Task.Delay(50);
        Assert.Empty(tracker.Snapshot());
    }

    // ── Stop ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void After_Stop_Start_does_not_schedule_new_work()
    {
        var tracker = new TaskTracker();
        tracker.Stop();

        bool ran = false;
        tracker.Start(_ => { ran = true; return Task.CompletedTask; }, CancellationToken.None);

        Assert.False(ran);
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public async Task Stop_does_not_cancel_already_running_task()
    {
        var tracker = new TaskTracker();
        var tcs = new TaskCompletionSource();

        tracker.Start(_ => tcs.Task, CancellationToken.None);
        await Task.Delay(20);

        tracker.Stop();

        // task is still running (not cancelled by Stop)
        Assert.NotEmpty(tracker.Snapshot());

        tcs.SetResult();
        await Task.Delay(50);
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

        tracker.Start(_ => tcs1.Task, CancellationToken.None);
        await Task.Delay(20);

        var snap = tracker.Snapshot();
        Assert.Single(snap);

        // Add a second task AFTER taking the snapshot
        tracker.Start(_ => tcs2.Task, CancellationToken.None);
        await Task.Delay(20);

        // Original snapshot is unaffected
        Assert.Single(snap);
        // Live snapshot now has two
        Assert.Equal(2, tracker.Snapshot().Length);

        tcs1.SetResult();
        tcs2.SetResult();
        await Task.Delay(50);
    }

    // ── Multiple concurrent tasks ────────────────────────────────────────────────────────

    [Fact]
    public async Task Multiple_concurrent_tasks_all_tracked()
    {
        var tracker = new TaskTracker();
        var gates = Enumerable.Range(0, 5).Select(_ => new TaskCompletionSource()).ToArray();

        foreach (var g in gates)
            tracker.Start(_ => g.Task, CancellationToken.None);

        await Task.Delay(50);
        Assert.Equal(5, tracker.Snapshot().Length);

        foreach (var g in gates) g.SetResult();
        await Task.Delay(100);
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
            tracker.Start(_ => Task.CompletedTask, CancellationToken.None);

            // Allow all continuations to drain.
            await Task.Yield();
            await Task.Delay(10);

            var leaked = tracker.Snapshot().Where(t => t.IsCompleted).ToArray();
            Assert.Empty(leaked);
        }
    }
}
