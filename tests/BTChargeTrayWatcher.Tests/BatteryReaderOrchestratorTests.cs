using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class BatteryReaderOrchestratorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    // Delegate-based stubs — no IBatteryReader required.
    private static Func<CancellationToken, Task<List<DeviceBatteryInfo>>>
        ClassicStub(List<DeviceBatteryInfo> results, Action? onCall = null) =>
            _ => { onCall?.Invoke(); return Task.FromResult(results); };

    private static Func<CancellationToken, Task<List<DeviceBatteryInfo>>>
        ClassicStubCounting(List<DeviceBatteryInfo> results, ref int counter)
    {
        // ref locals cannot be captured; use a 1-element array as a mutable box.
        var box = new int[1];
        return ct =>
        {
            box[0]++;
            counter = box[0];
            return Task.FromResult(results);
        };
    }

    private static Func<CancellationToken, Task<List<DeviceBatteryInfo>>>
        ClassicThrows() =>
            _ => throw new InvalidOperationException("Classic fault");

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

    // ── Classic fallback ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Classic_results_returned_when_no_BLE_devices()
    {
        var readClassic = ClassicStub([
            new DeviceBatteryInfo("classic-1", "Headphones", 75),
            new DeviceBatteryInfo("classic-2", "Keyboard",   45)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager, readClassic, new DeviceCapabilityCache());

        var results = await orchestrator.ReadAllAsync(
            [ClassicDevice("dev-1", "Some Device")],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Name == "Headphones");
        Assert.Contains(results, r => r.Name == "Keyboard");
    }

    [Fact]
    public async Task Classic_reader_fault_returns_empty_without_throwing()
    {
        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager, ClassicThrows(), new DeviceCapabilityCache());

        var results = await orchestrator.ReadAllAsync(
            [], TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Classic_reader_always_called_even_with_BLE_devices()
    {
        int callCount = 0;
        var readClassic = ClassicStubCounting([], ref callCount);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager, readClassic, new DeviceCapabilityCache());

        await orchestrator.ReadAllAsync(
            [BleDevice("ble-1", "Mouse")],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, callCount);
    }

    // ── Merge: name-based dedup ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Classic_device_with_same_name_as_GATT_device_is_deduplicated()
    {
        var cache = new DeviceCapabilityCache();
        cache.RecordFailure("ble-1");

        var readClassic = ClassicStub([
            new DeviceBatteryInfo("classic-1", "Headphones", 60),
            new DeviceBatteryInfo("classic-2", "Keyboard",   30)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(gattManager, readClassic, cache);

        var results = await orchestrator.ReadAllAsync(
            [BleDevice("ble-1", "Headphones")],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
    }

    // ── Capability cache integration ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GATT_not_attempted_for_non_BLE_devices()
    {
        var cache = new DeviceCapabilityCache();

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager, ClassicStub([]), cache);

        await orchestrator.ReadAllAsync(
            [ClassicDevice("classic-1", "Headphones")],
            TestContext.Current.CancellationToken);

        Assert.Null(cache.GetKnownSource("classic-1"));
    }

    // ── IsConnected skipping (#78) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disconnected_BLE_device_skipped_for_GATT_read()
    {
        var cache = new DeviceCapabilityCache();

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager, ClassicStub([]), cache);

        var results = await orchestrator.ReadAllAsync(
            [new WatchedDevice("ble-1", "Sleeping Mouse", IsBle: true, IsConnected: false)],
            TestContext.Current.CancellationToken);

        Assert.Null(cache.GetKnownSource("ble-1"));
        Assert.Empty(results);
    }

    [Fact]
    public async Task Connected_BLE_device_still_attempts_GATT()
    {
        var cache = new DeviceCapabilityCache();

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager, ClassicStub([]), cache);

        await orchestrator.ReadAllAsync(
            [new WatchedDevice("ble-1", "Active Mouse", IsBle: true, IsConnected: true)],
            TestContext.Current.CancellationToken);

        Assert.False(cache.ShouldAttempt("ble-1"));
    }

    [Fact]
    public async Task Classic_device_not_affected_by_IsConnected_default()
    {
        var readClassic = ClassicStub([
            new DeviceBatteryInfo("classic-1", "Keyboard", 80)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager, readClassic, new DeviceCapabilityCache());

        var results = await orchestrator.ReadAllAsync(
            [ClassicDevice("classic-1", "Keyboard")],
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Keyboard", results[0].Name);
    }

    [Fact]
    public async Task Empty_watch_list_returns_only_classic_results()
    {
        var readClassic = ClassicStub([
            new DeviceBatteryInfo("classic-1", "Speaker", 80)
        ]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager, readClassic, new DeviceCapabilityCache());

        var results = await orchestrator.ReadAllAsync(
            [], TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Speaker", results[0].Name);
    }

    // ── ADR-016: category filtering on production path ─────────────────────────────────────

    [Fact]
    public async Task Allowed_category_passes_through_on_production_path()
    {
        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager,
            ClassicStub([Device("classic-1", "Headphones", DeviceCategory.Audio)]),
            new DeviceCapabilityCache(),
            FilterEnabled());

        var results = await orchestrator.ReadAllAsync(
            [], TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Headphones", results[0].Name);
    }

    [Fact]
    public async Task Unknown_category_passes_through_when_filter_enabled_on_production_path()
    {
        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager,
            ClassicStub([Device("classic-1", "Mystery Device", DeviceCategory.Unknown)]),
            new DeviceCapabilityCache(),
            FilterEnabled());

        var results = await orchestrator.ReadAllAsync(
            [], TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Mystery Device", results[0].Name);
    }

    [Fact]
    public async Task Non_allowed_category_is_filtered_out_on_production_path()
    {
        var unknownCategory = (DeviceCategory)99;

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager,
            ClassicStub([Device("classic-1", "Smart Fridge", unknownCategory)]),
            new DeviceCapabilityCache(),
            FilterEnabled());

        var results = await orchestrator.ReadAllAsync(
            [], TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Category_override_bypasses_filter_on_production_path()
    {
        var unknownCategory = (DeviceCategory)99;
        var settings = FilterEnabled();
        settings.SetCategoryFilterOverrides(["classic-1"]);

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager,
            ClassicStub([Device("classic-1", "Smart Fridge", unknownCategory)]),
            new DeviceCapabilityCache(),
            settings);

        var results = await orchestrator.ReadAllAsync(
            [], TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Smart Fridge", results[0].Name);
    }

    [Fact]
    public async Task Disabled_filter_passes_all_categories_on_production_path()
    {
        var unknownCategory = (DeviceCategory)99;

        using var gattManager = new GattConnectionManager(1);
        var orchestrator = new BatteryReaderOrchestrator(
            gattManager,
            ClassicStub([
                Device("classic-1", "Headphones",  DeviceCategory.Audio),
                Device("classic-2", "Smart Fridge", unknownCategory)
            ]),
            new DeviceCapabilityCache(),
            FilterDisabled());

        var results = await orchestrator.ReadAllAsync(
            [], TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
    }
}
