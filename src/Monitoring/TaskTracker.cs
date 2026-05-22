namespace BTChargeTrayWatcher;

internal sealed class TaskTracker
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _active = [];
    private bool _stopped;

    /// <summary>
    /// Schedules <paramref name="work"/> on the thread pool and tracks the resulting task.
    /// The task is registered in <see cref="_active"/> <em>before</em> <c>Task.Run</c> is called
    /// so that a <see cref="Stop"/> + <see cref="Snapshot"/> sequence that races with this method
    /// cannot miss the task between creation and registration (ADR-007).
    /// </summary>
    public void Start(Func<CancellationToken, Task> work, CancellationToken shutdownToken)
    {
        if (_stopped || shutdownToken.IsCancellationRequested) return;

        // Register a placeholder before Task.Run so Snapshot() can never miss this work.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_stopped) return;
            _active.Add(tcs.Task);
        }

        Task runTask;
        try
        {
            runTask = Task.Run(() => work(shutdownToken), shutdownToken);
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _active.Remove(tcs.Task); }
            tcs.TrySetCanceled(shutdownToken);
            return;
        }

        // When the real task completes, mirror its outcome into the TCS so that
        // WhenAll(Snapshot()) in DisposeAsync sees the actual result/exception.
        _ = runTask.ContinueWith(t =>
        {
            lock (_gate) { _active.Remove(tcs.Task); }

            if (t.IsFaulted && t.Exception is not null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BTChargeTrayWatcher] Background task fault: {t.Exception}");
                tcs.TrySetException(t.Exception.InnerExceptions);
            }
            else if (t.IsCanceled)
                tcs.TrySetCanceled();
            else
                tcs.TrySetResult();
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
