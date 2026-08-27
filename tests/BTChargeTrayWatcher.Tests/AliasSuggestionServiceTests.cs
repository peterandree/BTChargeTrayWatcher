using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class AliasSuggestionServiceTests
{
    [Fact]
    public void Same_device_across_cycles_coalesces_to_one_pending_entry()
    {
        // #151: the old behavior re-enqueued on every BeginCycle(), growing _pending
        // unbounded. Now the same DeviceId stays deduplicated while pending.
        var svc = new AliasSuggestionService();
        var queued = new List<AliasSuggestion>();
        svc.SuggestionQueued += s => queued.Add(s);

        var a = new AliasSuggestion("dev1", "Device One", "KeyA", "canon-1", 0.95);

        svc.BeginCycle();
        svc.OnAliasSuggested(a);
        Assert.Single(queued);

        // duplicate in same cycle -> ignored
        svc.OnAliasSuggested(a);
        Assert.Single(queued);

        // next cycle, same device still pending -> still ignored
        svc.BeginCycle();
        svc.OnAliasSuggested(a);
        Assert.Single(queued);

        // third cycle, still pending -> still ignored
        svc.BeginCycle();
        svc.OnAliasSuggested(a);
        Assert.Single(queued);
    }

    [Fact]
    public void Dequeue_removes_from_pending_set_allowing_re_enqueue()
    {
        // After dequeue, the same device can be suggested again.
        var svc = new AliasSuggestionService();
        var a = new AliasSuggestion("dev1", "Device One", "KeyA", "canon-1", 0.95);

        svc.OnAliasSuggested(a);
        Assert.True(svc.HasPending);
        var dequeued = svc.TryDequeue();
        Assert.Equal(a, dequeued);
        Assert.False(svc.HasPending);

        // Now the same device can be enqueued again
        var queued2 = new List<AliasSuggestion>();
        svc.SuggestionQueued += s => queued2.Add(s);
        svc.OnAliasSuggested(a);
        Assert.True(svc.HasPending);
        Assert.Single(queued2);
    }

    [Fact]
    public void TryDequeue_returns_pending_in_fifo_and_HasPending_reflects_state()
    {
        var svc = new AliasSuggestionService();
        var a1 = new AliasSuggestion("d1", "D1", "K1", "c1", 0.93);
        var a2 = new AliasSuggestion("d2", "D2", "K2", "c2", 0.94);

        svc.BeginCycle();
        svc.OnAliasSuggested(a1);
        svc.OnAliasSuggested(a2);

        Assert.True(svc.HasPending);
        var d1 = svc.TryDequeue();
        Assert.Equal(a1, d1);
        var d2 = svc.TryDequeue();
        Assert.Equal(a2, d2);
        Assert.Null(svc.TryDequeue());
        Assert.False(svc.HasPending);
    }
}
