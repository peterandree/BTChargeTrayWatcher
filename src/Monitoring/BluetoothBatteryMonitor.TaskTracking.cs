namespace BTChargeTrayWatcher;

public partial class BluetoothBatteryMonitor
{
    private void StartTrackedTask(Func<CancellationToken, Task> work)
    {
        // Always create the task first
        Task task;
        try
        {
            task = Task.Run(() => work(_shutdownCts.Token), _shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Track it immediately; disposal logic will await it
        lock (_taskGate)
        {
            if (_disposeStarted || _isDisposed)
            {
                // If already disposing, we still need to wait for this task
                _activeTasks.Add(task);
                return;
            }

            _activeTasks.Add(task);
        }

        _ = task.ContinueWith(t =>
        {
            lock (_taskGate)
            {
                _activeTasks.Remove(t);
            }

            if (t.IsFaulted && t.Exception is not null)
                System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] Background task fault: {t.Exception}");
        }, TaskScheduler.Default);
    }

    private Task[] SnapshotActiveTasks()
    {
        lock (_taskGate)
        {
            return _activeTasks.ToArray();
        }
    }
}
