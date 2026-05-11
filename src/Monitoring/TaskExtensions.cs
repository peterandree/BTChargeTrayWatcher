using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher.Monitoring;

public static class TaskExtensions
{
    public static async Task<T> WaitAsync<T>(this Task<T> task, int millisecondsTimeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(millisecondsTimeout, cts.Token);
        var completed = await Task.WhenAny(task, delayTask).ConfigureAwait(false);
        if (completed == delayTask)
            throw new TimeoutException($"Task timed out after {millisecondsTimeout}ms");
        cts.Cancel();
        return await task.ConfigureAwait(false);
    }

    public static async Task WaitAsync(this Task task, int millisecondsTimeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(millisecondsTimeout, cts.Token);
        var completed = await Task.WhenAny(task, delayTask).ConfigureAwait(false);
        if (completed == delayTask)
            throw new TimeoutException($"Task timed out after {millisecondsTimeout}ms");
        cts.Cancel();
        await task.ConfigureAwait(false);
    }
}
