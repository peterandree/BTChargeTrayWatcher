using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class DeviceCapabilityCacheTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly TimeSpan _retryDelay = TimeSpan.FromMinutes(2);

    private DeviceCapabilityCache CreateCache() => new(_retryDelay, () => _now);

    // ── ShouldAttempt ─────────────────────────────────────────────────────────────

    [Fact]
    public void Unknown_device_returns_ShouldAttempt_true()
    {
        var cache = CreateCache();
        Assert.True(cache.ShouldAttempt("device-1"));
    }

    [Fact]
    public void Successful_device_always_returns_ShouldAttempt_true()
    {
        var cache = CreateCache();
        cache.RecordSuccess("device-1", BatterySource.Gatt);
        Assert.True(cache.ShouldAttempt("device-1"));
    }

    [Fact]
    public void Failed_device_returns_ShouldAttempt_false_within_retry_delay()
    {
        var cache = CreateCache();
        cache.RecordFailure("device-1");

        _now += TimeSpan.FromSeconds(30);
        Assert.False(cache.ShouldAttempt("device-1"));
    }

    [Fact]
    public void Failed_device_returns_ShouldAttempt_true_after_retry_delay()
    {
        var cache = CreateCache();
        cache.RecordFailure("device-1");

        _now += _retryDelay;
        Assert.True(cache.ShouldAttempt("device-1"));
    }

    // ── RecordSuccess / RecordFailure ──────────────────────────────────────────────

    [Fact]
    public void RecordSuccess_stores_known_source()
    {
        var cache = CreateCache();
        cache.RecordSuccess("device-1", BatterySource.Classic);
        Assert.Equal(BatterySource.Classic, cache.GetKnownSource("device-1"));
    }

    [Fact]
    public void RecordSuccess_clears_previous_failure_state()
    {
        var cache = CreateCache();
        cache.RecordFailure("device-1");
        cache.RecordSuccess("device-1", BatterySource.Gatt);

        Assert.True(cache.ShouldAttempt("device-1"));
        Assert.Equal(BatterySource.Gatt, cache.GetKnownSource("device-1"));
    }

    [Fact]
    public void RecordFailure_increments_failure_count()
    {
        var cache = CreateCache();
        cache.RecordFailure("device-1");
        cache.RecordFailure("device-1");

        // Still blocked within retry delay
        _now += TimeSpan.FromSeconds(30);
        Assert.False(cache.ShouldAttempt("device-1"));
    }

    [Fact]
    public void Unknown_device_returns_null_known_source()
    {
        var cache = CreateCache();
        Assert.Null(cache.GetKnownSource("device-1"));
    }

    // ── Invalidation ──────────────────────────────────────────────────────────────

    [Fact]
    public void Invalidate_removes_single_device()
    {
        var cache = CreateCache();
        cache.RecordSuccess("device-1", BatterySource.Gatt);
        cache.RecordSuccess("device-2", BatterySource.Classic);

        cache.Invalidate("device-1");

        Assert.Null(cache.GetKnownSource("device-1"));
        Assert.Equal(BatterySource.Classic, cache.GetKnownSource("device-2"));
    }

    [Fact]
    public void InvalidateAll_clears_entire_cache()
    {
        var cache = CreateCache();
        cache.RecordSuccess("device-1", BatterySource.Gatt);
        cache.RecordSuccess("device-2", BatterySource.Classic);

        cache.InvalidateAll();

        Assert.Null(cache.GetKnownSource("device-1"));
        Assert.Null(cache.GetKnownSource("device-2"));
    }

    [Fact]
    public void Invalidated_failure_allows_immediate_retry()
    {
        var cache = CreateCache();
        cache.RecordFailure("device-1");

        // Still blocked
        Assert.False(cache.ShouldAttempt("device-1"));

        cache.Invalidate("device-1");
        Assert.True(cache.ShouldAttempt("device-1"));
    }

    // ── Case insensitivity ──────────────────────────────────────────────────────────

    [Fact]
    public void Device_id_lookup_is_case_insensitive()
    {
        var cache = CreateCache();
        cache.RecordSuccess("Device-1", BatterySource.Gatt);

        Assert.Equal(BatterySource.Gatt, cache.GetKnownSource("device-1"));
        Assert.True(cache.ShouldAttempt("DEVICE-1"));
    }
}
