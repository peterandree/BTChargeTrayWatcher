using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using BTChargeTrayWatcher.Monitoring.Gatt;

namespace BTChargeTrayWatcher.Tests;

public sealed class GattConnectionManagerIntegrationTests
{
    [Fact]
    public async Task Concurrency_is_limited_to_PollingDefaults()
    {
        int running = 0;
        int maxObserved = 0;

        Func<string, int, CancellationToken, Task<int?>> slowReader = async (id, timeoutMs, ct) =>
        {
            Interlocked.Increment(ref running);
            try
            {
                int cur = Interlocked.CompareExchange(ref running, 0, 0);
                if (cur > maxObserved) maxObserved = cur;
                await Task.Delay(250, ct).ConfigureAwait(false);
                return 42;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        };

        var mgr = new GattConnectionManager(slowReader);

        var tasks = Enumerable.Range(0, PollingDefaults.GattMaxConcurrentReads * 3)
            .Select(i => mgr.TryReadBatteryAsync($"dev{i}", 5000, CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        Assert.InRange(maxObserved, 1, PollingDefaults.GattMaxConcurrentReads);
    }

    [Fact]
    public async Task Timeout_requests_return_null()
    {
        // Reader respects cancellation token by delaying longer than timeout
        Func<string, int, CancellationToken, Task<int?>> longReader = async (id, timeoutMs, ct) =>
        {
            await Task.Delay(timeoutMs + 200, ct).ConfigureAwait(false);
            return 99;
        };

        var mgr = new GattConnectionManager(longReader);

        var res = await mgr.TryReadBatteryAsync("dev-timeout", 50, CancellationToken.None);
        Assert.Null(res);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        Func<string, int, CancellationToken, Task<int?>> reader = async (id, timeoutMs, ct) =>
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            return 1;
        };

        var mgr = new GattConnectionManager(reader);
        using var cts = new CancellationTokenSource();

        var task = mgr.TryReadBatteryAsync("dev-cancel", 5000, cts.Token);
        cts.Cancel();

        try
        {
            await task;
            Assert.False(true, "Expected operation to be canceled");
        }
        catch (OperationCanceledException)
        {
            // expected - includes TaskCanceledException
        }
    }
}
