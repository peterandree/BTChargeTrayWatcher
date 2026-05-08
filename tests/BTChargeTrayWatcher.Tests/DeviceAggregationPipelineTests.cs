using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class DeviceAggregationPipelineTests
{
    // ── Stub reader ─────────────────────────────────────────────────────────────────────

    private sealed class StubReader(IReadOnlyList<DeviceBatteryInfo> results) : IBatteryReader
    {
        public Task<IReadOnlyList<DeviceBatteryInfo>> ReadAllAsync(CancellationToken ct)
            => Task.FromResult(results);
    }

    private sealed class ThrowingReader : IBatteryReader
    {
        public Task<IReadOnlyList<DeviceBatteryInfo>> ReadAllAsync(CancellationToken ct)
            => throw new InvalidOperationException("reader fault");
    }

    private static DeviceBatteryInfo Device(string id, string name, int? battery = 50)
        => new(id, name, battery, null);

    // ── Basic merge ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_all_devices_from_both_readers()
    {
        var gatt    = new StubReader([Device("id1", "Headphones")]);
        var classic = new StubReader([Device("id2", "Keyboard")]);
        var pipeline = new DeviceAggregationPipeline(gatt, classic, null);

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Both_readers_empty_returns_empty_list()
    {
        var gatt    = new StubReader([]);
        var classic = new StubReader([]);
        var pipeline = new DeviceAggregationPipeline(gatt, classic, null);

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Deduplication (GATT wins) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_device_id_is_deduplicated_GATT_entry_wins()
    {
        var gattDevice    = Device("id1", "Headphones-GATT",    75);
        var classicDevice = Device("id1", "Headphones-Classic", 50);

        var gatt    = new StubReader([gattDevice]);
        var classic = new StubReader([classicDevice]);
        var pipeline = new DeviceAggregationPipeline(gatt, classic, null);

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Headphones-GATT", result[0].Name);
        Assert.Equal(75, result[0].Battery);
    }

    [Fact]
    public async Task Deduplication_is_case_insensitive_on_device_id()
    {
        var gatt    = new StubReader([Device("ID1", "A")]);
        var classic = new StubReader([Device("id1", "B")]);
        var pipeline = new DeviceAggregationPipeline(gatt, classic, null);

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Single(result);
    }

    // ── Fault isolation ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Throwing_GATT_reader_still_returns_classic_results()
    {
        var classic = new StubReader([Device("id2", "Keyboard")]);
        var pipeline = new DeviceAggregationPipeline(new ThrowingReader(), classic, null);

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Keyboard", result[0].Name);
    }

    [Fact]
    public async Task Throwing_classic_reader_still_returns_GATT_results()
    {
        var gatt = new StubReader([Device("id1", "Headphones")]);
        var pipeline = new DeviceAggregationPipeline(gatt, new ThrowingReader(), null);

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Headphones", result[0].Name);
    }

    [Fact]
    public async Task Both_readers_throwing_returns_empty_list()
    {
        var pipeline = new DeviceAggregationPipeline(
            new ThrowingReader(), new ThrowingReader(), null);

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── onDeviceFound callback ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OnDeviceFound_called_for_each_unique_device_when_raiseDeviceFound_true()
    {
        var gatt    = new StubReader([Device("id1", "Headphones")]);
        var classic = new StubReader([Device("id2", "Keyboard")]);

        var found = new List<string>();
        var pipeline = new DeviceAggregationPipeline(gatt, classic,
            (id, name, _) => found.Add(id));

        await pipeline.ReadMergedAsync(true, CancellationToken.None);

        Assert.Contains("id1", found);
        Assert.Contains("id2", found);
    }

    [Fact]
    public async Task OnDeviceFound_not_called_when_raiseDeviceFound_false()
    {
        var gatt    = new StubReader([Device("id1", "Headphones")]);
        var classic = new StubReader([Device("id2", "Keyboard")]);

        var found = new List<string>();
        var pipeline = new DeviceAggregationPipeline(gatt, classic,
            (id, name, _) => found.Add(id));

        await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Empty(found);
    }

    [Fact]
    public async Task OnDeviceFound_called_only_once_for_duplicate_device_id()
    {
        var gatt    = new StubReader([Device("id1", "A")]);
        var classic = new StubReader([Device("id1", "B")]);

        var found = new List<string>();
        var pipeline = new DeviceAggregationPipeline(gatt, classic,
            (id, _, _) => found.Add(id));

        await pipeline.ReadMergedAsync(true, CancellationToken.None);

        Assert.Single(found);
    }
}
