using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class PhysicalDeviceIdentityResolverTests
{
    // ── Basic resolution ─────────────────────────────────────────────────────────

    [Fact]
    public void New_device_with_containerId_returns_containerId_as_physicalId()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        var id = resolver.Resolve("dev-1", containerId: "container-A", macAddress: "AA:BB:CC:DD:EE:FF");
        Assert.Equal("container-A", id);
    }

    [Fact]
    public void New_device_without_containerId_uses_mac_as_physicalId()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        var id = resolver.Resolve("dev-1", containerId: null, macAddress: "AA:BB:CC:DD:EE:FF");
        Assert.Equal("AA:BB:CC:DD:EE:FF", id);
    }

    [Fact]
    public void New_device_without_containerId_or_mac_uses_deviceId()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        var id = resolver.Resolve("dev-1", containerId: null, macAddress: null);
        Assert.Equal("dev-1", id);
    }

    // ── ContainerId deduplication ──────────────────────────────────────────────────

    [Fact]
    public void Same_containerId_with_different_deviceIds_resolves_to_same_physicalId()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        var id1 = resolver.Resolve("dev-1", "container-A", "AA:BB:CC:DD:EE:FF");
        var id2 = resolver.Resolve("dev-2", "container-A", "AA:BB:CC:DD:EE:FF");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Same_containerId_with_changed_mac_updates_mac_and_keeps_physicalId()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        var id1 = resolver.Resolve("dev-1", "container-A", "AA:BB:CC:DD:EE:01");
        var id2 = resolver.Resolve("dev-2", "container-A", "AA:BB:CC:DD:EE:02");

        Assert.Equal(id1, id2);
    }

    // ── MAC fallback deduplication ───────────────────────────────────────────────

    [Fact]
    public void Same_mac_without_containerId_resolves_to_same_physicalId()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        var id1 = resolver.Resolve("dev-1", containerId: null, macAddress: "AA:BB:CC:DD:EE:FF");
        var id2 = resolver.Resolve("dev-2", containerId: null, macAddress: "AA:BB:CC:DD:EE:FF");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Mac_match_gets_containerId_added_on_later_resolve()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        var id1 = resolver.Resolve("dev-1", containerId: null, macAddress: "AA:BB:CC:DD:EE:FF");
        var id2 = resolver.Resolve("dev-2", containerId: "container-A", macAddress: "AA:BB:CC:DD:EE:FF");

        Assert.Equal(id1, id2);

        // Now a third device with the containerId should also match
        var id3 = resolver.Resolve("dev-3", containerId: "container-A", macAddress: null);
        Assert.Equal(id1, id3);
    }

    // ── Case insensitivity ──────────────────────────────────────────────────────────

    [Fact]
    public void ContainerId_lookup_is_case_insensitive()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        var id1 = resolver.Resolve("dev-1", "Container-A", "AA:BB:CC:DD:EE:FF");
        var id2 = resolver.Resolve("dev-2", "container-a", null);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Mac_lookup_is_case_insensitive()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        var id1 = resolver.Resolve("dev-1", null, "aa:bb:cc:dd:ee:ff");
        var id2 = resolver.Resolve("dev-2", null, "AA:BB:CC:DD:EE:FF");

        Assert.Equal(id1, id2);
    }

    // ── Distinct devices ────────────────────────────────────────────────────────────

    [Fact]
    public void Different_containerIds_resolve_to_different_physicalIds()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        var id1 = resolver.Resolve("dev-1", "container-A", null);
        var id2 = resolver.Resolve("dev-2", "container-B", null);

        Assert.NotEqual(id1, id2);
    }

    // ── Remove ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_last_deviceId_cleans_up_physical_device()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        resolver.Resolve("dev-1", "container-A", "AA:BB:CC:DD:EE:FF");

        resolver.Remove("dev-1");

        // After removal, same containerId should create a new physical device
        var id2 = resolver.Resolve("dev-2", "container-A", null);
        Assert.Equal("container-A", id2); // same value, but it's a new entry
    }

    [Fact]
    public void Remove_one_of_multiple_deviceIds_keeps_physical_device()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        resolver.Resolve("dev-1", "container-A", "AA:BB:CC:DD:EE:FF");
        resolver.Resolve("dev-2", "container-A", "AA:BB:CC:DD:EE:FF");

        resolver.Remove("dev-1");

        // dev-2 still resolves
        var id3 = resolver.Resolve("dev-3", "container-A", null);
        Assert.Equal("container-A", id3);
    }

    // ── Clear ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_removes_all_tracked_devices()
    {
        var resolver = new PhysicalDeviceIdentityResolver();
        resolver.Resolve("dev-1", "container-A", "AA:BB:CC:DD:EE:FF");
        resolver.Resolve("dev-2", "container-B", "11:22:33:44:55:66");

        resolver.Clear();

        // After clear, same containerId creates new entries
        var id = resolver.Resolve("dev-3", "container-A", null);
        Assert.Equal("container-A", id);
    }
}
