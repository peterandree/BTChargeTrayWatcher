using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using BTChargeTrayWatcher;
using BTChargeTrayWatcher.Monitoring;

namespace BTChargeTrayWatcher.Tests;

public sealed class BatteryReaderOrchestratorTests
{
    private sealed class StaticReader : IBatteryReader
    {
        private readonly List<DeviceBatteryInfo> _items;
        public StaticReader(IEnumerable<DeviceBatteryInfo> items) => _items = new List<DeviceBatteryInfo>(items);
        public Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken) => Task.FromResult(new List<DeviceBatteryInfo>(_items));
    }

    private sealed class ThrowingReader : IBatteryReader
    {
        public Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken) => throw new System.InvalidOperationException("boom");
    }

    private sealed class NullReader : IBatteryReader
    {
        public Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken) => Task.FromResult<List<DeviceBatteryInfo>>(null);
    }

    [Fact]
    public async Task GattWinsOnDuplicateDeviceId()
    {
        var gatt = new StaticReader(new[] { new DeviceBatteryInfo("d1", "Gatt", 90) });
        var classic = new StaticReader(new[] { new DeviceBatteryInfo("d1", "Classic", 10) });
        var orchestrator = new BatteryReaderOrchestrator(gatt, classic);

        var res = await orchestrator.ReadAllAsync(CancellationToken.None);

        Assert.Single(res);
        Assert.Equal("Gatt", res[0].Name);
        Assert.Equal(90, res[0].Battery);
    }

    [Fact]
    public async Task FailingReaderDoesNotPreventOther()
    {
        var gatt = new ThrowingReader();
        var classic = new StaticReader(new[] { new DeviceBatteryInfo("c1", "ClassicOnly", 50) });
        var orchestrator = new BatteryReaderOrchestrator(gatt, classic);

        var res = await orchestrator.ReadAllAsync(CancellationToken.None);

        Assert.Single(res);
        Assert.Equal("c1", res[0].DeviceId);
    }

    [Fact]
    public async Task CaseInsensitiveDuplicate_GattWins()
    {
        var gatt = new StaticReader(new[] { new DeviceBatteryInfo("d1", "Gatt", 80) });
        var classic = new StaticReader(new[] { new DeviceBatteryInfo("D1", "Classic", 20) });
        var orchestrator = new BatteryReaderOrchestrator(gatt, classic);

        var res = await orchestrator.ReadAllAsync(CancellationToken.None);

        Assert.Single(res);
        Assert.Equal("Gatt", res[0].Name);
        Assert.Equal(80, res[0].Battery);
    }

    [Fact]
    public async Task NullReturningReader_IsTolerated()
    {
        var gatt = new NullReader();
        var classic = new StaticReader(new[] { new DeviceBatteryInfo("c1", "Classic", 33) });
        var orchestrator = new BatteryReaderOrchestrator(gatt, classic);

        var res = await orchestrator.ReadAllAsync(CancellationToken.None);

        Assert.Single(res);
        Assert.Equal("c1", res[0].DeviceId);
    }

    [Fact]
    public async Task RecordsSuccessInCapabilityCache()
    {
        var cache = new DeviceCapabilityCache();
        var gatt = new StaticReader(new[] { new DeviceBatteryInfo("g1", "Gatt", 77) });
        var classic = new StaticReader(new DeviceBatteryInfo[0]);
        var orchestrator = new BatteryReaderOrchestrator(gatt, classic, cache);

        var res = await orchestrator.ReadAllAsync(CancellationToken.None);

        Assert.True(cache.TryGetSuccess("g1"));
    }

    [Fact]
    public async Task ReadAllAsync_ThrowsWhenCancellationRequested()
    {
        var gatt = new StaticReader(new[] { new DeviceBatteryInfo("g1", "Gatt", 60) });
        var classic = new StaticReader(new DeviceBatteryInfo[0]);
        var orchestrator = new BatteryReaderOrchestrator(gatt, classic);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => orchestrator.ReadAllAsync(cts.Token));
    }

    [Fact]
    public async Task MergeUniqueDevices_ReturnsAllUniqueDevices()
    {
        var gatt = new StaticReader(new[] { new DeviceBatteryInfo("g1", "Gatt", 1) });
        var classic = new StaticReader(new[] { new DeviceBatteryInfo("c1", "Classic", 2) });
        var orchestrator = new BatteryReaderOrchestrator(gatt, classic);

        var res = await orchestrator.ReadAllAsync(CancellationToken.None);

        var ids = new HashSet<string>(res.ConvertAll(d => d.DeviceId), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("g1", ids);
        Assert.Contains("c1", ids);
        Assert.Equal(2, res.Count);
    }
}
