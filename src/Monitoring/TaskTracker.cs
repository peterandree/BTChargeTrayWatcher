using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

internal sealed class TaskTracker
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _active = [];
    private bool _stopped;

    public void Start(Func<CancellationToken, Task> work, CancellationToken shutdownToken)
    {
        if (_stopped || shutdownToken.IsCancellationRequested) return;

        Task task;
        try
        {
            task = Task.Run(() => work(shutdownToken), shutdownToken);
        }
        catch (OperationCanceledException) { return; }

        lock (_gate)
        {
            if (_stopped) return;
            _active.Add(task);
        }

        _ = task.ContinueWith(t =>
        {
            lock (_gate) { _active.Remove(t); }
            if (t.IsFaulted && t.Exception is not null)
                System.Diagnostics.Debug.WriteLine(
                    $"[BTChargeTrayWatcher] Background task fault: {t.Exception}");
        }, TaskScheduler.Default);
    }

    public Task[] Snapshot()
    {
        lock (_gate) { return [.. _active]; }
    }

    public void Stop()
    {
        lock (_gate) { _stopped = true; }
    }
}
