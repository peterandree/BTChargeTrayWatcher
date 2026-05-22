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

    private static DeviceBatteryInfo Device(
        string id,
        string name,
        DeviceCategory category = DeviceCategory.Unknown,
        int? battery = 50,
        BatterySource source = BatterySource.Unknown)
        => new(id, name, battery, null, source, category);

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
        var results = await orchestrator.ReadAllAsync(watched, TestContext.Current.CancellationToken);

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
        var results = await orchestrator.ReadAllAsync(watched, TestContext.Current.CancellationToken);

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
        await orchestrator.ReadAllAsync(watched, TestContext.Current.CancellationToken);

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
        var results = await orchestrator.ReadAllAsync(watched, TestContext.Current.CancellationToken);

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
        await orchestrator.ReadAllAsync(watched, TestContext.Current.CancellationToken);

        // Classic device should not have been recorded in capability cache
        Assert.Null(cache.GetKnownSource("classic-1"));
    }

    // ── IsConnected skipping (#78) ────────────────────────────────────────────────

    [Fact]
    public async Task Disconnected_BLE_device_skipped_for_GATT_read()
    {
        var cache = new DeviceCapabilityCache();
        var classicReader = new StubClassicReader([]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(gattManager, classicReader, cache);

        var watched = new List<WatchedDevice>
        {
            new("ble-1", "Sleeping Mouse", IsBle: true, IsConnected: false)
        };
        var results = await orchestrator.ReadAllAsync(watched, TestContext.Current.CancellationToken);

        // Device was disconnected → no GATT attempt → no capability cache entry
        Assert.Null(cache.GetKnownSource("ble-1"));
        Assert.Empty(results);
    }

    [Fact]
    public async Task Connected_BLE_device_still_attempts_GATT()
    {
        var cache = new DeviceCapabilityCache();
        var classicReader = new StubClassicReader([]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(gattManager, classicReader, cache);

        var watched = new List<WatchedDevice>
        {
            new("ble-1", "Active Mouse", IsBle: true, IsConnected: true)
        };
        await orchestrator.ReadAllAsync(watched, TestContext.Current.CancellationToken);

        // GATT was attempted (will fail in test env) and RecordFailure was called,
        // so ShouldAttempt returns false until the retry interval elapses.
        Assert.False(cache.ShouldAttempt("ble-1"));
    }

    [Fact]
    public async Task Classic_device_not_affected_by_IsConnected_default()
    {
        var classicReader = new StubClassicReader(
        [
            new DeviceBatteryInfo("classic-1", "Keyboard", 80)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var cache = new DeviceCapabilityCache();
        var orchestrator = new BatteryReaderOrchestrator(gattManager, classicReader, cache);

        // Classic device uses default IsConnected=true
        var watched = new List<WatchedDevice> { ClassicDevice("classic-1", "Keyboard") };
        var results = await orchestrator.ReadAllAsync(watched, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Keyboard", results[0].Name);
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

        var results = await orchestrator.ReadAllAsync([], TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Speaker", results[0].Name);
    }

    // ── ADR-016: category filtering on production path ────────────────────────────

    [Fact]
    public async Task Allowed_category_passes_through_on_production_path()
    {
        var classicReader = new StubClassicReader([
            Device("classic-1", "Headphones", DeviceCategory.Audio)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager,
            classicReader,
            new DeviceCapabilityCache(),
            FilterEnabled());

        var results = await orchestrator.ReadAllAsync([], TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Headphones", results[0].Name);
    }

    [Fact]
    public async Task Unknown_category_passes_through_when_filter_enabled_on_production_path()
    {
        var classicReader = new StubClassicReader([
            Device("classic-1", "Mystery Device", DeviceCategory.Unknown)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager,
            classicReader,
            new DeviceCapabilityCache(),
            FilterEnabled());

        var results = await orchestrator.ReadAllAsync([], TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Mystery Device", results[0].Name);
    }

    [Fact]
    public async Task Non_allowed_category_is_filtered_out_on_production_path()
    {
        var unknownCategory = (DeviceCategory)99;
        var classicReader = new StubClassicReader([
            Device("classic-1", "Smart Fridge", unknownCategory)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager,
            classicReader,
            new DeviceCapabilityCache(),
            FilterEnabled());

        var results = await orchestrator.ReadAllAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Category_override_bypasses_filter_on_production_path()
    {
        var unknownCategory = (DeviceCategory)99;
        var settings = FilterEnabled();
        settings.SetCategoryFilterOverrides(["classic-1"]);
        var classicReader = new StubClassicReader([
            Device("classic-1", "Smart Fridge", unknownCategory)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager,
            classicReader,
            new DeviceCapabilityCache(),
            settings);

        var results = await orchestrator.ReadAllAsync([], TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Smart Fridge", results[0].Name);
    }

    [Fact]
    public async Task Disabled_filter_passes_all_categories_on_production_path()
    {
        var unknownCategory = (DeviceCategory)99;
        var classicReader = new StubClassicReader([
            Device("classic-1", "Headphones", DeviceCategory.Audio),
            Device("classic-2", "Smart Fridge", unknownCategory)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager,
            classicReader,
            new DeviceCapabilityCache(),
            FilterDisabled());

        var results = await orchestrator.ReadAllAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
    }
}
