using System;

namespace BTChargeTrayWatcher.Monitoring;

public static class PhysicalDeviceIdentityResolver
{
    /// <summary>
    /// Resolve a stable identity for a device using ContainerId, MAC, or DeviceId fallback.
    /// </summary>
    public static string Resolve(string? containerId, string? macAddress, string deviceId)
    {
        if (!string.IsNullOrWhiteSpace(containerId)) return containerId!;
        if (!string.IsNullOrWhiteSpace(macAddress)) return macAddress!;
        // Last resort: deviceId hash to keep a stable short id
        var hash = deviceId.GetHashCode();
        return $"DEV_{Math.Abs(hash):X8}";
    }
}
