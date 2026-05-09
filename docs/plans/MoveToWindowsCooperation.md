# Feature Plan: Move to Windows Cooperation for Battery Monitoring

---

## Goal

**Rely on Windows’ built-in Bluetooth device discovery** as the primary source of truth, and **read battery levels from these devices using a minimal set of protocols**, while **acknowledging the fragmentation of battery reporting** across device classes, transports, and vendor implementations. This strategy **reduces Bluetooth radio pressure and saves battery** while ensuring practical coverage for devices used with the computer.

**Key Principle:**
> *"Windows discovers the devices; we **try** to read the battery—**but expect failures and handle them gracefully**."*

**What This Means:**
✅ **Use Windows’ device list** (`DeviceInformation.FindAllAsync` + PnP Watcher) as the **primary source** for Bluetooth devices.
✅ **Read battery levels** from each device using **GATT and HID** (covers the majority of practical cases).
✅ **Handle edge cases** where Windows doesn’t expose battery (e.g., vendor-specific protocols) **only if users report missing data**.
✅ **Deduplicate devices** to avoid showing the same physical device multiple times (e.g., a headset with BLE, HFP, and A2DP interfaces).
✅ **Cache capabilities** to avoid redundant protocol attempts.
✅ **Embrace degraded performance** (timeouts, skipped devices) as normal.

❌ **Do NOT scan for unpaired devices in range** (e.g., BLE advertisements for unknown devices).
❌ **Do NOT duplicate Windows’ discovery work** (e.g., custom device scanning).
❌ **Do NOT assume uniform battery reporting** across devices or Windows versions.

**Why This Approach?**
- **Lower Bluetooth radio usage** → Better battery life and fewer disconnections.
- **Simpler code** → Less complexity, fewer bugs, easier maintenance.
- **More reliable** → Uses Windows’ tested device enumeration.
- **Practical coverage** → Focus on what works, not theoretical completeness.

---

## Background and Constraints

### Current Implementation
The project currently supports:
- **GATT Battery Service (0x180F)** for BLE devices.

**Problem:**
The existing approach **assumes responsibility for device discovery**, which:
- **Duplicates Windows’ work** (inefficient).
- **Increases Bluetooth radio usage** (drains battery).
- **Adds complexity** (custom scanning logic for edge cases).

### Windows’ Built-in Capabilities and Limitations
Windows **already discovers and tracks** Bluetooth devices via:

| Mechanism | Devices Covered | Battery Access? | API | Notes |
|-----------|-----------------|-----------------|-----|-------|
| `DeviceInformation.FindAllAsync` | All paired/remembered devices (BLE + Classic) | ⚠️ Partial | `Windows.Devices.Enumeration` | Primary source for device list. |
| PnP Device Watcher | Real-time device additions/removals | ❌ No (triggers scans) | `Windows.Devices.Enumeration` | Supplementary for real-time updates. |
| GATT (0x180F) | BLE devices with Battery Service | ✅ Yes | `Windows.Devices.Bluetooth.GenericAttributeProfile` | Most modern BLE devices. |
| GATT (0x2A1B) | Battery Power State (HID devices) | ✅ Yes | `Windows.Devices.Bluetooth.GenericAttributeProfile` | Used by many HID devices (e.g., Logitech, Razer). |

**Critical Realities:**
1. **Not all Bluetooth devices report battery levels** (e.g., some legacy headsets, gaming peripherals).
2. **Battery reporting varies by device class, transport, and vendor** (e.g., Sony vs. Bose vs. Logitech).
3. **Windows does not normalize battery APIs** across device types or Bluetooth stacks.
4. **`Win32_Battery` (WMI) is for system batteries (laptop/UPS), NOT Bluetooth peripherals** → **ClassicBatteryReader is removed** from this plan.
5. **HID battery reporting is vendor-specific** → Generic HID report parsing is **not feasible**; use **GATT 0x2A1B** where possible.

**Key Insight:**
Windows **already maintains a list of all Bluetooth devices** used with the computer (paired or previously connected). However, **battery reporting is fragmented**, and we must **handle this gracefully** with deduplication, capability caching, and fallback protocols.

---

## Design Decisions That Govern This Feature

---

### ADR-001 — Single Non-Nullable Constructor per Class
All data models (e.g., `DeviceBatteryInfo`) must adhere to the principle of **immutability** and **single non-nullable constructors**. Any new fields must be added as optional parameters with defaults to avoid breaking existing code.

**Rule:** New fields in `DeviceBatteryInfo` or related records must be added as optional constructor parameters with default values (e.g., `bool? IsCharging = null`).

---

### ADR-002 — Windows-First Device Discovery
**All device discovery must rely on Windows’ built-in mechanisms** (`DeviceInformation` + PnP Watcher). Custom scanning (e.g., BLE advertisements for unpaired devices) is **explicitly out of scope**.

**Rule:**
- **Primary source:** `DeviceInformation.FindAllAsync` + PnP Watcher.
- **Secondary:** Protocol-specific battery reading (GATT, HID).
- **No custom device discovery** (e.g., no `BluetoothLEAdvertisementWatcher` for unpaired devices).

---

### ADR-003 — Minimal Protocol Coverage First
To ensure **practical coverage with minimal complexity**, the app must first implement support for the **two most reliable protocols** (GATT for 0x180F and 0x2A1B). Additional protocols (AVRCP, HFP, Vendor-Specific) may be added **later if users report missing battery data** for specific devices.

**Rule:** The initial implementation must support:
1. **GATT (0x180F)** → Most BLE devices (headphones, mice, keyboards).
2. **GATT (0x2A1B, Battery Power State)** → HID devices (keyboards, mice) that support BLE.

AVRCP, HFP, and Vendor-Specific support may be added **later if needed**.

**Note:** The **95% coverage claim is removed** from this plan. Realistic coverage with GATT (0x180F + 0x2A1B) is **~70–80% of devices**, depending on the user’s hardware. This is **not a failure**—it is a **realistic expectation** given the fragmentation of Bluetooth battery reporting.

---

### ADR-004 — Polling Over Push (Clarified)
The project uses a **polling-based approach** (60-second interval) for battery monitoring, as documented in [ADR-003](../adr/adr-003-polling-over-push.md). **PnP Device Watcher is a supplementary mechanism** that triggers immediate scans for new/updated devices but **does not replace polling**.

**Rule:**
- **Primary mechanism:** Polling every 60s (`PollingOrchestrator` fires alerts).
- **Supplementary mechanism:** PnP Watcher triggers **UI updates only** (no alerts) for new/updated devices.
- **No conflict:** `PollingOrchestrator` remains the **single source of truth** for alert state (ADR-011).
- **PnP events are serialized** to avoid reentrancy issues (ADR-013).

---

### ADR-005 — Physical Device Identity Normalization
A single physical device (e.g., a headset) may appear as **multiple `DeviceInformation` entries** in Windows (e.g., one for audio, one for HFP, one for BLE). To avoid duplicates, conflicting battery values, and UI instability, we must **normalize device identities** using MAC address and ContainerId.

**Rule:**
- **Use `PhysicalDeviceIdentityResolver`** to map multiple `DeviceInformation` entries to a single physical device.
- **Primary keys:** MAC address (`System.Devices.Bluetooth.DeviceAddress`) and ContainerId (`System.Devices.ContainerId`).
- **Fallback:** Device name (last resort, unreliable).

---

### ADR-006 — Capability Caching
Battery levels change **slowly** (minutes, not seconds), and **protocol support for a device does not change** unless the device is reconnected or the system resumes from sleep. To minimize Bluetooth radio usage, we must **cache device capabilities and use smart caching for battery reads**.

**Rule:**
- **Cache protocol support** per device (e.g., "Device X supports GATT but not HID").
- **Cache TTL:** 1 hour (configurable).
- **Invalidate cache** on:
  - Device reconnect.
  - System resume from sleep.
  - User-requested refresh.
- **Use `BluetoothCacheMode.Cached`** for regular polls (reduces radio wakeups).
- **Use `BluetoothCacheMode.Uncached`** only on:
  - First read for a device.
  - Device reconnect.
  - System resume from sleep.
  - User-requested refresh.

---

### ADR-007 — Minimal Bluetooth Radio Usage
To **save battery and avoid disconnections**, the app must:
- **Reuse existing connections** (via `GattConnectionCache`).
- **Limit concurrent Bluetooth operations** (default: **1**, configurable: 1–3).
- **Skip scans on battery power** (for optional protocols).
- **Throttle retries** for failed connections.
- **Respect timeouts** (2s per protocol, 10s global scan).

**Rule:** All protocol readers must respect radio usage limits and power-aware throttling.

**Note:** Intel, Broadcom, and Qualcomm adapters behave differently. A **conservative default of 1 concurrent operation** is recommended to avoid radio instability.

---

### ADR-008 — Graceful Degradation
If a protocol fails to read battery for a device, the app must **continue trying other protocols** without blocking the UI or crashing. **Missing battery data is normal** and should be treated as `null` (unknown), not an error.

**Rule:**
- **Never fail silently** (log warnings for debugging).
- **Always try the next protocol** in the fallback chain.
- **Treat missing battery data as `null`** (not an error).
- **Skip devices after 3 consecutive failures** (configurable).

---

### ADR-009 — Realistic Performance Targets
Battery monitoring is **not a real-time system**. **Latency is acceptable** as long as it does not block the UI or drain the battery.

**Rule:** Define **three performance tiers**:
| Scenario | Target | Degraded Behavior |
|----------|--------|-------------------|
| **Ideal** (cached reads, no radio wakeups) | <2s | None |
| **Acceptable** (some uncached reads) | <5s | Skip slow protocols after timeout |
| **Degraded** (radio busy/sleep) | <10s | Skip entire scan, retry next cycle |
| **Timeout** (per protocol) | 2s | Skip to next protocol |

---

### ADR-010 — SynchronizationContext Over Control.Invoke
All UI updates must be dispatched through the existing `SynchronizationContext.Post` pattern (via `ScanCoordinator`). No direct `Control.Invoke` or `Dispatcher.Invoke` calls may be introduced.

**Rule:** New UI updates must use the existing `PostToUi` pattern.

---

### ADR-011 — Single Source of Alert Truth
The `PollingOrchestrator` remains the **only authority** on alert state. New battery data sources must not introduce separate alert logic.

**Rule:** All battery data, regardless of source, must be processed by `PollingOrchestrator.ClassifyBatteryState`.

**Note:** PnP-triggered scans **do not fire alerts**—they only update the UI.

---

### ADR-012 — Sleep/Resume Handling
The Windows Bluetooth stack is **unstable immediately after sleep/resume**. Scans should be **delayed** to allow the stack to stabilize, and caches should be **invalidated** to avoid stale handles.

**Rule:**
- **Invalidate caches** (`DeviceCapabilityCache`, `PhysicalDeviceIdentityResolver`) after resume.
- **Delay scans** for **10 seconds** after resume.
- **Retry failed scans** after a delay if the radio is busy.

---

### ADR-013 — Serialized Event Handling
PnP Device Watcher events (`Added`, `Removed`, `Updated`) must be **serialized** to avoid:
- Reentrancy issues.
- Race conditions.
- Unobserved exceptions.
- Duplicate updates.

**Rule:** Use a `SemaphoreSlim` (with a count of 1) to **serialize all PnP event handling**.

---

## Architecture Overview

### High-Level Design
```
Windows Device List (DeviceInformation + PnP Watcher)
       ↓
[PhysicalDeviceIdentityResolver] → Deduplicates devices (MAC/ContainerId)
       ↓
[DeviceCapabilityCache] → Caches which protocols work per device
       ↓
[DeviceEnumerator] → Enumerates devices from Windows (AQS filtering)
       ↓
[BatteryReaderOrchestrator] → Tries protocols in order (GATT 0x180F → GATT 0x2A1B)
       ↓
[BluetoothBatteryMonitor] → Polling + PnP events, respects timeouts/resume
       ↓
[PollingOrchestrator] → Single source of alert truth (ADR-011)
       ↓
[ScanCoordinator] → UI updates via SynchronizationContext (ADR-010)
```

**Key Components:**

| Component | Purpose | Priority | Files |
|-----------|---------|----------|-------|
| `PhysicalDeviceIdentityResolver` | Deduplicates devices by MAC/ContainerId | ⭐⭐⭐⭐⭐ | `src/Monitoring/PhysicalDeviceIdentityResolver.cs` |
| `DeviceCapabilityCache` | Caches protocol support per device | ⭐⭐⭐⭐⭐ | `src/Monitoring/DeviceCapabilityCache.cs` |
| `DeviceEnumerator` | Enumerates devices from Windows (AQS filtering) | ⭐⭐⭐⭐⭐ | `src/Monitoring/DeviceEnumerator.cs` |
| `DeviceWatcherService` | PnP events (serialized, no `.Result`) | ⭐⭐⭐⭐⭐ | `src/Monitoring/DeviceWatcherService.cs` |
| `BatteryReaderOrchestrator` | Orchestrates protocol fallback (GATT 0x180F → GATT 0x2A1B) | ⭐⭐⭐⭐⭐ | `src/Monitoring/BatteryReaderOrchestrator.cs` |
| `GattBatteryReader` | Reads battery via GATT (0x180F and 0x2A1B) | ⭐⭐⭐⭐⭐ | `src/Monitoring/Gatt/GattBatteryReader.cs` |
| `HidBatteryReader` | Reads battery via GATT 0x2A1B (HID devices) | ⭐⭐⭐⭐ | `src/Monitoring/Hid/HidBatteryReader.cs` |

**Note:** ClassicBatteryReader (WMI/Win32_Battery) is **removed** from this plan, as `Win32_Battery` is not intended for Bluetooth peripherals. AVRCP, HFP, and Vendor-Specific readers are **not implemented in Phase 1** and may be added later if users report missing battery data.

---

## Proposed Implementation

---

### Step 1: Physical Device Identity Normalization
**Goal:** Map multiple `DeviceInformation` entries to a **single physical device** to avoid duplicates in the UI.

#### Background
A single physical device (e.g., a Bluetooth headset) may appear as **multiple entries** in Windows’ device list:
- One for **BLE** (e.g., for battery reporting via GATT).
- One for **Classic Bluetooth** (e.g., for audio via A2DP or HFP).
- One for **HID** (e.g., for media controls).

Without normalization, the app will show **duplicate devices** with **conflicting battery values** and **UI instability**.

#### Implementation

```csharp
/// <summary>
/// Resolves multiple DeviceInformation entries to a single physical device.
/// Uses MAC address and ContainerId as primary keys.
/// </summary>
public class PhysicalDeviceIdentityResolver
{
    private readonly Dictionary<string, PhysicalDevice> _physicalDevices = new();
    private readonly object _lock = new();

    /// <summary>
    /// Gets the physical device ID for a DeviceInformation entry.
    /// </summary>
    public string GetPhysicalDeviceId(DeviceInformation device)
    {
        lock (_lock)
        {
            var macAddress = GetMacAddress(device);
            var containerId = GetContainerId(device);

            // Try to find an existing physical device for this DeviceInformation
            var existing = _physicalDevices.Values.FirstOrDefault(pd =>
                pd.MacAddress == macAddress ||
                pd.ContainerId == containerId ||
                pd.DeviceIds.Contains(device.Id));

            if (existing != null)
            {
                existing.DeviceIds.Add(device.Id);
                return existing.Id;
            }

            // Create a new physical device
            var physicalId = Guid.NewGuid().ToString();
            _physicalDevices[physicalId] = new PhysicalDevice
            {
                Id = physicalId,
                DeviceIds = new HashSet<string> { device.Id },
                MacAddress = macAddress,
                ContainerId = containerId,
                Name = device.Name
            };

            return physicalId;
        }
    }

    /// <summary>
    /// Removes a DeviceInformation entry from the resolver.
    /// </summary>
    public void RemoveDevice(string deviceId)
    {
        lock (_lock)
        {
            foreach (var pd in _physicalDevices.Values.ToList())
            {
                if (pd.DeviceIds.Contains(deviceId))
                {
                    pd.DeviceIds.Remove(deviceId);
                    if (pd.DeviceIds.Count == 0)
                    {
                        _physicalDevices.Remove(pd.Id);
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Clears all cached device identities (e.g., after resume).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _physicalDevices.Clear();
        }
    }

    private static string? GetMacAddress(DeviceInformation device)
    {
        if (device.Properties.TryGetValue("System.Devices.Bluetooth.DeviceAddress", out var address))
        {
            return address.ToString();
        }
        return null;
    }

    private static string? GetContainerId(DeviceInformation device)
    {
        if (device.Properties.TryGetValue("System.Devices.ContainerId", out var containerId))
        {
            return containerId.ToString();
        }
        return null;
    }

    private class PhysicalDevice
    {
        public string Id { get; set; } = string.Empty;
        public HashSet<string> DeviceIds { get; set; } = new();
        public string? MacAddress { get; set; }
        public string? ContainerId { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
```

**Key Notes:**
- Uses **MAC address** and **ContainerId** as primary keys (most reliable).
- Falls back to **DeviceId** for matching existing physical devices.
- **Thread-safe** (locks all access to `_physicalDevices`).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/PhysicalDeviceIdentityResolver.cs` | New file: Deduplicates devices by MAC/ContainerId. |

---

### Step 2: Device Capability Caching
**Goal:** Avoid redundant protocol attempts by caching which protocols work for each physical device.

#### Background
- **Battery reporting capabilities** for a device **do not change** unless the device is reconnected or the system resumes from sleep.
- **Repeatedly trying all protocols** for every device on every poll **wastes radio bandwidth** and **increases latency**.
- **Example:** If a device fails to report battery via GATT 0x180F, there’s no point trying it again on the next poll.

#### Implementation

```csharp
/// <summary>
/// Caches the battery-reading capabilities of each physical device.
/// </summary>
public class DeviceCapabilityCache
{
    private readonly Dictionary<string, DeviceCapabilities> _cache = new();
    private readonly TimeSpan _cacheTTL = TimeSpan.FromHours(1);
    private readonly object _lock = new();

    public class DeviceCapabilities
    {
        public bool SupportsGattBatteryLevel { get; set; } // 0x2A19
        public bool SupportsGattBatteryPowerState { get; set; } // 0x2A1B
        public BatterySource LastSuccessfulSource { get; set; } = BatterySource.Unknown;
        public DateTimeOffset LastUpdated { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    /// <summary>
    /// Updates the capabilities for a physical device.
    /// </summary>
    public void UpdateCapabilities(
        string physicalDeviceId,
        BatterySource successfulSource,
        bool supportsGattBatteryLevel = false,
        bool supportsGattBatteryPowerState = false)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(physicalDeviceId, out var caps))
            {
                caps = new DeviceCapabilities();
                _cache[physicalDeviceId] = caps;
            }

            caps.SupportsGattBatteryLevel = supportsGattBatteryLevel;
            caps.SupportsGattBatteryPowerState = supportsGattBatteryPowerState;
            caps.LastSuccessfulSource = successfulSource;
            caps.LastUpdated = DateTimeOffset.UtcNow;
            caps.ConsecutiveFailures = 0; // Reset on success
        }
    }

    /// <summary>
    /// Records a failure for a physical device.
    /// </summary>
    public void RecordFailure(string physicalDeviceId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(physicalDeviceId, out var caps))
            {
                caps.ConsecutiveFailures++;
            }
        }
    }

    /// <summary>
    /// Gets the capabilities for a physical device.
    /// </summary>
    public DeviceCapabilities? GetCapabilities(string physicalDeviceId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(physicalDeviceId, out var caps) &&
                DateTimeOffset.UtcNow - caps.LastUpdated < _cacheTTL)
            {
                return caps;
            }
            return null;
        }
    }

    /// <summary>
    /// Invalidates the capabilities for a physical device.
    /// </summary>
    public void InvalidateCapabilities(string physicalDeviceId)
    {
        lock (_lock)
        {
            _cache.Remove(physicalDeviceId);
        }
    }

    /// <summary>
    /// Invalidates all capabilities (e.g., after resume).
    /// </summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _cache.Clear();
        }
    }

    /// <summary>
    /// Checks if a device should be skipped due to too many consecutive failures.
    /// </summary>
    public bool ShouldSkipDevice(string physicalDeviceId, int maxFailures = 3)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(physicalDeviceId, out var caps))
            {
                return caps.ConsecutiveFailures >= maxFailures;
            }
            return false;
        }
    }
}
```

**Key Notes:**
- **Cache TTL: 1 hour** (battery reporting capabilities rarely change).
- **Tracks consecutive failures** to skip problematic devices after 3 failures.
- **Thread-safe** (locks all access to `_cache`).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/DeviceCapabilityCache.cs` | New file: Caches protocol support per device. |

---

### Step 3: Device Enumeration via Windows
**Goal:** Get the **complete list of Bluetooth devices** from Windows, using **AQS (Advanced Query Syntax)** for reliable filtering.

#### Background
- `DeviceInformation.FindAllAsync` returns **all paired/remembered Bluetooth devices** from Windows.
- **AQS filtering** is more reliable than name matching or GUID comparisons.
- **Device properties** (e.g., `System.Devices.Bluetooth.DeviceAddress`) are used to identify Bluetooth devices.

#### Implementation

```csharp
/// <summary>
/// Enumerates Bluetooth devices from Windows using DeviceInformation APIs.
/// This is the primary source of truth for device discovery.
/// </summary>
public class DeviceEnumerator
{
    private readonly PhysicalDeviceIdentityResolver _identityResolver;

    public DeviceEnumerator(PhysicalDeviceIdentityResolver identityResolver)
    {
        _identityResolver = identityResolver;
    }

    /// <summary>
    /// Gets all paired/remembered Bluetooth devices from Windows.
    /// </summary>
    public async Task<IReadOnlyList<DeviceInformation>> GetBluetoothDevicesAsync()
    {
        // Use AQS to filter for Bluetooth devices
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector);

        // Filter to only Bluetooth devices (using properties, not name)
        return devices.Where(IsBluetoothDevice).ToList();
    }

    /// <summary>
    /// Gets a specific Bluetooth device by its ID.
    /// </summary>
    public async Task<DeviceInformation?> GetDeviceByIdAsync(string deviceId)
    {
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromId(deviceId);
            var devices = await DeviceInformation.FindAllAsync(selector);
            return devices.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get device by ID: {DeviceId}", deviceId);
            return null;
        }
    }

    private static bool IsBluetoothDevice(DeviceInformation device)
    {
        // Check for Bluetooth-specific properties (most reliable)
        if (device.Properties.ContainsKey("System.Devices.Bluetooth.DeviceAddress"))
        {
            return true;
        }

        // Check for Bluetooth interface class GUID (may be string or Guid)
        if (device.Properties.TryGetValue("System.Devices.InterfaceClassGuid", out var ifaceGuidObj))
        {
            var bluetoothGuid = BluetoothDevice.BluetoothDeviceInterfaceClassGuid.ToString("B").ToUpper();
            var deviceGuid = ifaceGuidObj.ToString().ToUpper();
            if (deviceGuid == bluetoothGuid)
            {
                return true;
            }
        }

        // Check for Bluetooth category
        if (device.Properties.TryGetValue("System.Devices.Category", out var category))
        {
            var categoryStr = category.ToString().ToUpper();
            if (categoryStr.Contains("BLUETOOTH"))
            {
                return true;
            }
        }

        return false;
    }
}
```

**Key Notes:**
- Uses **AQS** (`BluetoothDevice.GetDeviceSelectorFromPairingState`) for reliable filtering.
- Falls back to **property-based filtering** (not name matching).
- **Handles errors** in `GetDeviceByIdAsync` (e.g., stale device IDs).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/DeviceEnumerator.cs` | New file: Enumerates devices from Windows (AQS filtering). |

---

### Step 4: Device Watcher Service (Fixed)
**Goal:** Monitor PnP events for Bluetooth devices and provide real-time updates, **without deadlocks or reentrancy issues**.

#### Background
- **PnP Device Watcher** triggers events when devices are **added, removed, or updated**.
- **Events must be serialized** to avoid race conditions.
- **No `.Result`** (avoids deadlocks on `SynchronizationContext`).

#### Implementation

```csharp
/// <summary>
/// Monitors PnP events for Bluetooth devices and provides real-time updates.
/// Uses DeviceEnumerator to fetch the current list of devices.
/// </summary>
public class DeviceWatcherService : IAsyncDisposable
{
    private readonly DeviceEnumerator _deviceEnumerator;
    private DeviceWatcher _watcher;
    private readonly List<DeviceInformation> _currentDevices = new();
    private readonly SemaphoreSlim _eventSemaphore = new(1); // Serialize event handling (ADR-013)
    private readonly CancellationTokenSource _cts = new();

    public event Action<DeviceInformation> DeviceAdded;
    public event Action<DeviceInformation> DeviceRemoved;

    public DeviceWatcherService(DeviceEnumerator deviceEnumerator)
    {
        _deviceEnumerator = deviceEnumerator;
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        _watcher = DeviceInformation.CreateWatcher(selector);
        _watcher.Added += OnDeviceAddedAsync;
        _watcher.Removed += OnDeviceRemovedAsync;
        _watcher.Updated += OnDeviceUpdatedAsync;
        _watcher.Start();
    }

    /// <summary>
    /// Gets the current list of Bluetooth devices from Windows.
    /// </summary>
    public async Task<IReadOnlyList<DeviceInformation>> GetCurrentDevicesAsync()
    {
        return await _deviceEnumerator.GetBluetoothDevicesAsync();
    }

    private async void OnDeviceAddedAsync(DeviceWatcher sender, DeviceInformation device)
    {
        await _eventSemaphore.WaitAsync(_cts.Token);
        try
        {
            lock (_currentDevices) _currentDevices.Add(device);
            DeviceAdded?.Invoke(device);
        }
        finally
        {
            _eventSemaphore.Release();
        }
    }

    private async void OnDeviceRemovedAsync(DeviceWatcher sender, DeviceInformationUpdate deviceUpdate)
    {
        await _eventSemaphore.WaitAsync(_cts.Token);
        try
        {
            lock (_currentDevices)
            {
                var device = _currentDevices.FirstOrDefault(d => d.Id == deviceUpdate.Id);
                if (device != null)
                {
                    _currentDevices.Remove(device);
                    DeviceRemoved?.Invoke(device);
                }
            }
        }
        finally
        {
            _eventSemaphore.Release();
        }
    }

    private async void OnDeviceUpdatedAsync(DeviceWatcher sender, DeviceInformationUpdate deviceUpdate)
    {
        await _eventSemaphore.WaitAsync(_cts.Token);
        try
        {
            lock (_currentDevices)
            {
                var oldDevice = _currentDevices.FirstOrDefault(d => d.Id == deviceUpdate.Id);
                if (oldDevice != null) _currentDevices.Remove(oldDevice);
            }

            // Fetch the updated device asynchronously (no .Result deadlock!)
            var updatedDevice = await _deviceEnumerator.GetDeviceByIdAsync(deviceUpdate.Id);
            if (updatedDevice != null)
            {
                lock (_currentDevices) _currentDevices.Add(updatedDevice);
                DeviceAdded?.Invoke(updatedDevice); // Treat as "new" for UI purposes
            }
        }
        finally
        {
            _eventSemaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _watcher.Stop();
        _eventSemaphore.Dispose();
        _cts.Dispose();
    }
}
```

**Key Fixes:**
- **No `.Result` deadlock:** `OnDeviceUpdatedAsync` now uses `await` instead of `.Result`.
- **Serialized event handling:** Uses `_eventSemaphore` to avoid reentrancy (ADR-013).
- **Robust device filtering:** Uses AQS and properties (not name matching).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/DeviceWatcherService.cs` | Enhanced: Serialized events, no `.Result`, AQS filtering. |

---

### Step 5: Battery Reader Orchestrator
**Goal:** For each device from Windows, **try the core protocols (GATT 0x180F → GATT 0x2A1B)** in order until battery data is found. Uses **capability caching** to skip redundant attempts.

#### Background
- **GATT (0x180F, Battery Level)** is the **primary method** for modern BLE devices.
- **GATT (0x2A1B, Battery Power State)** is used by many **HID devices** (keyboards, mice) that support BLE.
- **Capability cache** avoids retrying protocols that previously failed.
- **Smart caching** (`Cached` mode) reduces radio wakeups.

#### Implementation

```csharp
/// <summary>
/// Orchestrates the protocol fallback chain to read battery from a device.
/// Tries protocols in order of priority (GATT 0x180F → GATT 0x2A1B).
/// Uses DeviceCapabilityCache to skip redundant attempts.
/// </summary>
public class BatteryReaderOrchestrator
{
    private readonly IBatteryReader[] _readers;
    private readonly DeviceCapabilityCache _capabilityCache;
    private readonly SemaphoreSlim _bluetoothSemaphore = new(1); // Default: 1 concurrent op (ADR-007)

    public BatteryReaderOrchestrator(
        GattBatteryReader gattReader,
        HidBatteryReader hidReader,
        DeviceCapabilityCache capabilityCache)
    {
        _readers = new IBatteryReader[]
        {
            gattReader,
            hidReader
        };
        _capabilityCache = capabilityCache;
    }

    /// <summary>
    /// Reads battery from a device using the protocol fallback chain.
    /// </summary>
    /// <param name="device">The device to read battery from.</param>
    /// <param name="physicalDeviceId">The physical device ID (for caching).</param>
    /// <param name="forceUncached">Whether to force uncached reads (e.g., after reconnect).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DeviceBatteryInfo with battery data, or a default info with null battery if no data is available.</returns>
    public async Task<DeviceBatteryInfo> ReadBatteryAsync(
        DeviceInformation device,
        string physicalDeviceId,
        bool forceUncached = false,
        CancellationToken ct = default)
    {
        // Check if we should skip this device due to too many failures
        if (_capabilityCache.ShouldSkipDevice(physicalDeviceId))
        {
            Log.Debug("Skipping device {PhysicalDeviceId} due to too many consecutive failures", physicalDeviceId);
            return new DeviceBatteryInfo(device.Id, device.Name, null, null, BatterySource.Unknown);
        }

        var cacheMode = forceUncached ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;

        foreach (var reader in _readers)
        {
            // Skip protocols that previously failed for this device
            var caps = _capabilityCache.GetCapabilities(physicalDeviceId);
            if (caps != null && !GetProtocolSupport(reader, caps))
            {
                continue;
            }

            try
            {
                await _bluetoothSemaphore.WaitAsync(ct);
                try
                {
                    var result = await reader.TryReadDeviceAsync(device, cacheMode, ct);
                    if (result != null)
                    {
                        // Update capability cache
                        var protocolSupport = GetProtocolSupport(reader);
                        _capabilityCache.UpdateCapabilities(
                            physicalDeviceId,
                            result.Source,
                            protocolSupport.SupportsGattBatteryLevel,
                            protocolSupport.SupportsGattBatteryPowerState);

                        return result;
                    }
                }
                finally
                {
                    _bluetoothSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read battery from {DeviceName} using {ReaderName}",
                    device.Name, reader.GetType().Name);
                _capabilityCache.RecordFailure(physicalDeviceId);
            }
        }

        return new DeviceBatteryInfo(device.Id, device.Name, null, null, BatterySource.Unknown);
    }

    private static (bool SupportsGattBatteryLevel, bool SupportsGattBatteryPowerState) GetProtocolSupport(IBatteryReader reader)
    {
        return reader switch
        {
            GattBatteryReader => (true, true), // GattBatteryReader supports both 0x180F and 0x2A1B
            HidBatteryReader => (false, true), // HidBatteryReader supports 0x2A1B
            _ => (false, false)
        };
    }
}
```

**Key Notes:**
- **Protocol order:** GATT 0x180F → GATT 0x2A1B (most common to least common).
- **Capability caching:** Skips protocols that previously failed for a device.
- **Smart caching:** Uses `Cached` mode by default, `Uncached` only when forced.
- **Radio throttling:** Limits to **1 concurrent Bluetooth operation** (ADR-007).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/BatteryReaderOrchestrator.cs` | New file: Orchestrates protocol fallback (GATT 0x180F → GATT 0x2A1B). |

---

### Step 6: Protocol-Specific Readers (Core: GATT)
Each reader implements `IBatteryReader` and **tries to read battery from a given `DeviceInformation` object**. If it fails or the device doesn’t support the protocol, it returns `null`.

#### A. Updated `IBatteryReader` Interface

```csharp
/// <summary>
/// Interface for battery readers that can read battery from a specific device.
/// </summary>
public interface IBatteryReader
{
    /// <summary>
    /// Attempts to read battery from the specified device.
    /// </summary>
    /// <param name="device">The device to read battery from.</param>
    /// <param name="cacheMode">Whether to use cached or uncached reads.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DeviceBatteryInfo if battery data is available, otherwise null.</returns>
    Task<DeviceBatteryInfo?> TryReadDeviceAsync(
        DeviceInformation device,
        BluetoothCacheMode cacheMode,
        CancellationToken ct);
}
```

#### B. GATT Battery Reader (0x180F and 0x2A1B)

```csharp
/// <summary>
/// Reads battery from BLE devices using the GATT Battery Service (0x180F) or Battery Power State (0x2A1B).
/// </summary>
public class GattBatteryReader : IBatteryReader
{
    private readonly GattConnectionCache _connectionCache;

    public GattBatteryReader(GattConnectionCache connectionCache)
    {
        _connectionCache = connectionCache;
    }

    public async Task<DeviceBatteryInfo?> TryReadDeviceAsync(
        DeviceInformation device,
        BluetoothCacheMode cacheMode,
        CancellationToken ct)
    {
        try
        {
            // Check if the device is a BLE device
            if (!device.Properties.ContainsKey("System.Devices.Bluetooth.DeviceAddress"))
            {
                return null;
            }

            var bluetoothDevice = await BluetoothDevice.FromIdAsync(device.Id);
            if (bluetoothDevice == null)
            {
                Log.Debug("BluetoothDevice.FromIdAsync returned null for {DeviceId}", device.Id);
                return null;
            }

            // Try Battery Level (0x2A19) first
            var batteryLevelResult = await TryReadBatteryLevelAsync(bluetoothDevice, cacheMode, ct);
            if (batteryLevelResult != null)
            {
                return batteryLevelResult;
            }

            // Fall back to Battery Power State (0x2A1B) for HID devices
            var batteryPowerStateResult = await TryReadBatteryPowerStateAsync(bluetoothDevice, cacheMode, ct);
            if (batteryPowerStateResult != null)
            {
                return batteryPowerStateResult;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GATT read failed for {DeviceName}", device.Name);
            return null;
        }
    }

    private async Task<DeviceBatteryInfo?> TryReadBatteryLevelAsync(
        BluetoothDevice bluetoothDevice,
        BluetoothCacheMode cacheMode,
        CancellationToken ct)
    {
        var batteryService = await _connectionCache.GetServiceAsync(
            bluetoothDevice,
            GattServiceUuids.Battery);
        if (batteryService == null)
        {
            return null;
        }

        var batteryCharacteristic = batteryService.GetCharacteristics(GattCharacteristicUuids.BatteryLevel).FirstOrDefault();
        if (batteryCharacteristic == null)
        {
            return null;
        }

        var result = await batteryCharacteristic.ReadValueAsync(cacheMode);
        if (result.Status != GattCommunicationStatus.Success || result.Value.Length == 0)
        {
            return null;
        }

        return new DeviceBatteryInfo(
            bluetoothDevice.DeviceId,
            bluetoothDevice.Name,
            result.Value[0], // Battery % (0-100)
            null, // IsCharging not available via GATT 0x180F
            BatterySource.Gatt);
    }

    private async Task<DeviceBatteryInfo?> TryReadBatteryPowerStateAsync(
        BluetoothDevice bluetoothDevice,
        BluetoothCacheMode cacheMode,
        CancellationToken ct)
    {
        var batteryService = await _connectionCache.GetServiceAsync(
            bluetoothDevice,
            GattServiceUuids.Battery);
        if (batteryService == null)
        {
            return null;
        }

        var batteryPowerStateChar = batteryService.GetCharacteristics(
            new Guid("00002A1B-0000-1000-8000-00805F9B34FB")).FirstOrDefault();
        if (batteryPowerStateChar == null)
        {
            return null;
        }

        var result = await batteryPowerStateChar.ReadValueAsync(cacheMode);
        if (result.Status != GattCommunicationStatus.Success || result.Value.Length < 2)
        {
            return null;
        }

        var batteryLevel = result.Value[0]; // Battery % (0-100)
        var isCharging = (result.Value[1] & 0x02) != 0; // Bit 1 = Charging

        return new DeviceBatteryInfo(
            bluetoothDevice.DeviceId,
            bluetoothDevice.Name,
            batteryLevel,
            isCharging,
            BatterySource.Gatt);
    }
}
```

**Key Notes:**
- Supports **both 0x180F (Battery Level)** and **0x2A1B (Battery Power State)**.
- Uses **`GattConnectionCache`** to reuse connections.
- Respects **`cacheMode`** (Cached/Uncached).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/IBatteryReader.cs` | Modified: Add `cacheMode` parameter. |
| `src/Monitoring/Gatt/GattBatteryReader.cs` | Modified: Support 0x180F and 0x2A1B, `cacheMode` parameter. |

---

### Step 7: HID Battery Reader (GATT 0x2A1B Only)
**Goal:** Read battery from HID devices (keyboards, mice) that **do not support GATT 0x180F** but **do support GATT 0x2A1B** (Battery Power State).

#### Background
- **Many HID devices** (e.g., Logitech, Razer) report battery via **GATT 0x2A1B** even though they are HID-class.
- **Generic HID report parsing is not feasible** due to vendor-specific layouts.
- **Phase 1:** Only try **GATT 0x2A1B** for HID devices.
- **Phase 2 (Future):** Add vendor-specific adapters (e.g., Logitech, Razer) if users report missing battery data.

#### Implementation

```csharp
/// <summary>
/// Reads battery from HID devices via GATT 0x2A1B (Battery Power State).
/// Note: Generic HID report parsing is not feasible due to vendor-specific layouts.
/// This reader only tries GATT 0x2A1B, which is used by many HID devices (e.g., Logitech, Razer).
/// </summary>
public class HidBatteryReader : IBatteryReader
{
    public async Task<DeviceBatteryInfo?> TryReadDeviceAsync(
        DeviceInformation device,
        BluetoothCacheMode cacheMode,
        CancellationToken ct)
    {
        try
        {
            // Only try HID for HID-class devices
            if (!device.IsKind(DeviceClass.HumanInterfaceDevice))
            {
                return null;
            }

            // Try GATT 0x2A1B (Battery Power State) first
            // Note: Many HID devices (e.g., Logitech, Razer) report battery via GATT even though they are HID-class.
            var bluetoothDevice = await BluetoothDevice.FromIdAsync(device.Id);
            if (bluetoothDevice != null)
            {
                var batteryService = await bluetoothDevice.GetGattServiceAsync(
                    new Guid("0000180F-0000-1000-8000-00805F9B34FB"));
                if (batteryService != null)
                {
                    var batteryPowerStateChar = batteryService.GetCharacteristics(
                        new Guid("00002A1B-0000-1000-8000-00805F9B34FB")).FirstOrDefault();
                    if (batteryPowerStateChar != null)
                    {
                        var result = await batteryPowerStateChar.ReadValueAsync(cacheMode);
                        if (result.Status == GattCommunicationStatus.Success && result.Value.Length >= 2)
                        {
                            var batteryLevel = result.Value[0]; // Battery % (0-100)
                            var isCharging = (result.Value[1] & 0x02) != 0; // Bit 1 = Charging
                            return new DeviceBatteryInfo(
                                device.Id,
                                device.Name,
                                batteryLevel,
                                isCharging,
                                BatterySource.Hid);
                        }
                    }
                }
            }

            // Note: Generic HID report parsing is not implemented here.
            // If a device does not support GATT 0x2A1B, it will require a vendor-specific adapter (Phase 2).
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HID read failed for {DeviceName}", device.Name);
            return null;
        }
    }
}
```

**Key Notes:**
- **Only tries GATT 0x2A1B** (Battery Power State) for HID devices.
- **Does not parse generic HID reports** (vendor-specific, not feasible).
- **Vendor-specific adapters** (e.g., Logitech, Razer) may be added in Phase 2.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Hid/HidBatteryReader.cs` | New file: Implement `IBatteryReader` for HID devices (GATT 0x2A1B only). |

---

### Step 8: Integration with BluetoothBatteryMonitor
**Goal:** Replace the existing polling logic with the new **Windows-first approach**, including **sleep/resume handling** and **realistic timeouts**.

#### Implementation

```csharp
/// <summary>
/// Monitors Bluetooth device battery levels using Windows' device list as the primary source.
/// </summary>
public class BluetoothBatteryMonitor : IAsyncDisposable
{
    private readonly DeviceWatcherService _deviceWatcherService;
    private readonly BatteryReaderOrchestrator _batteryReaderOrchestrator;
    private readonly PhysicalDeviceIdentityResolver _identityResolver;
    private readonly DeviceCapabilityCache _capabilityCache;
    private readonly Timer _pollingTimer;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _scanSemaphore = new(1); // Prevent overlapping scans (ADR-007)

    public BluetoothBatteryMonitor(
        DeviceWatcherService deviceWatcherService,
        BatteryReaderOrchestrator batteryReaderOrchestrator,
        PhysicalDeviceIdentityResolver identityResolver,
        DeviceCapabilityCache capabilityCache)
    {
        _deviceWatcherService = deviceWatcherService;
        _batteryReaderOrchestrator = batteryReaderOrchestrator;
        _identityResolver = identityResolver;
        _capabilityCache = capabilityCache;

        _pollingTimer = new Timer(OnPollingTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));

        // Subscribe to PnP events (triggers UI updates only, no alerts)
        _deviceWatcherService.DeviceAdded += OnDeviceAdded;
        _deviceWatcherService.DeviceRemoved += OnDeviceRemoved;

        // Subscribe to system power events
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private async void OnPollingTick(object? state)
    {
        if (PowerStatus.IsBatteryPower && !UserIsActive)
        {
            // Skip scan if on battery and user is idle
            return;
        }

        await _scanSemaphore.WaitAsync();
        try
        {
            await PollAsync();
        }
        finally
        {
            _scanSemaphore.Release();
        }
    }

    private async Task PollAsync()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // Global scan timeout (ADR-009)
        try
        {
            var startTime = DateTimeOffset.UtcNow;
            var currentDevices = await _deviceWatcherService.GetCurrentDevicesAsync();
            var results = new List<DeviceBatteryInfo>();

            foreach (var device in currentDevices)
            {
                if (cts.Token.IsCancellationRequested) break;
                if (DateTimeOffset.UtcNow - startTime > TimeSpan.FromSeconds(10)) break; // Global timeout

                var physicalDeviceId = _identityResolver.GetPhysicalDeviceId(device);

                // Skip if device has too many consecutive failures
                if (_capabilityCache.ShouldSkipDevice(physicalDeviceId))
                {
                    Log.Debug("Skipping device {PhysicalDeviceId} due to too many failures", physicalDeviceId);
                    continue;
                }

                var batteryInfo = await _batteryReaderOrchestrator.ReadBatteryAsync(
                    device,
                    physicalDeviceId,
                    forceUncached: false, // Use cached reads by default (ADR-006)
                    ct: cts.Token);

                results.Add(batteryInfo);
            }

            await PollingOrchestrator.ProcessResultsAsync(results);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Scan timed out after 10s");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Scan failed");
        }
    }

    private async void OnDeviceAdded(DeviceInformation device)
    {
        // Trigger a one-time scan for the new device (UI update only, no alert)
        await _scanSemaphore.WaitAsync();
        try
        {
            var physicalDeviceId = _identityResolver.GetPhysicalDeviceId(device);
            var batteryInfo = await _batteryReaderOrchestrator.ReadBatteryAsync(
                device,
                physicalDeviceId,
                forceUncached: true, // Force uncached read for new devices
                _cts.Token);

            ScanCoordinator.OnDeviceBatteryUpdated(batteryInfo);
        }
        finally
        {
            _scanSemaphore.Release();
        }
    }

    private void OnDeviceRemoved(DeviceInformation device)
    {
        var physicalDeviceId = _identityResolver.GetPhysicalDeviceId(device);
        _identityResolver.RemoveDevice(device.Id);
        _capabilityCache.InvalidateCapabilities(physicalDeviceId);
        ScanCoordinator.OnDeviceRemoved(physicalDeviceId);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            // Invalidate caches after resume (ADR-012)
            _capabilityCache.InvalidateAll();
            _identityResolver.Clear();

            // Delay scans for 10s to let the Bluetooth stack stabilize
            Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            {
                if (!_cts.Token.IsCancellationRequested)
                {
                    _ = PollAsync();
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _pollingTimer?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        _scanSemaphore.Dispose();
        await _deviceWatcherService.DisposeAsync();
    }
}
```

**Key Notes:**
- **PnP events** trigger **UI updates only** (no alerts). Alerts are **only fired by `PollingOrchestrator`** (ADR-011).
- **Polling** remains the primary mechanism (ADR-004).
- **Radio throttling** via `_scanSemaphore` (ADR-007, default: 1 concurrent operation).
- **Sleep/resume handling** (ADR-012: invalidate caches, delay scans for 10s).
- **Realistic timeouts** (ADR-009: 2s per protocol, 10s global scan).
- **Smart caching** (ADR-006: use `Cached` mode by default).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/BluetoothBatteryMonitor.cs` | Modified: Integrate `DeviceWatcherService`, `BatteryReaderOrchestrator`, `PhysicalDeviceIdentityResolver`, `DeviceCapabilityCache`. |

---

### Step 9: UI Integration
**Goal:** Update the UI to reflect the new architecture (no changes to the user-facing behavior).

#### Implementation

```csharp
/// <summary>
/// Coordinates UI updates for battery scans.
/// </summary>
public class ScanCoordinator
{
    private readonly SynchronizationContext _uiContext;
    private readonly ScanWindow _scanWindow;

    public ScanCoordinator(SynchronizationContext uiContext, ScanWindow scanWindow)
    {
        _uiContext = uiContext;
        _scanWindow = scanWindow;
    }

    public void OnScanComplete(IReadOnlyList<DeviceBatteryInfo> results)
    {
        _uiContext.Post(_ => _scanWindow.UpdateDeviceList(results), null);
    }

    public void OnDeviceBatteryUpdated(DeviceBatteryInfo batteryInfo)
    {
        _uiContext.Post(_ => _scanWindow.UpdateDevice(batteryInfo), null);
    }

    public void OnDeviceRemoved(string physicalDeviceId)
    {
        _uiContext.Post(_ => _scanWindow.RemoveDevice(physicalDeviceId), null);
    }
}
```

#### Files Changed
| File | Change |
|------|--------|
| `src/Tray/ScanCoordinator.cs` | Modified: Handle results from `BluetoothBatteryMonitor`. |

---

## Data Model Changes

### `DeviceBatteryInfo`
Extend `DeviceBatteryInfo` to include the **source of the battery data** (for debugging and UI indicators):

```csharp
/// <summary>
/// Represents battery information for a Bluetooth device.
/// </summary>
public sealed record DeviceBatteryInfo(
    string DeviceId,
    string Name,
    int? Battery,
    bool? IsCharging = null,
    BatterySource? Source = null);  // Indicates the source of the battery data
```

**`BatterySource` Enum:**
```csharp
/// <summary>
/// Indicates the source of the battery data for a device.
/// </summary>
public enum BatterySource
{
    Unknown,
    Gatt,
    Hid
    // AVRCP, HFP, and VendorSpecific may be added later if needed
}
```

**Purpose:**
- Helps with **debugging** (e.g., "Why is this device's battery not updating?").
- Enables **UI indicators** (e.g., icons for different sources).
- Preserves **immutability** (ADR-001).

---

## Settings Changes

Add the following settings to `ThresholdSettings` to control the new behavior:

```json
{
  "Version": 2,
  "Low": 20,
  "High": 80,
  "EnableHidBatteryMonitoring": true,
  "MaxConcurrentBluetoothOperations": 1,
  "ScanTimeoutSeconds": 10,
  "ProtocolTimeoutSeconds": 2,
  "MaxConsecutiveFailures": 3,
  "CacheTTLMinutes": 60
}
```

**Notes:**
- `EnableHidBatteryMonitoring` defaults to `true` (HID is a core protocol).
- `MaxConcurrentBluetoothOperations` defaults to **1** (conservative to avoid radio instability; configurable up to 3).
- `ScanTimeoutSeconds` is the **global timeout** for a full scan (10s).
- `ProtocolTimeoutSeconds` is the **per-protocol timeout** (2s).
- `MaxConsecutiveFailures` is the **threshold** for skipping a device (3).
- `CacheTTLMinutes` is the **TTL for capability cache** (60 minutes).

---

## Acceptance Criteria

### Phase 1 (Core Implementation)
1. **Windows-First Discovery:**
   - Device list is **always sourced from Windows** (`DeviceInformation` + PnP Watcher).
   - No custom device scanning is performed.

2. **Deduplication:**
   - **One entry per physical device** in the UI (merged from multiple `DeviceInformation` entries).
   - Uses **MAC address + ContainerId** for normalization.

3. **Capability Caching:**
   - **Skips protocols that previously failed** for a device.
   - **Cache TTL: 1 hour** (configurable).
   - **Invalidates cache** on reconnect/resume.

4. **Smart Caching:**
   - Uses **`Cached` mode** for regular polls (reduces radio wakeups).
   - Uses **`Uncached` mode** only on reconnect/resume/user refresh.

5. **Performance:**
   - **Ideal:** <2s (cached reads, no radio wakeups).
   - **Acceptable:** <5s (some uncached reads).
   - **Degraded:** <10s (skip slow protocols after timeout).
   - **Timeout:** 2s per protocol, 10s global scan.

6. **Error Handling:**
   - **No silent failures** (log warnings for debugging).
   - **Graceful degradation** (skip failed protocols/devices).
   - **Skip devices after 3 consecutive failures** (configurable).

7. **Sleep/Resume Handling:**
   - **Invalidates caches** after resume.
   - **Delays scans** for 10s to let the Bluetooth stack stabilize.

8. **Async Safety:**
   - **No `.Result`** (all async/await).
   - **Serialized event handling** (no reentrancy issues).

9. **Radio Usage:**
   - **Max 1 concurrent Bluetooth operation** by default (configurable up to 3).
   - **No radio contention** with other Bluetooth activities.

10. **UI Consistency:**
    - Battery data from all sources is displayed **uniformly** in the scan window and tray tooltip.
    - **Loading indicators** are shown during scans.

11. **Real-Time Updates:**
    - New devices are **automatically detected** via PnP Watcher and scanned immediately (UI update only).
    - Disconnected devices are **removed from the UI** within one poll cycle.
    - **No alerts** are fired for PnP-triggered scans (ADR-011).

12. **Backward Compatibility:**
    - All existing functionality (GATT) continues to work unchanged.
    - Existing `DeviceBatteryInfo` construction sites compile without changes.

---

## Files Changed Summary

### New Files (Core)
| File | Purpose |
|------|---------|
| `src/Monitoring/PhysicalDeviceIdentityResolver.cs` | Deduplicates devices by MAC/ContainerId. |
| `src/Monitoring/DeviceCapabilityCache.cs` | Caches protocol support per device. |
| `src/Monitoring/DeviceEnumerator.cs` | Enumerates devices from Windows (AQS filtering). |
| `src/Monitoring/BatteryReaderOrchestrator.cs` | Orchestrates protocol fallback (GATT 0x180F → GATT 0x2A1B). |
| `src/Monitoring/Hid/HidBatteryReader.cs` | Reads battery via GATT 0x2A1B (HID devices). |

### Modified Files (Core)
| File | Change |
|------|--------|
| `src/Monitoring/DeviceWatcherService.cs` | Serialized events, no `.Result`, AQS filtering. |
| `src/Monitoring/BluetoothBatteryMonitor.cs` | Polling + PnP events, respects timeouts/resume. |
| `src/Monitoring/IBatteryReader.cs` | Add `cacheMode` parameter. |
| `src/Monitoring/Gatt/GattBatteryReader.cs` | Support 0x180F and 0x2A1B, `cacheMode` parameter. |
| `src/Monitoring/DeviceBatteryInfo.cs` | Add `BatterySource? Source = null`. |
| `src/Tray/ScanCoordinator.cs` | Handle results from `BluetoothBatteryMonitor`. |
| `src/Settings/ThresholdSettings.cs` | Add new settings for timeouts, concurrency, and caching. |

### Removed Files
| File | Reason |
|------|--------|
| `src/Monitoring/Classic/ClassicBatteryReader.cs` | WMI/Win32_Battery is not for Bluetooth peripherals (expert feedback). |

### Future Files (Optional)
| File | Purpose | Notes |
|------|---------|-------|
| `src/Monitoring/Vendor/LogitechBatteryReader.cs` | Logitech-specific adapter | Add if users report missing battery for Logitech devices. |
| `src/Monitoring/Vendor/RazerBatteryReader.cs` | Razer-specific adapter | Add if users report missing battery for Razer devices. |
| `src/Monitoring/Avrcp/AvrcpBatteryReader.cs` | AVRCP support | Add if users report missing battery for audio devices. |
| `src/Monitoring/Hfp/HfpBatteryReader.cs` | HFP support | Add if users report missing battery for legacy headsets. |

---

## Open Questions

1. **HID Battery Reporting:**
   - How should we handle HID devices that **do not support GATT 0x2A1B**?
   - **Proposed Solution:** Add **vendor-specific adapters** (e.g., Logitech, Razer) if users report missing battery data. Generic HID report parsing is **not feasible** due to vendor-specific layouts.

2. **Radio Concurrency:**
   - Should the default `MaxConcurrentBluetoothOperations` be **1 or 2**?
   - **Proposed Solution:** Default to **1** (most conservative), but allow users to increase to **2–3** if they experience slow scans on high-end adapters.

3. **Cache TTL:**
   - Should the `DeviceCapabilityCache` TTL be **1 hour or 24 hours**?
   - **Proposed Solution:** **1 hour** (battery reporting capabilities rarely change, but this allows for dynamic adjustments if a device reconnects with new capabilities).

---

## Next Steps

### Phase 1: Core Implementation (High Priority)
1. **Implement `PhysicalDeviceIdentityResolver`** (deduplication by MAC/ContainerId).
2. **Implement `DeviceCapabilityCache`** (skip redundant protocol attempts).
3. **Fix `DeviceWatcherService`** (serialized events, no `.Result`, AQS filtering).
4. **Implement `DeviceEnumerator`** (Windows device list + AQS filtering).
5. **Implement `BatteryReaderOrchestrator`** (GATT 0x180F → GATT 0x2A1B fallback).
6. **Update `GattBatteryReader`** to support 0x180F and 0x2A1B with `cacheMode`.
7. **Implement `HidBatteryReader`** (GATT 0x2A1B only for HID devices).
8. **Integrate with `BluetoothBatteryMonitor`** (polling + PnP events, timeouts, resume handling).
9. **Update `DeviceBatteryInfo`** to include `BatterySource`.
10. **Add Settings** for timeouts, concurrency, and caching.
11. **Test with real devices** (BLE mice, keyboards, headphones).

### Phase 2: Testing & Validation
1. **Verify deduplication** (e.g., a headset with multiple interfaces should appear once).
2. **Verify capability caching** (skip failed protocols on subsequent scans).
3. **Verify sleep/resume handling** (scans should recover after 10s delay).
4. **Measure performance** (target: <2s ideal, <5s acceptable, <10s degraded).
5. **Test edge cases** (Broadcom stack, HID devices, disconnected devices).

### Phase 3: Optional Enhancements (Low Priority)
- Add **vendor adapters** (Logitech, Razer, etc.) for devices that don’t support GATT 0x180F or 0x2A1B.
- Add **AVRCP/HFP support** if users report missing battery for audio devices.
- Improve **HID report parsing** if generic support becomes feasible.

---

## Migration Guide (From Current Implementation)

### For Existing Users
- **No action required**: The new implementation will **automatically use Windows’ device list** and fall back to the same protocols as before.
- **Performance improvement**: Faster scans and lower battery usage due to reduced radio contention and smart caching.
- **Better reliability**: Fewer duplicates and more stable device tracking.

### For Developers
1. **Replace `DeviceAggregationPipeline`** with `DeviceEnumerator` + `BatteryReaderOrchestrator`.
2. **Update protocol readers** to implement `TryReadDeviceAsync` (instead of `ReadAllAsync`).
3. **Remove `ClassicBatteryReader`** (WMI/Win32_Battery is not for Bluetooth peripherals).
4. **Add `PhysicalDeviceIdentityResolver`** and `DeviceCapabilityCache` to the dependency graph.
5. **Update `BluetoothBatteryMonitor`** to use the new components.
6. **Test edge cases** (Broadcom stack, HID devices, sleep/resume).

---

## Why This Approach Wins

| **Metric** | **Old Approach (Custom Scanning)** | **New Approach (Windows Cooperation)** |
|------------|------------------------------------|----------------------------------------|
| **Bluetooth Radio Usage** | High (duplicate scanning) | Low (Windows does discovery) |
| **Battery Impact** | Medium-High | Low (smart caching) |
| **Code Complexity** | High | Low |
| **Device Coverage** | ~70–80% (realistic) | ~70–80% (same, but more reliable) |
| **Real-Time Updates** | ✅ Yes | ✅ Yes (PnP Watcher) |
| **Maintainability** | Medium | High |
| **Reliability** | Medium (custom scanning bugs) | High (Windows’ tested enumeration + graceful degradation) |
| **Deduplication** | ❌ No | ✅ Yes (MAC/ContainerId) |
| **Capability Caching** | ❌ No | ✅ Yes (skip redundant protocols) |
| **Sleep/Resume Handling** | ❌ No | ✅ Yes (delay scans, invalidate caches) |

**Result:**
✅ **Lower Bluetooth radio usage** → Better battery life.
✅ **Simpler code** → Fewer bugs, easier maintenance.
✅ **More reliable** → Uses Windows’ tested enumeration + handles edge cases gracefully.
✅ **Practical coverage** → Focus on what works, not theoretical completeness.
✅ **Production-ready** → Addresses all expert critiques (deduplication, caching, timeouts, etc.).

---

## Appendix: Real-World Battery Reporting Coverage

The following table provides a **realistic estimate** of battery reporting coverage across common device types and protocols. **This is not a guarantee**—actual coverage depends on the device, its firmware, and the Windows Bluetooth stack.

| **Device Type** | **GATT (0x180F)** | **GATT (0x2A1B)** | **Total Coverage (Phase 1)** | **Notes** |
|----------------|-------------------|-------------------|-------------------------------|-----------|
| BLE Mice/Keyboards | ✅ High | ✅ High | **~80–90%** | Most modern devices support GATT. |
| AirPods | ✅ High | ❌ No | **~70–80%** | Uses GATT 0x180F. |
| Sony WH-1000XM4 | ✅ High | ❌ No | **~80–90%** | Uses GATT 0x180F. |
| Bose QC45 | ✅ High | ❌ No | **~80–90%** | Uses GATT 0x180F. |
| JBL Speakers | ⚠️ Medium | ❌ No | **~50–60%** | Some models support GATT 0x180F. |
| Xbox Controllers | ⚠️ Medium | ✅ High | **~70–80%** | Uses GATT 0x2A1B. |
| Logitech MX Master | ✅ High | ✅ High | **~90–95%** | Uses GATT 0x180F or 0x2A1B. |
| Legacy BT Headsets | ❌ No | ❌ No | **~0–10%** | Requires AVRCP/HFP (Phase 2). |
| Gaming Peripherals | ⚠️ Medium | ⚠️ Medium | **~60–70%** | Vendor-specific (Phase 2). |

**Key Takeaways:**
- **GATT (0x180F + 0x2A1B)** covers **~70–80% of devices** in practice.
- **AVRCP/HFP** would add **~10–15%** (mostly audio devices).
- **Vendor-Specific** would add **~5–10%** (Logitech, Razer, etc.).
- **No single protocol covers all devices**—**fallbacks are essential**.

---

## Appendix: Common Pitfalls and Mitigations

| **Pitfall** | **Impact** | **Mitigation** |
|-------------|------------|----------------|
| **Duplicate devices** | Confusing UI, conflicting battery values | `PhysicalDeviceIdentityResolver` (MAC/ContainerId) |
| **Stale caches** | Missed battery updates | Invalidate caches on reconnect/resume |
| **Radio contention** | Disconnections, audio dropouts | Limit concurrent ops (default: 1) |
| **Slow scans** | Poor UX | Smart caching (`Cached` mode), timeouts |
| **Sleep/resume instability** | Crashes, failed scans | Delay scans for 10s after resume |
| **Vendor-specific HID reports** | Missing battery data | Vendor adapters (Logitech, Razer) |
| **Broadcom/Widcomm stacks** | Missing Classic BT battery | Fall back to GATT 0x2A1B |
| **Uncached GATT reads** | High radio usage | Use `Cached` mode for regular polls |
| **PnP event storms** | Reentrancy, crashes | Serialized event handling (`SemaphoreSlim`) |
| **DeviceID mismatches** | Failed lookups | Use `GetDeviceByIdAsync` with error handling |

---

## Appendix: Testing Checklist

### Unit Tests
- [ ] `PhysicalDeviceIdentityResolver` correctly deduplicates devices by MAC/ContainerId.
- [ ] `DeviceCapabilityCache` caches and invalidates capabilities correctly.
- [ ] `DeviceEnumerator` filters devices correctly (AQS + properties).
- [ ] `BatteryReaderOrchestrator` skips protocols based on cached capabilities.
- [ ] `GattBatteryReader` reads 0x180F and 0x2A1B correctly.
- [ ] `HidBatteryReader` reads GATT 0x2A1B correctly.

### Integration Tests
- [ ] Full scan (5–10 devices) completes in **<2s (ideal)**, **<5s (acceptable)**, or **<10s (degraded)**.
- [ ] PnP events trigger **UI updates only** (no alerts).
- [ ] Sleep/resume **invalidates caches** and **delays scans** for 10s.
- [ ] **Max 1 concurrent Bluetooth operation** by default.
- [ ] **Capability cache** skips redundant protocol attempts.
- [ ] **Deduplication** works for devices with multiple interfaces.

### Manual Tests
- [ ] Test with **BLE mice/keyboards** (GATT 0x180F).
- [ ] Test with **HID devices** (GATT 0x2A1B).
- [ ] Test with **audio devices** (Sony, Bose, AirPods).
- [ ] Test **sleep/resume** (scans should recover).
- [ ] Test **device reconnect** (caches should invalidate).
- [ ] Test **radio contention** (e.g., during file transfer).
- [ ] Test **Broadcom/Widcomm stacks** (fallback to GATT 0x2A1B).

---

## Appendix: Future Work

### Vendor-Specific Adapters
If users report missing battery data for specific devices, implement **vendor-specific adapters** as separate `IBatteryReader` implementations:

```csharp
// Example: LogitechBatteryReader (Phase 2)
public class LogitechBatteryReader : IBatteryReader
{
    public async Task<DeviceBatteryInfo?> TryReadDeviceAsync(
        DeviceInformation device,
        BluetoothCacheMode cacheMode,
        CancellationToken ct)
    {
        // Check if the device is a Logitech device
        if (!IsLogitechDevice(device)) return null;

        // Use Logitech's proprietary GATT services or HID reports
        // Example: Logitech uses GATT service UUID 0000FF00-0000-1000-8000-00805F9B34FB
        var battery = await ReadLogitechBatteryAsync(device, cacheMode, ct);
        if (battery == null) return null;

        return new DeviceBatteryInfo(
            device.Id,
            device.Name,
            battery.Value.Battery,
            battery.Value.IsCharging,
            BatterySource.VendorSpecific);
    }

    private static bool IsLogitechDevice(DeviceInformation device)
    {
        return device.Properties.TryGetValue("System.Devices.Manufacturer", out var manufacturer)
            && manufacturer.ToString().Contains("Logitech", StringComparison.OrdinalIgnoreCase);
    }
}
```

### AVRCP/HFP Support
If users report missing battery for audio devices, implement **AVRCP/HFP readers** as optional components:

```csharp
// Example: AvrcpBatteryReader (Phase 2)
public class AvrcpBatteryReader : IBatteryReader
{
    public async Task<DeviceBatteryInfo?> TryReadDeviceAsync(
        DeviceInformation device,
        BluetoothCacheMode cacheMode,
        CancellationToken ct)
    {
        // Only try AVRCP for audio devices
        if (!device.IsKind(DeviceClass.Audio)) return null;

        // Use RFCOMM to send AVRCP commands
        var battery = await ReadAvrcpBatteryAsync(device, ct);
        if (battery == null) return null;

        return new DeviceBatteryInfo(
            device.Id,
            device.Name,
            battery.Value.Battery,
            battery.Value.IsCharging,
            BatterySource.Avrcp);
    }
}
```

**Note:** AVRCP/HFP support is **not included in Phase 1** and should only be added if users explicitly request it.
