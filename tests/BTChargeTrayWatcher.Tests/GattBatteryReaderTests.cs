using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class GattBatteryReaderTests
{
    [Fact]
    public async Task ReadAllAsync_from_list_uses_processor_override()
    {
        var reader = new GattBatteryReader((id, name, ct) =>
            Task.FromResult(new GattDeviceReadResult(id, name, 33, false)), TimeSpan.FromMilliseconds(1000));

        var devices = new[] { (Id: "dev1", Name: "D1"), (Id: "dev2", Name: "D2") };

        var results = await reader.ReadAllAsync(devices, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.DeviceId == "dev1" && r.Battery == 33);
        Assert.Contains(results, r => r.DeviceId == "dev2" && r.Battery == 33);
    }

    [Fact]
    public async Task PerDeviceTimeout_returns_null_battery()
    {
        // Use a short per-device timeout for fast test.
        var reader = new GattBatteryReader(async (id, name, ct) =>
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            return new GattDeviceReadResult(id, name, 77);
        }, TimeSpan.FromMilliseconds(50));

        var devices = new[] { (Id: "slow", Name: "Slow") };
        var results = await reader.ReadAllAsync(devices, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Null(results[0].Battery);
    }

    [Fact]
    public async Task Concurrency_is_limited()
    {
        int running = 0;
        int maxObserved = 0;

        Func<string, string, CancellationToken, Task<GattDeviceReadResult>> slow = async (id, name, ct) =>
        {
            Interlocked.Increment(ref running);
            try
            {
                int cur = Interlocked.CompareExchange(ref running, 0, 0);
                if (cur > maxObserved) maxObserved = cur;
                await Task.Delay(200, ct).ConfigureAwait(false);
                return new GattDeviceReadResult(id, name, 5);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        };

        var reader = new GattBatteryReader(slow, TimeSpan.FromSeconds(2));

        var devices = Enumerable.Range(0, PollingDefaults.GattMaxConcurrentReads * 3)
            .Select(i => (Id: $"d{i}", Name: $"D{i}"))
            .ToArray();

        var results = await reader.ReadAllAsync(devices, TestContext.Current.CancellationToken);

        Assert.InRange(maxObserved, 1, PollingDefaults.GattMaxConcurrentReads);
        Assert.Equal(devices.Length, results.Count);
    }
}
