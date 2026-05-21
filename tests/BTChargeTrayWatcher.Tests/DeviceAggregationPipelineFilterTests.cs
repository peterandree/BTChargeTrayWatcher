using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Tests for ADR-016 category filter logic in <see cref="DeviceAggregationPipeline"/>.
/// </summary>
public sealed class DeviceAggregationPipelineFilterTests
{
    private sealed class StubReader(List<DeviceBatteryInfo> results) : IBatteryReader
    {
        public Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken ct)
            => Task.FromResult(results);
    }

    private static DeviceBatteryInfo Device(
        string id,
        string name,
        DeviceCategory category = DeviceCategory.Unknown,
        int? battery = 50)
        => new(id, name, battery, null, BatterySource.Unknown, category);

    private static ThresholdSettings FilterEnabled()
    {
        var s = new ThresholdSettings();
        s.SetCategoryFilterEnabled(true);
        return s;
    }

    private static ThresholdSettings FilterDisabled()
    {
        var s = new ThresholdSettings();
        s.SetCategoryFilterEnabled(false);
        return s;
    }

    // ── Case 1: known allowed category passes through ─────────────────────────────────

    [Fact]
    public async Task Device_in_allowed_category_Audio_passes_through()
    {
        var gatt = new StubReader([Device("id1", "Headphones", DeviceCategory.Audio)]);
        var pipeline = new DeviceAggregationPipeline(gatt, new StubReader([]), null, FilterEnabled());

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Headphones", result[0].Name);
    }

    // ── Case 2: Unknown category passes through (reader did not classify) ─────────────

    [Fact]
    public async Task Device_with_Unknown_category_passes_through_when_filter_enabled()
    {
        var gatt = new StubReader([Device("id1", "Mystery Device", DeviceCategory.Unknown)]);
        var pipeline = new DeviceAggregationPipeline(gatt, new StubReader([]), null, FilterEnabled());

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Mystery Device", result[0].Name);
    }

    // ── Case 3: known but non-allowed category is filtered out ────────────────────────
    // We introduce a hypothetical non-allowed value by casting an int outside the
    // AllowedCategories set. DeviceCategory has Audio=1, Hid=2, Controller=3 — all
    // three are currently allowed, so we verify the path with a cast to a value
    // that is not Unknown (0) and not in the set.
    // Because the enum is exhaustive over the three "good" values, we simulate a
    // future fourth category via an unchecked cast.

    [Fact]
    public async Task Device_with_non_allowed_category_is_filtered_out()
    {
        // (DeviceCategory)99 is a defined-but-unknown enum value — not in AllowedCategories,
        // not Unknown — representing a future category the filter does not permit.
        var unknownCategory = (DeviceCategory)99;
        var gatt = new StubReader([Device("id1", "Smart Fridge", unknownCategory)]);
        var pipeline = new DeviceAggregationPipeline(gatt, new StubReader([]), null, FilterEnabled());

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Case 4: device overridden by ID bypasses filter ───────────────────────────────

    [Fact]
    public async Task Device_overridden_by_ID_passes_through_despite_non_allowed_category()
    {
        var unknownCategory = (DeviceCategory)99;
        var settings = FilterEnabled();
        settings.SetCategoryFilterOverrides(["id-fridge"]);

        var gatt = new StubReader([Device("id-fridge", "Smart Fridge", unknownCategory)]);
        var pipeline = new DeviceAggregationPipeline(gatt, new StubReader([]), null, settings);

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Smart Fridge", result[0].Name);
    }

    // ── Case 5: filter disabled — all categories pass ─────────────────────────────────

    [Fact]
    public async Task Filter_disabled_passes_all_categories_including_non_allowed()
    {
        var unknownCategory = (DeviceCategory)99;
        var gatt = new StubReader([
            Device("id1", "Headphones", DeviceCategory.Audio),
            Device("id2", "Smart Fridge", unknownCategory),
        ]);
        var pipeline = new DeviceAggregationPipeline(gatt, new StubReader([]), null, FilterDisabled());

        var result = await pipeline.ReadMergedAsync(false, CancellationToken.None);

        Assert.Equal(2, result.Count);
    }
}
