namespace BTChargeTrayWatcher;

/// <summary>
/// Deduplicates Bluetooth device identities using ContainerId as primary key
/// and MAC address as fallback. Handles Random Private Address (RPA) changes
/// by updating the MAC when a known ContainerId reappears with a new address.
/// Thread-safe.
/// </summary>
internal sealed class PhysicalDeviceIdentityResolver
{
    private readonly Dictionary<string, PhysicalDevice> _byContainerId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PhysicalDevice> _byMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Resolves a stable physical device identity from potentially multiple DeviceInformation IDs.
    /// Uses ContainerId as primary key, MAC address as fallback, deviceId as last resort.
    /// </summary>
    internal string Resolve(string deviceId, string? containerId, string? macAddress)
    {
        lock (_lock)
        {
            // Try ContainerId first (handles RPA)
            if (!string.IsNullOrEmpty(containerId) &&
                _byContainerId.TryGetValue(containerId, out var byContainer))
            {
                byContainer.DeviceIds.Add(deviceId);
                if (!string.IsNullOrEmpty(macAddress))
                {
                    byContainer.MacAddress = macAddress;
                    _byMac[macAddress] = byContainer;
                }
                return byContainer.PhysicalId;
            }

            // Try MAC as fallback
            if (!string.IsNullOrEmpty(macAddress) &&
                _byMac.TryGetValue(macAddress, out var byMac))
            {
                byMac.DeviceIds.Add(deviceId);
                if (!string.IsNullOrEmpty(containerId))
                {
                    byMac.ContainerId = containerId;
                    _byContainerId[containerId] = byMac;
                }
                return byMac.PhysicalId;
            }

            // New device — use ContainerId > MAC > DeviceId as stable identity
            var physicalId = containerId ?? macAddress ?? deviceId;
            var device = new PhysicalDevice
            {
                PhysicalId = physicalId,
                ContainerId = containerId,
                MacAddress = macAddress,
                DeviceIds = [deviceId]
            };

            if (!string.IsNullOrEmpty(containerId)) _byContainerId[containerId] = device;
            if (!string.IsNullOrEmpty(macAddress)) _byMac[macAddress] = device;

            return physicalId;
        }
    }

    /// <summary>Removes a device ID from the resolver. If no device IDs remain, the physical device is removed.</summary>
    internal void Remove(string deviceId)
    {
        lock (_lock)
        {
            PhysicalDevice? toRemove = null;
            foreach (var dev in _byContainerId.Values)
            {
                if (dev.DeviceIds.Remove(deviceId) && dev.DeviceIds.Count == 0)
                {
                    toRemove = dev;
                    break;
                }
            }

            if (toRemove is null)
            {
                foreach (var dev in _byMac.Values)
                {
                    if (dev.DeviceIds.Remove(deviceId) && dev.DeviceIds.Count == 0)
                    {
                        toRemove = dev;
                        break;
                    }
                }
            }

            if (toRemove is not null)
            {
                if (toRemove.ContainerId is not null) _byContainerId.Remove(toRemove.ContainerId);
                if (toRemove.MacAddress is not null) _byMac.Remove(toRemove.MacAddress);
            }
        }
    }

    /// <summary>Clears all tracked device identities (e.g. on sleep/resume).</summary>
    internal void Clear()
    {
        lock (_lock)
        {
            _byContainerId.Clear();
            _byMac.Clear();
        }
    }

    private sealed class PhysicalDevice
    {
        public required string PhysicalId { get; init; }
        public string? ContainerId { get; set; }
        public string? MacAddress { get; set; }
        public required HashSet<string> DeviceIds { get; init; }
    }
}
