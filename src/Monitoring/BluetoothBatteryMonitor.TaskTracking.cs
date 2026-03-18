namespace BTChargeTrayWatcher;

public partial class BluetoothBatteryMonitor
{
    private void StartTrackedTask(Func<CancellationToken, Task> work)
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested)
            return;

        Task task;
        try
        {
            task = Task.Run(() => work(_shutdownCts.Token), _shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_taskGate)
        {
            if (_disposeStarted || _isDisposed)
                return;

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
