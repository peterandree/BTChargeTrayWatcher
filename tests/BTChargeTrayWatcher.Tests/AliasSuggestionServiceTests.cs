using System.Collections.Generic;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class AliasSuggestionServiceTests
{
    [Fact]
    public void BeginCycle_resets_dedup_and_queues_once_per_device()
    {
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

        // next cycle -> allowed again
        svc.BeginCycle();
        svc.OnAliasSuggested(a);
        Assert.Equal(2, queued.Count);
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
