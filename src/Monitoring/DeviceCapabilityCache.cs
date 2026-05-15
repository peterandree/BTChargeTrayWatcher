namespace BTChargeTrayWatcher;

/// <summary>
/// Caches confirmed battery-reading capabilities per physical device.
/// Only successful reads are cached as "known good"; failures are retried
/// after <see cref="_retryDelay"/> elapses. Thread-safe.
/// </summary>
internal sealed class DeviceCapabilityCache
{
    private readonly Dictionary<string, DeviceCapabilities> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly TimeSpan _retryDelay;
    private readonly Func<DateTimeOffset> _clock;

    internal DeviceCapabilityCache(TimeSpan retryDelay, Func<DateTimeOffset>? clock = null)
    {
        _retryDelay = retryDelay;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal DeviceCapabilityCache()
        : this(TimeSpan.FromMinutes(2)) { }

    /// <summary>Records a successful battery read for <paramref name="deviceId"/>.</summary>
    internal void RecordSuccess(string deviceId, BatterySource source)
    {
        lock (_lock)
        {
            _cache[deviceId] = new DeviceCapabilities(
                SupportsSource: source,
                LastSuccess: _clock(),
                LastFailure: null,
                FailureCount: 0);
        }
    }

    /// <summary>Records a failed battery read attempt for <paramref name="deviceId"/>.</summary>
    internal void RecordFailure(string deviceId)
    {
        lock (_lock)
        {
            int failures = _cache.TryGetValue(deviceId, out var existing)
                ? existing.FailureCount
                : 0;
            _cache[deviceId] = new DeviceCapabilities(
                SupportsSource: null,
                LastSuccess: existing?.LastSuccess,
                LastFailure: _clock(),
                FailureCount: failures + 1);
        }
    }

    /// <summary>
    /// Returns <c>true</c> if a battery read should be attempted for <paramref name="deviceId"/>:
    /// either unknown, previously successful, or retry delay has elapsed since last failure.
    /// </summary>
    internal bool ShouldAttempt(string deviceId)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(deviceId, out var caps)) return true;
            if (caps.SupportsSource is not null) return true;
            if (caps.LastFailure is null) return true;
            return (_clock() - caps.LastFailure.Value) >= _retryDelay;
        }
    }

    /// <summary>Returns the known battery source for a device, or <c>null</c> if unknown.</summary>
    internal BatterySource? GetKnownSource(string deviceId)
    {
        lock (_lock)
        {
            return _cache.TryGetValue(deviceId, out var caps) ? caps.SupportsSource : null;
        }
    }

    /// <summary>Removes a single device from the cache.</summary>
    internal void Invalidate(string deviceId)
    {
        lock (_lock) { _cache.Remove(deviceId); }
    }

    /// <summary>Clears all cached capabilities (e.g. on sleep/resume).</summary>
    internal void InvalidateAll()
    {
        lock (_lock) { _cache.Clear(); }
    }

    private sealed record DeviceCapabilities(
        BatterySource? SupportsSource,
        DateTimeOffset? LastSuccess,
        DateTimeOffset? LastFailure,
        int FailureCount);
}
