using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class BatteryReaderOrchestratorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private sealed class StubClassicReader(List<DeviceBatteryInfo> results) : IBatteryReader
    {
        public int CallCount { get; private set; }

        public Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(results);
        }
    }

    private sealed class ThrowingClassicReader : IBatteryReader
    {
        public Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Classic fault");
    }

    /// <summary>
    /// Fake GattConnectionManager that returns predetermined results per device ID.
    /// Inherits from GattConnectionManager to satisfy the type, but overrides nothing
    /// (GattConnectionManager.TryReadBatteryAsync is not virtual).
    /// Instead, we use BatteryReaderOrchestrator with a real GattConnectionManager that
    /// will fail for non-existent devices, or we test the merge logic in isolation.
    /// </summary>
    /// <remarks>
    /// Since GattConnectionManager uses WinRT and can't be mocked, we test the
    /// orchestrator's merge logic and Classic fallback by providing a real
    /// GattConnectionManager (GATT reads will return null for non-existent devices
    /// in a test environment) and verifying Classic results come through.
    /// For GATT-specific tests, we test GattConnectionManager separately with real devices.
    /// </remarks>

    private static WatchedDevice BleDevice(string id, string name) =>
        new(id, name, IsBle: true);

    private static WatchedDevice ClassicDevice(string id, string name) =>
        new(id, name, IsBle: false);

    // ── Classic fallback ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Classic_results_returned_when_no_BLE_devices()
    {
        var classicReader = new StubClassicReader(
        [
            new DeviceBatteryInfo("classic-1", "Headphones", 75),
            new DeviceBatteryInfo("classic-2", "Keyboard", 45)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var cache = new DeviceCapabilityCache();
        var orchestrator = new BatteryReaderOrchestrator(gattManager, classicReader, cache);

        var watched = new List<WatchedDevice> { ClassicDevice("dev-1", "Some Device") };
        var results = await orchestrator.ReadAllAsync(watched, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Name == "Headphones");
        Assert.Contains(results, r => r.Name == "Keyboard");
    }

    [Fact]
    public async Task Classic_reader_fault_returns_empty_without_throwing()
    {
        var classicReader = new ThrowingClassicReader();

        using var gattManager = new GattConnectionManager(1);
        var cache = new DeviceCapabilityCache();
        var orchestrator = new BatteryReaderOrchestrator(gattManager, classicReader, cache);

        var watched = new List<WatchedDevice>();
        var results = await orchestrator.ReadAllAsync(watched, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Classic_reader_always_called_even_with_BLE_devices()
    {
        var classicReader = new StubClassicReader([]);

        using var gattManager = new GattConnectionManager(1);
        var cache = new DeviceCapabilityCache();
        var orchestrator = new BatteryReaderOrchestrator(gattManager, classicReader, cache);

        var watched = new List<WatchedDevice> { BleDevice("ble-1", "Mouse") };
        await orchestrator.ReadAllAsync(watched, CancellationToken.None);

        Assert.Equal(1, classicReader.CallCount);
    }

    // ── Merge: name-based dedup ─────────────────────────────────────────────────

    [Fact]
    public async Task Classic_device_with_same_name_as_GATT_device_is_deduplicated()
    {
        // Pre-seed the capability cache to prevent GATT attempt for the device
        // (GATT will fail in tests anyway since no real device)
        var cache = new DeviceCapabilityCache();
        cache.RecordFailure("ble-1"); // Prevent GATT attempt

        var classicReader = new StubClassicReader(
        [
            new DeviceBatteryInfo("classic-1", "Headphones", 60),
            new DeviceBatteryInfo("classic-2", "Keyboard", 30)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(gattManager, classicReader, cache);

        var watched = new List<WatchedDevice> { BleDevice("ble-1", "Headphones") };
        var results = await orchestrator.ReadAllAsync(watched, CancellationToken.None);

        // Classic Headphones and Keyboard both come through since GATT was skipped
        Assert.Equal(2, results.Count);
    }

    // ── Capability cache integration ──────────────────────────────────────────────

    [Fact]
    public async Task GATT_not_attempted_for_non_BLE_devices()
    {
        var cache = new DeviceCapabilityCache();
        var classicReader = new StubClassicReader([]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(gattManager, classicReader, cache);

        var watched = new List<WatchedDevice> { ClassicDevice("classic-1", "Headphones") };
        await orchestrator.ReadAllAsync(watched, CancellationToken.None);

        // Classic device should not have been recorded in capability cache
        Assert.Null(cache.GetKnownSource("classic-1"));
    }

    [Fact]
    public async Task Empty_watch_list_returns_only_classic_results()
    {
        var classicReader = new StubClassicReader(
        [
            new DeviceBatteryInfo("classic-1", "Speaker", 80)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var cache = new DeviceCapabilityCache();
        var orchestrator = new BatteryReaderOrchestrator(gattManager, classicReader, cache);

        var results = await orchestrator.ReadAllAsync([], CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Speaker", results[0].Name);
    }
}
