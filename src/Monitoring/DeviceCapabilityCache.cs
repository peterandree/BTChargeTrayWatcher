using System;
using System.Collections.Generic;

namespace BTChargeTrayWatcher.Monitoring;

/// <summary>
/// Simple success-only capability cache used to remember which protocols worked for a device.
/// Thread-safe for basic usage in the orchestrator.
/// </summary>
public sealed class DeviceCapabilityCache
{
    private readonly Dictionary<string, DateTime> _lastSuccess = new();
    private readonly object _lock = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(5);

    public bool TryGetSuccess(string deviceId)
    {
        lock (_lock)
        {
            if (_lastSuccess.TryGetValue(deviceId, out var ts))
            {
                if (DateTime.UtcNow - ts < _ttl) return true;
                _lastSuccess.Remove(deviceId);
            }
            return false;
        }
    }

    public void RecordSuccess(string deviceId)
    {
        lock (_lock)
        {
            _lastSuccess[deviceId] = DateTime.UtcNow;
        }
    }

    public void InvalidateAll()
    {
        lock (_lock)
        {
            _lastSuccess.Clear();
        }
    }
}
