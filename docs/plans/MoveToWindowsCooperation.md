# Feature Plan: Move to Windows Cooperation for Battery Monitoring

---

## Goal

**Rely on Windows’ built-in Bluetooth device discovery** as the primary source of truth, and **read battery levels from these devices using a minimal, transport-aware set of protocols**, while **acknowledging the fragmentation of battery reporting** across device classes, transports, and vendor implementations. This strategy **reduces Bluetooth radio pressure and saves battery** while ensuring practical coverage for devices used with the computer.

**Key Principle:**
> *"Windows discovers the devices; we **extract** the battery—**using the correct APIs for each transport**."*

**What This Means:**
✅ **Use Windows’ device list** (`DeviceInformation` + PnP Watcher) as the **primary source** for Bluetooth devices.
✅ **Read battery levels** using **transport-aware protocols** (BLE: `BluetoothLEDevice`, GATT 0x2A19).
✅ **Handle edge cases** where Windows doesn’t expose battery (e.g., vendor-specific protocols) **only if users report missing data**.
✅ **Deduplicate devices** to avoid showing the same physical device multiple times (e.g., a headset with BLE, HFP, and A2DP interfaces).
✅ **Cache capabilities and connections** to avoid redundant operations and handle Windows Bluetooth stack quirks.
✅ **Embrace degraded performance** (timeouts, skipped devices) as normal.

❌ **Do NOT scan for unpaired devices in range** (e.g., BLE advertisements for unknown devices).
❌ **Do NOT duplicate Windows’ discovery work** (e.g., custom device scanning).
❌ **Do NOT assume uniform battery reporting** across devices or Windows versions.
❌ **Do NOT use `BluetoothDevice` for BLE-only devices** (use `BluetoothLEDevice`).
❌ **Do NOT treat 0x2A1B as a battery percentage source** (it’s metadata only).

**Why This Approach?**
- **Lower Bluetooth radio usage** → Better battery life and fewer disconnections.
- **Simpler code** → Less complexity, fewer bugs, easier maintenance.
- **More reliable** → Uses Windows’ tested device enumeration + transport-aware APIs.
- **Practical coverage** → Focus on what works, not theoretical completeness.
- **Production-ready** → Handles Windows Bluetooth stack quirks (sleep/resume, connection leaks, etc.).

---

## Background and Constraints

### Current Implementation
The project currently supports:
- **GATT Battery Service (0x180F)** for BLE devices.

**Problem:**
The existing approach **assumes responsibility for device discovery** and **uses incorrect APIs for BLE devices**, which:
- **Duplicates Windows’ work** (inefficient).
- **Increases Bluetooth radio usage** (drains battery).
- **Adds complexity** (custom scanning logic for edge cases).

### Windows’ Built-in Capabilities and Limitations
Windows **already discovers and tracks** Bluetooth devices via:

| Mechanism | Devices Covered | Battery Access? | API | Notes |
|-----------|-----------------|-----------------|-----|-------|
| `DeviceInformation.FindAllAsync` | All paired/remembered devices (BLE + Classic) | ⚠️ Partial | `Windows.Devices.Enumeration` | Primary source for device list (used **only on startup/resume**). |
| PnP Device Watcher | Real-time device additions/removals | ❌ No (triggers scans) | `Windows.Devices.Enumeration` | **Maintains live device set** (no `FindAllAsync` in polling loop). |
| `BluetoothLEDevice` | BLE devices | ✅ Yes (GATT) | `Windows.Devices.Bluetooth` | **Correct API for BLE** (not `BluetoothDevice`). |
| GATT (0x2A19) | BLE devices with Battery Service | ✅ Yes | `Windows.Devices.Bluetooth.GenericAttributeProfile` | **Primary source for battery %**. |

**Critical Realities:**
1. **Not all Bluetooth devices report battery levels** (e.g., some legacy headsets, gaming peripherals).
2. **Battery reporting varies by device class, transport, and vendor** (e.g., Sony vs. Bose vs. Logitech).
3. **Windows does not normalize battery APIs** across device types or Bluetooth stacks.
4. **`Win32_Battery` (WMI) is for system batteries (laptop/UPS), NOT Bluetooth peripherals** → **No ClassicBatteryReader** in this plan.
5. **0x2A1B (Battery Power State) is metadata only** (charging state, power source) and **should not be treated as a percentage source** (ADR-004).

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
- **Primary source:** PnP Device Watcher **maintains a live set** of devices.
- **Secondary:** `DeviceInformation.FindAllAsync` is used **only on startup, resume, or watcher desync**.

---

### ADR-003 — Transport-Aware API Usage
**BLE and Classic Bluetooth require different APIs.** Using the wrong API (e.g., `BluetoothDevice` for BLE-only devices) will cause **intermittent failures**.

**Rule:**
- Use **`BluetoothLEDevice.FromIdAsync`** for **BLE devices** (detected via `DeviceProfileClassifier`).
- **Never use `BluetoothDevice` for BLE-only devices** (it targets Classic/dual-mode abstractions).

---

### ADR-004 — Correct GATT Characteristic Handling
**0x2A19 (Battery Level) is the primary source for battery percentage.** 0x2A1B (Battery Power State) is **metadata only** (charging state, power source) and **must not be treated as a percentage source**.

**Rule:**
- **0x2A19 (Battery Level):** Primary source for battery **percentage (0–100%)**.
- **0x2A1B (Battery Power State):** Supplemental **metadata** (charging state, power source).
- **Do NOT** use `0x2A1B` as a fallback for battery percentage.

**Background:**
- `0x2A19` is **mandatory** for the Battery Service (`0x180F`) and reports a **percentage (0–100%)**.
- `0x2A1B` is **optional** and reports **power state metadata** (e.g., discharging, charging, external power). It **does not always include a percentage** and is **not a reliable source for battery level**.

---

### ADR-005 — Polling Over Push (Clarified)
The project uses a **polling-based approach** (60-second interval) for battery monitoring. **PnP Device Watcher is a supplementary mechanism** that triggers immediate scans for new/updated devices but **does not replace polling**.

**Rule:**
- **Primary mechanism:** Polling every 60s (`PollingOrchestrator` fires alerts).
- **Supplementary mechanism:** PnP Watcher triggers **UI updates only** (no alerts) for new/updated devices.
- **No conflict:** `PollingOrchestrator` remains the **single source of truth** for alert state (ADR-011).

---

### ADR-006 — Physical Device Identity Normalization
A single physical device may appear as **multiple `DeviceInformation` entries** in Windows. To avoid duplicates, we **normalize device identities** using MAC address and ContainerId.

**Rule:** Use `PhysicalDeviceIdentityResolver` with **MAC + ContainerId** as primary keys.

---

### ADR-007 — Success-Only Capability Caching
**Transient failures ≠ lack of support.** Caching failures permanently is **dangerous** and can lead to **stale state**.

**Rule:**
- Cache **only confirmed successes** (not failures).
- **Retry after 5 minutes** for unknown/failed protocols.
- Invalidate cache on reconnect/resume/radio state change.

---

### ADR-008 — Minimal Bluetooth Radio Usage
Limit radio usage to **avoid disconnections** and **save battery**.

**Rule:**
- **1 concurrent Bluetooth operation** (default, configurable up to 3).
- **Cached reads** for regular polls (reduces radio wakeups).
- **Uncached reads** only on reconnect/resume.

---

### ADR-009 — Realistic Performance Targets
Battery monitoring is **not a real-time system**. Latency is acceptable if it doesn’t block the UI.

**Targets:**
- **Ideal:** <2s (cached reads, no radio wakeups).
- **Acceptable:** <5s (some uncached reads).
- **Degraded:** <10s (skip slow protocols).

---

### ADR-010 — SynchronizationContext Over Control.Invoke
All UI updates must use `SynchronizationContext.Post`.

---

### ADR-011 — Single Source of Alert Truth
`PollingOrchestrator` is the **only authority** on alerts.

---

### ADR-012 — Sleep/Resume Handling
Delay scans for **10s after resume** to let the Bluetooth stack stabilize.

---

### ADR-013 — Serialized Event Handling
Use **`Channel<DeviceEvent>`** to avoid `async void` pitfalls (unobserved exceptions, shutdown races).

---

### ADR-014 — GATT Connection Lifecycle
Connections must have **explicit lifecycle rules** to avoid leaks and stale handles.

**Rules:**
1. **Idle timeout:** 30 seconds.
2. **Invalidate on disconnect/radio off/resume.**
3. **Max failures:** 3 consecutive failures before invalidation.

---

### ADR-015 — Device Classification
Classify devices by **transport** and **category** to optimize protocol fallback.

---

### ADR-016 — Global Scan Cancellation
Use **linked `CancellationTokenSource`** to cancel all pending scans on shutdown.

---

## Architecture Overview

```
Windows Device List (PnP Device Watcher → Live Set)
       ↓
[DeviceProfileClassifier] → Classifies by transport/category
       ↓
[PhysicalDeviceIdentityResolver] → Deduplicates (MAC/ContainerId)
       ↓
[DeviceCapabilityCache] → Caches successes (retries failures after 5 min)
       ↓
[GattConnectionManager] → Manages connection lifecycle (idle timeout, invalidate on resume)
       ↓
[BatteryReaderOrchestrator] → Prioritized fallback (GATT 0x2A19 only)
       ↓
[BluetoothBatteryMonitor] → Polling + PnP, global cancellation, sleep/resume
       ↓
[PollingOrchestrator] → Single source of alerts (ADR-011)
       ↓
[ScanCoordinator] → UI updates via SynchronizationContext (ADR-010)
```

---

## Proposed Implementation

---

### Step 1: Device Transport Detection
**Goal:** Correctly identify whether a device is **BLE or Classic** to use the right API (`BluetoothLEDevice` vs. `BluetoothDevice`).

#### Background
- **`BluetoothDevice.FromIdAsync`** is for **Classic Bluetooth** or **dual-mode** devices.
- **`BluetoothLEDevice.FromIdAsync`** is for **BLE-only** devices.
- Using the wrong API can **fail silently** or **return incomplete data**.

#### Implementation

```csharp
/// <summary>
/// Extension methods for determining Bluetooth device transport type.
/// </summary>
public static class BluetoothDeviceExtensions
{
    /// <summary>
    /// Checks if the device is a BLE device.
    /// </summary>
    public static bool IsBleDevice(this DeviceInformation device)
    {
        // Check for BLE-specific properties
        return device.Properties.ContainsKey("System.Devices.Bluetooth.DeviceAddress") &&
               device.Properties.ContainsKey("System.Devices.Bluetooth.SdpRecords");
    }

    /// <summary>
    /// Checks if the device is a Classic Bluetooth device.
    /// </summary>
    public static bool IsClassicDevice(this DeviceInformation device)
    {
        // Classic devices may not have BLE properties
        return device.IsKind(DeviceClass.Bluetooth) && !device.IsBleDevice();
    }
}
```

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/BluetoothDeviceExtensions.cs` | New file: Transport detection (BLE vs. Classic). |

---

### Step 2: Device Classification
**Goal:** Classify devices by **transport** and **category** to optimize protocol selection and reduce wasted operations.

#### Background
Different device types have different **likely battery reporting methods**:
- **Audio devices** (headphones, speakers) → GATT 0x2A19.
- **HID devices** (keyboards, mice) → GATT 0x2A19 (if BLE) or vendor-specific.
- **Controllers** (Xbox, PlayStation) → GATT 0x2A19 or HID.

#### Implementation

```csharp
/// <summary>
/// Classifies Bluetooth devices by transport and category to optimize battery reading.
/// </summary>
public class DeviceProfileClassifier
{
    /// <summary>
    /// Classifies a device into transport and category.
    /// </summary>
    public (DeviceTransport Transport, DeviceCategory Category) Classify(DeviceInformation device)
    {
        var transport = ClassifyTransport(device);
        var category = ClassifyCategory(device);
        return (transport, category);
    }

    private DeviceTransport ClassifyTransport(DeviceInformation device)
    {
        if (device.IsBleDevice())
        {
            return DeviceTransport.Ble;
        }
        else if (device.IsClassicDevice())
        {
            return DeviceTransport.Classic;
        }
        else
        {
            return DeviceTransport.DualMode;
        }
    }

    private DeviceCategory ClassifyCategory(DeviceInformation device)
    {
        // Check device class first
        if (device.IsKind(DeviceClass.Audio))
        {
            return DeviceCategory.Audio;
        }
        if (device.IsKind(DeviceClass.HumanInterfaceDevice))
        {
            return DeviceCategory.Hid;
        }

        // Check manufacturer for known brands
        if (device.Properties.TryGetValue("System.Devices.Manufacturer", out var manufacturerObj))
        {
            var manufacturer = manufacturerObj.ToString().ToLower();
            if (manufacturer.Contains("logitech") || manufacturer.Contains("razer"))
            {
                return DeviceCategory.Hid;
            }
            if (manufacturer.Contains("sony") || manufacturer.Contains("bose") || manufacturer.Contains("jbl"))
            {
                return DeviceCategory.Audio;
            }
            if (manufacturer.Contains("microsoft") && device.Name.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
            {
                return DeviceCategory.Controller;
            }
        }

        // Check device name for hints
        var name = device.Name.ToLower();
        if (name.Contains("headset") || name.Contains("headphone") || name.Contains("earbud"))
        {
            return DeviceCategory.Audio;
        }
        if (name.Contains("mouse") || name.Contains("keyboard") || name.Contains("trackpad"))
        {
            return DeviceCategory.Hid;
        }
        if (name.Contains("controller") || name.Contains("gamepad") || name.Contains("xbox") || name.Contains("playstation"))
        {
            return DeviceCategory.Controller;
        }

        return DeviceCategory.Unknown;
    }
}

/// <summary>
/// Bluetooth transport types.
/// </summary>
public enum DeviceTransport
{
    Ble,
    Classic,
    DualMode
}

/// <summary>
/// Bluetooth device categories.
/// </summary>
public enum DeviceCategory
{
    Unknown,
    Audio,
    Hid,
    Controller,
    Phone
}
```

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/DeviceProfileClassifier.cs` | New file: Classifies devices by transport and category. |

---

### Step 3: Physical Device Identity Normalization
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

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/PhysicalDeviceIdentityResolver.cs` | New file: Deduplicates devices by MAC/ContainerId. |

---

### Step 4: Success-Only Capability Caching
**Goal:** Avoid redundant protocol attempts by caching **only confirmed successes** (not failures).

#### Background
- **Transient failures** (e.g., radio busy, device asleep) do **not** imply lack of support.
- **Permanent failures** (e.g., service not found) can be cached, but **only after explicit confirmation** (e.g., service discovery).
- **Successes** are stable and can be cached for **1 hour**.
- **Retry failures after 5 minutes** to allow for temporary issues to resolve.

#### Implementation

```csharp
/// <summary>
/// Caches the battery-reading capabilities of each physical device.
/// Only caches confirmed successes (not failures).
/// </summary>
public class DeviceCapabilityCache
{
    private readonly Dictionary<string, DeviceCapabilities> _cache = new();
    private readonly object _lock = new();
    private readonly TimeSpan _successCacheTTL = TimeSpan.FromHours(1);
    private readonly TimeSpan _failureRetryDelay = TimeSpan.FromMinutes(5);

    public class DeviceCapabilities
    {
        public bool? SupportsGattBatteryLevel { get; set; } // null = unknown, true/false = confirmed
        public BatterySource LastSuccessfulSource { get; set; } = BatterySource.Unknown;
        public DateTimeOffset LastSuccessTime { get; set; }
        public DateTimeOffset LastFailureTime { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    /// <summary>
    /// Records a successful battery read for a device.
    /// </summary>
    public void RecordSuccess(
        string physicalDeviceId,
        BatterySource source,
        bool supportsGattBatteryLevel = true)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(physicalDeviceId, out var caps))
            {
                caps = new DeviceCapabilities();
                _cache[physicalDeviceId] = caps;
            }

            caps.SupportsGattBatteryLevel = supportsGattBatteryLevel;
            caps.LastSuccessfulSource = source;
            caps.LastSuccessTime = DateTimeOffset.UtcNow;
            caps.ConsecutiveFailures = 0; // Reset on success
        }
    }

    /// <summary>
    /// Records a failure for a device.
    /// </summary>
    public void RecordFailure(string physicalDeviceId)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(physicalDeviceId, out var caps))
            {
                caps = new DeviceCapabilities();
                _cache[physicalDeviceId] = caps;
            }

            caps.LastFailureTime = DateTimeOffset.UtcNow;
            caps.ConsecutiveFailures++;
        }
    }

    /// <summary>
    /// Checks if a protocol should be tried for a device.
    /// Only skips if we have confirmed the device does NOT support it.
    /// </summary>
    public bool ShouldTryProtocol(string physicalDeviceId, Protocol protocol)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(physicalDeviceId, out var caps))
            {
                return true; // Unknown → try it
            }

            // If we have confirmed the device does NOT support this protocol, skip it
            if (protocol == Protocol.GattBatteryLevel && caps.SupportsGattBatteryLevel == false)
            {
                return false;
            }

            // If we've failed recently, retry after the delay
            if (DateTimeOffset.UtcNow - caps.LastFailureTime < _failureRetryDelay)
            {
                return false;
            }

            return true;
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
                // Skip if too many recent failures AND we haven't succeeded recently
                return caps.ConsecutiveFailures >= maxFailures &&
                       (DateTimeOffset.UtcNow - caps.LastSuccessTime) > _failureRetryDelay;
            }
            return false;
        }
    }

    /// <summary>
    /// Invalidates capabilities for a physical device.
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
}

/// <summary>
/// Supported protocols for battery reading.
/// </summary>
public enum Protocol
{
    GattBatteryLevel // 0x2A19 (Battery Level)
    // Future: HidBattery, Avrcp, Hfp, VendorSpecific
}
```

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/DeviceCapabilityCache.cs` | New file: Caches successful protocols per device (success-only). |
| `src/Monitoring/Protocol.cs` | New file: Enum for supported protocols. |

---

### Step 5: GATT Connection Lifecycle Management
**Goal:** Prevent **handle leaks** and **stale connections** by managing GATT connection lifecycle explicitly.

#### Background
- **Windows leaks GATT handles** if not disposed properly.
- **Stale connections** (e.g., after sleep/resume) can cause **permanent failures**.
- **Concurrent connections** can **overload the radio** (ADR-007).

#### Implementation

```csharp
/// <summary>
/// Manages the lifecycle of GATT connections to avoid leaks and stale handles.
/// </summary>
public class GattConnectionManager : IAsyncDisposable
{
    private readonly Dictionary<string, CachedGattService> _cachedServices = new();
    private readonly SemaphoreSlim _connectionSemaphore = new(1); // ADR-007 + ADR-014: 1 concurrent connection
    private readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(30); // ADR-014
    private readonly object _lock = new();

    private class CachedGattService
    {
        public BluetoothLEDevice Device { get; set; } = null!;
        public GattDeviceService Service { get; set; } = null!;
        public DateTimeOffset LastUsed { get; set; }
    }

    /// <summary>
    /// Gets a GATT service for a device, reusing cached connections where possible.
    /// </summary>
    public async Task<GattDeviceService?> GetServiceAsync(
        BluetoothLEDevice device,
        Guid serviceUuid,
        CancellationToken ct)
    {
        var deviceId = device.DeviceId;

        // Check cache for a valid, non-expired connection
        if (TryGetCached(deviceId, serviceUuid, out var cached))
        {
            return cached;
        }

        // Acquire connection semaphore (ADR-007 + ADR-014)
        await _connectionSemaphore.WaitAsync(ct);
        try
        {
            // Double-check cache after acquiring semaphore
            if (TryGetCached(deviceId, serviceUuid, out cached))
            {
                return cached;
            }

            // Connect and cache the service
            var service = await device.GetGattServiceAsync(serviceUuid);
            if (service != null)
            {
                CacheService(device, serviceUuid, service);
            }
            return service;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private bool TryGetCached(string deviceId, Guid serviceUuid, out GattDeviceService service)
    {
        lock (_lock)
        {
            if (_cachedServices.TryGetValue(deviceId, out var cached) &&
                DateTimeOffset.UtcNow - cached.LastUsed < _idleTimeout)
            {
                cached.LastUsed = DateTimeOffset.UtcNow;
                service = cached.Service;
                return true;
            }
            service = null!;
            return false;
        }
    }

    private void CacheService(BluetoothLEDevice device, Guid serviceUuid, GattDeviceService service)
    {
        lock (_lock)
        {
            _cachedServices[device.DeviceId] = new CachedGattService
            {
                Device = device,
                Service = service,
                LastUsed = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>
    /// Invalidates the connection for a specific device.
    /// </summary>
    public void InvalidateConnection(string deviceId)
    {
        lock (_lock)
        {
            _cachedServices.Remove(deviceId);
        }
    }

    /// <summary>
    /// Invalidates all connections (e.g., on radio off or resume).
    /// </summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _cachedServices.Clear();
        }
    }

    /// <summary>
    /// Removes expired connections from the cache.
    /// </summary>
    public void CleanupExpired()
    {
        lock (_lock)
        {
            var expired = _cachedServices.Where(kvp =>
                DateTimeOffset.UtcNow - kvp.Value.LastUsed > _idleTimeout).ToList();
            foreach (var kvp in expired)
            {
                _cachedServices.Remove(kvp.Key);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        InvalidateAll();
        _connectionSemaphore.Dispose();
    }
}
```

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/GattConnectionManager.cs` | New file: Manages GATT connection lifecycle. |

---

### Step 6: Device Watcher Service (Channel-Based)
**Goal:** Monitor PnP events for Bluetooth devices **without `async void`** and **with serialized processing**.

#### Background
- **`async void` event handlers** can **lose exceptions** and **race during shutdown**.
- **PnP events can fire rapidly** (e.g., during device reconnection storms).
- **Solution:** Use a **`Channel<DeviceEvent>`** with a **single consumer loop**.

#### Implementation

```csharp
/// <summary>
/// Monitors PnP events for Bluetooth devices and provides real-time updates.
/// Uses a Channel to serialize event processing and avoid async void issues (ADR-013).
/// </summary>
public class DeviceWatcherService : IAsyncDisposable
{
    private readonly DeviceEnumerator _deviceEnumerator;
    private readonly Channel<DeviceEvent> _eventChannel = Channel.CreateUnbounded<DeviceEvent>();
    private readonly List<DeviceInformation> _currentDevices = new();
    private readonly object _devicesLock = new();
    private DeviceWatcher _watcher = null!;
    private readonly Task _eventProcessorTask;
    private readonly CancellationTokenSource _cts = new();

    public event Action<DeviceInformation> DeviceAdded;
    public event Action<DeviceInformation> DeviceRemoved;

    /// <summary>
    /// Gets the current list of Bluetooth devices (maintained by PnP Watcher).
    /// </summary>
    public IReadOnlyList<DeviceInformation> CurrentDevices
    {
        get
        {
            lock (_devicesLock) return _currentDevices.ToList();
        }
    }

    public DeviceWatcherService(DeviceEnumerator deviceEnumerator)
    {
        _deviceEnumerator = deviceEnumerator;

        // Start event processor loop
        _eventProcessorTask = ProcessEventsAsync(_cts.Token);

        // Configure watcher
        InitializeWatcher();
    }

    private void InitializeWatcher()
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        _watcher = DeviceInformation.CreateWatcher(selector);
        _watcher.Added += (s, device) => _eventChannel.Writer.TryWrite(new DeviceEvent.Added(device));
        _watcher.Removed += (s, update) => _eventChannel.Writer.TryWrite(new DeviceEvent.Removed(update));
        _watcher.Updated += (s, update) => _eventChannel.Writer.TryWrite(new DeviceEvent.Updated(update));
        _watcher.Start();
    }

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        await foreach (var deviceEvent in _eventChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                switch (deviceEvent)
                {
                    case DeviceEvent.Added added:
                        await HandleDeviceAddedAsync(added.Device, ct);
                        break;
                    case DeviceEvent.Removed removed:
                        await HandleDeviceRemovedAsync(removed.DeviceUpdate, ct);
                        break;
                    case DeviceEvent.Updated updated:
                        await HandleDeviceUpdatedAsync(updated.DeviceUpdate, ct);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to process device event");
            }
        }
    }

    private async Task HandleDeviceAddedAsync(DeviceInformation device, CancellationToken ct)
    {
        lock (_devicesLock) _currentDevices.Add(device);
        DeviceAdded?.Invoke(device);
    }

    private async Task HandleDeviceRemovedAsync(DeviceInformationUpdate deviceUpdate, CancellationToken ct)
    {
        DeviceInformation? removedDevice = null;
        lock (_devicesLock)
        {
            removedDevice = _currentDevices.FirstOrDefault(d => d.Id == deviceUpdate.Id);
            if (removedDevice != null)
            {
                _currentDevices.Remove(removedDevice);
            }
        }
        if (removedDevice != null)
        {
            DeviceRemoved?.Invoke(removedDevice);
        }
    }

    private async Task HandleDeviceUpdatedAsync(DeviceInformationUpdate deviceUpdate, CancellationToken ct)
    {
        lock (_devicesLock)
        {
            var oldDevice = _currentDevices.FirstOrDefault(d => d.Id == deviceUpdate.Id);
            if (oldDevice != null) _currentDevices.Remove(oldDevice);
        }

        // Fetch the updated device asynchronously (no .Result deadlock!)
        var updatedDevice = await _deviceEnumerator.GetDeviceByIdAsync(deviceUpdate.Id);
        if (updatedDevice != null)
        {
            lock (_devicesLock) _currentDevices.Add(updatedDevice);
            DeviceAdded?.Invoke(updatedDevice); // Treat as "new" for UI purposes
        }
    }

    /// <summary>
    /// Refreshes the device list from Windows (used on startup/resume/desync).
    /// </summary>
    public async Task RefreshDeviceListAsync(CancellationToken ct)
    {
        var devices = await _deviceEnumerator.GetBluetoothDevicesAsync();
        lock (_devicesLock)
        {
            _currentDevices.Clear();
            _currentDevices.AddRange(devices);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _eventProcessorTask; // Wait for event loop to finish
        }
        catch (OperationCanceledException)
        {
            // Expected during disposal
        }
        _eventChannel.Writer.Complete();
        _watcher.Stop();
        _cts.Dispose();
    }

    private abstract record DeviceEvent;
    private record DeviceEvent.Added(DeviceInformation Device) : DeviceEvent;
    private record DeviceEvent.Removed(DeviceInformationUpdate DeviceUpdate) : DeviceEvent;
    private record DeviceEvent.Updated(DeviceInformationUpdate DeviceUpdate) : DeviceEvent;
}
```

**Key Fixes:**
- **No `async void`:** Uses `Channel<DeviceEvent>` + consumer loop (ADR-013).
- **Serialized events:** All events processed sequentially.
- **No `.Result` deadlocks:** Fully async/await.
- **Live device set:** Exposes `CurrentDevices` for polling (no `FindAllAsync` in loop).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/DeviceWatcherService.cs` | Rewritten: Channel-based event handling, live device set. |

---

### Step 7: Device Enumeration
**Goal:** Enumerate Bluetooth devices from Windows **only on startup, resume, or watcher desync** (not on every poll).

#### Background
- `DeviceInformation.FindAllAsync` returns **all paired/remembered Bluetooth devices** from Windows.
- **AQS filtering** is more reliable than name matching or GUID comparisons.
- **Device properties** (e.g., `System.Devices.Bluetooth.DeviceAddress`) are used to identify Bluetooth devices.

#### Implementation

```csharp
/// <summary>
/// Enumerates Bluetooth devices from Windows using DeviceInformation APIs.
/// This is used only on startup, resume, or watcher desync (ADR-002).
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
            Log.Warning(ex, "GetDeviceByIdAsync failed for {DeviceId}", deviceId);
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

        return false;
    }
}
```

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/DeviceEnumerator.cs` | New file: Enumerates devices from Windows (AQS filtering). |

---

### Step 8: Battery Reader Orchestrator (Success-Only Caching)
**Goal:** For each device from Windows, **try GATT 0x2A19** (primary) and use **success-only caching** to skip redundant attempts.

#### Background
- **GATT 0x2A19 (Battery Level)** is the **only reliable source** for battery percentage.
- **Capability cache** avoids retrying protocols that **explicitly failed** (e.g., service not found).
- **Device classification** optimizes protocol selection.

#### Implementation

```csharp
/// <summary>
/// Orchestrates the protocol fallback chain to read battery from a device.
/// Tries GATT 0x2A19 (primary) and uses success-only caching (ADR-006).
/// </summary>
public class BatteryReaderOrchestrator
{
    private readonly IBatteryReader[] _readers;
    private readonly DeviceCapabilityCache _capabilityCache;
    private readonly DeviceProfileClassifier _classifier;
    private readonly GattConnectionManager _connectionManager;
    private readonly SemaphoreSlim _bluetoothSemaphore = new(1); // ADR-007 + ADR-014: 1 concurrent op

    public BatteryReaderOrchestrator(
        GattBatteryReader gattReader,
        HidBatteryReader hidReader,
        DeviceCapabilityCache capabilityCache,
        DeviceProfileClassifier classifier,
        GattConnectionManager connectionManager)
    {
        _readers = new IBatteryReader[]
        {
            gattReader,
            hidReader
        };
        _capabilityCache = capabilityCache;
        _classifier = classifier;
        _connectionManager = connectionManager;
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
        var (transport, category) = _classifier.Classify(device);

        // Get prioritized readers for this device category
        var readers = GetPrioritizedReaders(category);

        foreach (var reader in readers)
        {
            // Skip protocols that we have confirmed do NOT work for this device
            if (!_capabilityCache.ShouldTryProtocol(physicalDeviceId, reader.Protocol))
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
                        // Record success in capability cache
                        _capabilityCache.RecordSuccess(physicalDeviceId, result.Source);
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

    private IBatteryReader[] GetPrioritizedReaders(DeviceCategory category)
    {
        return category switch
        {
            DeviceCategory.Audio or DeviceCategory.Hid or DeviceCategory.Controller => _readers,
            _ => _readers
        };
    }
}
```

**Key Notes:**
- **Primary protocol:** GATT 0x2A19 (Battery Level).
- **Success-only caching:** Only skips protocols if **explicitly confirmed unsupported** (ADR-006).
- **Device classification:** Optimizes protocol selection.
- **Radio throttling:** 1 concurrent operation (ADR-007 + ADR-014).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/BatteryReaderOrchestrator.cs` | Rewritten: Success-only caching, device classification. |

---

### Step 9: Protocol-Specific Readers (GATT 0x2A19 Only)
Each reader implements `IBatteryReader` and **tries to read battery from a given `DeviceInformation` object**. If it fails or the device doesn’t support the protocol, it returns `null`.

#### A. Updated `IBatteryReader` Interface

```csharp
/// <summary>
/// Interface for battery readers that can read battery from a specific device.
/// </summary>
public interface IBatteryReader
{
    /// <summary>
    /// The protocol this reader supports.
    /// </summary>
    Protocol Protocol { get; }

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

#### B. GATT Battery Reader (0x2A19 Only, BLE Devices)

```csharp
/// <summary>
/// Reads battery from BLE devices using the GATT Battery Level characteristic (0x2A19).
/// Note: 0x2A1B (Battery Power State) is NOT a percentage source (see ADR-004).
/// </summary>
public class GattBatteryReader : IBatteryReader
{
    private readonly GattConnectionManager _connectionManager;

    public GattBatteryReader(GattConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
        Protocol = Protocol.GattBatteryLevel;
    }

    public Protocol Protocol { get; }

    public async Task<DeviceBatteryInfo?> TryReadDeviceAsync(
        DeviceInformation device,
        BluetoothCacheMode cacheMode,
        CancellationToken ct)
    {
        try
        {
            // Only try GATT for BLE devices (ADR-003)
            if (!device.IsBleDevice())
            {
                return null;
            }

            // Use BluetoothLEDevice (not BluetoothDevice) for BLE (ADR-003)
            var bleDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
            if (bleDevice == null)
            {
                Log.Debug("BluetoothLEDevice.FromIdAsync returned null for {DeviceId}", device.Id);
                return null;
            }

            // Get Battery Service (0x180F) via connection manager
            var batteryService = await _connectionManager.GetServiceAsync(
                bleDevice,
                GattServiceUuids.Battery,
                ct);
            if (batteryService == null)
            {
                return null;
            }

            // Read Battery Level (0x2A19) - this is the percentage source (ADR-004)
            var batteryLevelChar = batteryService.GetCharacteristics(GattCharacteristicUuids.BatteryLevel)
                .FirstOrDefault();
            if (batteryLevelChar == null)
            {
                return null;
            }

            var result = await batteryLevelChar.ReadValueAsync(cacheMode);
            if (result.Status != GattCommunicationStatus.Success || result.Value.Length == 0)
            {
                return null;
            }

            return new DeviceBatteryInfo(
                bleDevice.DeviceId,
                bleDevice.Name,
                result.Value[0], // ✅ Percentage (0-100)
                null, // IsCharging not available via 0x2A19
                BatterySource.Gatt);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GATT read failed for {DeviceName}", device.Name);
            return null;
        }
    }
}
```

**Key Fixes:**
- **Uses `BluetoothLEDevice.FromIdAsync`** (not `BluetoothDevice`) (ADR-003).
- **Only reads 0x2A19 (Battery Level)** for percentage (ADR-004).
- **Does NOT use 0x2A1B as a percentage source** (it’s metadata only).
- **Respects `cacheMode`** (Cached/Uncached).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/IBatteryReader.cs` | Modified: Add `Protocol` property and `cacheMode` parameter. |
| `src/Monitoring/Gatt/GattBatteryReader.cs` | Rewritten: Use `BluetoothLEDevice`, only 0x2A19. |

---

### Step 10: HID Battery Reader (GATT 0x2A19 Only)
**Goal:** Read battery from HID devices (keyboards, mice) that **support GATT 0x2A19** (many modern HID devices do).

#### Background
- **Many HID devices** (e.g., Logitech, Razer) **also support BLE** and expose battery via **GATT 0x2A19**.
- **Generic HID report parsing is not feasible** due to vendor-specific layouts.
- **Phase 1:** Only try **GATT 0x2A19** for HID devices.
- **Phase 2 (Future):** Add vendor-specific adapters (e.g., Logitech, Razer) if users report missing battery data.

#### Implementation

```csharp
/// <summary>
/// Reads battery from HID devices via GATT 0x2A19 (Battery Level).
/// Note: Many modern HID devices (e.g., Logitech, Razer) support BLE and expose battery via GATT.
/// Generic HID report parsing is not feasible due to vendor-specific layouts.
/// </summary>
public class HidBatteryReader : IBatteryReader
{
    private readonly GattConnectionManager _connectionManager;

    public HidBatteryReader(GattConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
        Protocol = Protocol.GattBatteryLevel; // Same protocol as GATT reader
    }

    public Protocol Protocol { get; }

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

            // Many HID devices also support BLE - try to read via GATT 0x2A19
            if (!device.IsBleDevice())
            {
                return null;
            }

            var bleDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
            if (bleDevice == null)
            {
                return null;
            }

            // Get Battery Service (0x180F) via connection manager
            var batteryService = await _connectionManager.GetServiceAsync(
                bleDevice,
                GattServiceUuids.Battery,
                ct);
            if (batteryService == null)
            {
                return null;
            }

            // Read Battery Level (0x2A19) - this is the percentage source (ADR-004)
            var batteryLevelChar = batteryService.GetCharacteristics(GattCharacteristicUuids.BatteryLevel)
                .FirstOrDefault();
            if (batteryLevelChar == null)
            {
                return null;
            }

            var result = await batteryLevelChar.ReadValueAsync(cacheMode);
            if (result.Status != GattCommunicationStatus.Success || result.Value.Length == 0)
            {
                return null;
            }

            return new DeviceBatteryInfo(
                bleDevice.DeviceId,
                bleDevice.Name,
                result.Value[0], // ✅ Percentage (0-100)
                null,
                BatterySource.Hid);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HID GATT read failed for {DeviceName}", device.Name);
            return null;
        }
    }
}
```

**Key Notes:**
- **Only tries GATT 0x2A19** (Battery Level) for HID devices that support BLE.
- **Does not parse generic HID reports** (vendor-specific, not feasible).
- **Vendor-specific adapters** (e.g., Logitech, Razer) may be added in Phase 2.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Hid/HidBatteryReader.cs` | New file: Reads battery via GATT 0x2A19 for HID devices. |

---

### Step 11: BluetoothBatteryMonitor (Global Cancellation)
**Goal:** Replace the existing polling logic with the new **Windows-first approach**, including:
- **Live device set** (no `FindAllAsync` in polling loop).
- **Global cancellation** (linked CTS for clean shutdown).
- **Sleep/resume handling** (cache invalidation + 10s delay).

#### Implementation

```csharp
/// <summary>
/// Monitors Bluetooth device battery levels using Windows' device list as the primary source.
/// </summary>
public class BluetoothBatteryMonitor : IAsyncDisposable
{
    private readonly DeviceWatcherService _watcherService;
    private readonly BatteryReaderOrchestrator _batteryReaderOrchestrator;
    private readonly PhysicalDeviceIdentityResolver _identityResolver;
    private readonly DeviceCapabilityCache _capabilityCache;
    private readonly GattConnectionManager _connectionManager;
    private readonly Timer _pollingTimer;
    private readonly CancellationTokenSource _globalCts = new();
    private readonly List<CancellationTokenSource> _scanCtsList = new();
    private readonly object _scanLock = new();

    public BluetoothBatteryMonitor(
        DeviceWatcherService watcherService,
        BatteryReaderOrchestrator batteryReaderOrchestrator,
        PhysicalDeviceIdentityResolver identityResolver,
        DeviceCapabilityCache capabilityCache,
        GattConnectionManager connectionManager)
    {
        _watcherService = watcherService;
        _batteryReaderOrchestrator = batteryReaderOrchestrator;
        _identityResolver = identityResolver;
        _capabilityCache = capabilityCache;
        _connectionManager = connectionManager;

        _pollingTimer = new Timer(OnPollingTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));

        // Subscribe to PnP events (triggers UI updates only, no alerts)
        _watcherService.DeviceAdded += OnDeviceAdded;
        _watcherService.DeviceRemoved += OnDeviceRemoved;

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

        lock (_scanLock)
        {
            // Create a linked CTS for this scan (ADR-016)
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token);
            _scanCtsList.Add(scanCts);
            try
            {
                _ = PollAsync(scanCts.Token); // Fire-and-forget (no await to avoid blocking timer)
            }
            finally
            {
                _scanCtsList.Remove(scanCts);
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var startTime = DateTimeOffset.UtcNow;

        // Use the live set from DeviceWatcherService (no FindAllAsync in loop) (ADR-002)
        var currentDevices = _watcherService.CurrentDevices;
        var results = new List<DeviceBatteryInfo>();

        foreach (var device in currentDevices)
        {
            if (ct.IsCancellationRequested) break;
            if (DateTimeOffset.UtcNow - startTime > TimeSpan.FromSeconds(10)) break; // Global timeout (ADR-009)

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
                ct);

            results.Add(batteryInfo);
        }

        await PollingOrchestrator.ProcessResultsAsync(results);
    }

    private async void OnDeviceAdded(DeviceInformation device)
    {
        lock (_scanLock)
        {
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token);
            _scanCtsList.Add(scanCts);
            try
            {
                _ = HandleDeviceAddedAsync(device, scanCts.Token); // Fire-and-forget
            }
            finally
            {
                _scanCtsList.Remove(scanCts);
            }
        }
    }

    private async Task HandleDeviceAddedAsync(DeviceInformation device, CancellationToken ct)
    {
        var physicalDeviceId = _identityResolver.GetPhysicalDeviceId(device);
        var batteryInfo = await _batteryReaderOrchestrator.ReadBatteryAsync(
            device,
            physicalDeviceId,
            forceUncached: true, // Force uncached read for new devices
            ct);

        ScanCoordinator.OnDeviceBatteryUpdated(batteryInfo);
    }

    private void OnDeviceRemoved(DeviceInformation device)
    {
        var physicalDeviceId = _identityResolver.GetPhysicalDeviceId(device);
        _identityResolver.RemoveDevice(device.Id);
        _capabilityCache.InvalidateCapabilities(physicalDeviceId);
        _connectionManager.InvalidateConnection(device.Id);
        ScanCoordinator.OnDeviceRemoved(physicalDeviceId);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            // Invalidate caches after resume (ADR-012)
            _capabilityCache.InvalidateAll();
            _identityResolver.Clear();
            _connectionManager.InvalidateAll();

            // Refresh device list and delay scans for 10s to let the Bluetooth stack stabilize
            _ = _watcherService.RefreshDeviceListAsync(_globalCts.Token);
            Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            {
                if (!_globalCts.Token.IsCancellationRequested)
                {
                    _ = PollAsync(_globalCts.Token);
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _pollingTimer?.Dispose();

        // Cancel all pending scans (ADR-016)
        _globalCts.Cancel();
        lock (_scanCtsList)
        {
            foreach (var cts in _scanCtsList.ToList())
            {
                cts.Cancel();
                cts.Dispose();
            }
            _scanCtsList.Clear();
        }
        _globalCts.Dispose();
        _scanLock.Dispose();
        await _watcherService.DisposeAsync();
        await _connectionManager.DisposeAsync();
    }
}
```

**Key Notes:**
- **Live device set:** Uses `_watcherService.CurrentDevices` (no `FindAllAsync` in polling loop) (ADR-002).
- **Global cancellation:** Linked CTS for clean shutdown (ADR-016).
- **Sleep/resume handling:** Invalidates caches + 10s delay (ADR-012).
- **PnP events:** Trigger **UI updates only** (no alerts).
- **Radio throttling:** 1 concurrent operation (ADR-007 + ADR-014).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/BluetoothBatteryMonitor.cs` | Rewritten: Live device set, global cancellation, sleep/resume handling. |

---

### Step 12: UI Integration
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
    Gatt
    // Hid, Avrcp, Hfp, VendorSpecific may be added later if needed
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
  "CacheTTLMinutes": 60,
  "RetryAfterFailureMinutes": 5,
  "IdleConnectionTimeoutSeconds": 30
}
```

**Notes:**
- `EnableHidBatteryMonitoring` defaults to `true` (HID devices often support GATT 0x2A19).
- `MaxConcurrentBluetoothOperations` defaults to **1** (conservative to avoid radio instability; configurable up to 3).
- `ScanTimeoutSeconds` is the **global timeout** for a full scan (10s).
- `ProtocolTimeoutSeconds` is the **per-protocol timeout** (2s).
- `MaxConsecutiveFailures` is the **threshold** for skipping a device (3).
- `CacheTTLMinutes` is the **TTL for capability cache** (60 minutes).
- `RetryAfterFailureMinutes` is the **delay before retrying a failed protocol** (5 minutes).
- `IdleConnectionTimeoutSeconds` is the **idle timeout for GATT connections** (30 seconds).

---

## Acceptance Criteria

### Phase 1 (Core Implementation)
1. **Windows-First Discovery:**
   - Device list is **always sourced from Windows** (PnP Watcher live set).
   - No custom device scanning is performed.
   - No `FindAllAsync` in polling loop (ADR-002).

2. **Correct BLE API Usage:**
   - Uses **`BluetoothLEDevice.FromIdAsync`** for BLE devices (not `BluetoothDevice`) (ADR-003).
   - **No silent failures** due to wrong API usage.

3. **GATT Characteristic Handling:**
   - **Only uses 0x2A19 (Battery Level)** for percentage (ADR-004).
   - **Does NOT use 0x2A1B as a percentage source** (it’s metadata only).

4. **Deduplication:**
   - **One entry per physical device** in the UI (merged from multiple `DeviceInformation` entries).
   - Uses **MAC address + ContainerId** for normalization (ADR-005).

5. **Success-Only Capability Caching:**
   - **Caches only confirmed successes** (not failures) (ADR-006).
   - **Cache TTL: 1 hour** (configurable).
   - **Retry failures after 5 minutes** (configurable).
   - **Invalidates cache** on reconnect/resume.

6. **Smart Caching:**
   - Uses **`Cached` mode** for regular polls (reduces radio wakeups) (ADR-006).
   - Uses **`Uncached` mode** only on reconnect/resume/user refresh.

7. **Performance:**
   - **Ideal:** <2s (cached reads, no radio wakeups) (ADR-009).
   - **Acceptable:** <5s (some uncached reads).
   - **Degraded:** <10s (skip slow protocols after timeout).
   - **Timeout:** 2s per protocol, 10s global scan.

8. **Error Handling:**
   - **No silent failures** (log warnings for debugging).
   - **Graceful degradation** (skip failed protocols/devices).
   - **Skip devices after 3 consecutive failures** (configurable).

9. **Sleep/Resume Handling:**
   - **Invalidates caches** (`DeviceCapabilityCache`, `PhysicalDeviceIdentityResolver`, `GattConnectionManager`) after resume (ADR-012).
   - **Delays scans** for 10s to let the Bluetooth stack stabilize.

10. **Async Safety:**
    - **No `async void`** (uses `Channel<DeviceEvent>` + consumer loop) (ADR-013).
    - **Serialized event handling** (no reentrancy issues).

11. **Radio Usage:**
    - **Max 1 concurrent Bluetooth operation** by default (configurable up to 3) (ADR-007 + ADR-014).
    - **No radio contention** with other Bluetooth activities.

12. **Connection Lifecycle:**
    - **Idle timeout:** 30 seconds (ADR-014).
    - **Invalidate on disconnect/radio off/resume** (ADR-014).
    - **No handle leaks**.

13. **Global Cancellation:**
    - **Linked CTS** for scan-wide abort (ADR-016).
    - **Clean shutdown** (no pending GATT calls survive).

14. **Device Classification:**
    - **Transport detection** (BLE vs. Classic) (ADR-015).
    - **Category classification** (Audio, HID, Controller, etc.) (ADR-015).
    - **Protocol prioritization** based on category.

15. **UI Consistency:**
    - Battery data from all sources is displayed **uniformly** in the scan window and tray tooltip.
    - **Loading indicators** are shown during scans.

16. **Real-Time Updates:**
    - New devices are **automatically detected** via PnP Watcher and scanned immediately (UI update only).
    - Disconnected devices are **removed from the UI** within one poll cycle.
    - **No alerts** are fired for PnP-triggered scans (ADR-011).

17. **Backward Compatibility:**
    - All existing functionality (GATT) continues to work unchanged.
    - Existing `DeviceBatteryInfo` construction sites compile without changes.

---

## Files Changed Summary

### New Files (Core)
| File | Purpose |
|------|---------|
| `src/Monitoring/BluetoothDeviceExtensions.cs` | Transport detection (BLE vs. Classic). |
| `src/Monitoring/DeviceProfileClassifier.cs` | Classifies devices by transport and category (ADR-015). |
| `src/Monitoring/PhysicalDeviceIdentityResolver.cs` | Deduplicates devices by MAC/ContainerId (ADR-005). |
| `src/Monitoring/DeviceCapabilityCache.cs` | Caches successful protocols per device (success-only) (ADR-006). |
| `src/Monitoring/Protocol.cs` | Enum for supported protocols. |
| `src/Monitoring/GattConnectionManager.cs` | Manages GATT connection lifecycle (ADR-014). |
| `src/Monitoring/BatteryReaderOrchestrator.cs` | Orchestrates protocol fallback (GATT 0x2A19 only) (ADR-004). |
| `src/Monitoring/Hid/HidBatteryReader.cs` | Reads battery via GATT 0x2A19 for HID devices. |

### Modified Files (Core)
| File | Change |
|------|--------|
| `src/Monitoring/DeviceWatcherService.cs` | Rewritten: Channel-based event handling, live device set (ADR-013). |
| `src/Monitoring/BluetoothBatteryMonitor.cs` | Rewritten: Live device set, global cancellation, sleep/resume handling (ADR-012, ADR-016). |
| `src/Monitoring/IBatteryReader.cs` | Modified: Add `Protocol` property and `cacheMode` parameter. |
| `src/Monitoring/Gatt/GattBatteryReader.cs` | Rewritten: Use `BluetoothLEDevice`, only 0x2A19 (ADR-003, ADR-004). |
| `src/Monitoring/DeviceBatteryInfo.cs` | Add `BatterySource? Source = null`. |
| `src/Monitoring/BatterySource.cs` | New file: Enum for battery data sources. |
| `src/Tray/ScanCoordinator.cs` | Modified: Handle results from `BluetoothBatteryMonitor`. |
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
   - How should we handle HID devices that **do not support GATT 0x2A19**?
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
1. **Implement `BluetoothDeviceExtensions`** (transport detection) (ADR-003).
2. **Implement `DeviceProfileClassifier`** (device classification) (ADR-015).
3. **Implement `PhysicalDeviceIdentityResolver`** (deduplication by MAC/ContainerId) (ADR-005).
4. **Implement `DeviceCapabilityCache`** (success-only caching) (ADR-006).
5. **Implement `GattConnectionManager`** (connection lifecycle) (ADR-014).
6. **Rewrite `DeviceWatcherService`** (Channel-based event handling) (ADR-013).
7. **Implement `BatteryReaderOrchestrator`** (GATT 0x2A19 only, success-only caching).
8. **Rewrite `GattBatteryReader`** (use `BluetoothLEDevice`, only 0x2A19) (ADR-003, ADR-004).
9. **Implement `HidBatteryReader`** (GATT 0x2A19 for HID devices).
10. **Rewrite `BluetoothBatteryMonitor`** (live device set, global cancellation, sleep/resume handling) (ADR-012, ADR-016).
11. **Update `DeviceBatteryInfo`** to include `BatterySource`.
12. **Add Settings** for timeouts, concurrency, and caching.
13. **Test with real devices** (BLE mice, keyboards, headphones).

### Phase 2: Testing & Validation
1. **Verify transport detection** (BLE vs. Classic) (ADR-003).
2. **Verify deduplication** (e.g., a headset with multiple interfaces should appear once) (ADR-005).
3. **Verify success-only caching** (skip failed protocols only if explicitly unsupported) (ADR-006).
4. **Verify sleep/resume handling** (scans should recover after 10s delay) (ADR-012).
5. **Measure performance** (target: <2s ideal, <5s acceptable, <10s degraded) (ADR-009).
6. **Test edge cases** (Broadcom stack, HID devices, disconnected devices).
7. **Verify no `async void`** (Channel-based event handling) (ADR-013).
8. **Verify global cancellation** (clean shutdown) (ADR-016).
9. **Verify connection lifecycle** (no handle leaks) (ADR-014).

### Phase 3: Optional Enhancements (Low Priority)
- Add **vendor adapters** (Logitech, Razer, etc.) for devices that don’t support GATT 0x2A19.
- Add **AVRCP/HFP support** if users report missing battery for audio devices.
- Improve **HID report parsing** if generic support becomes feasible.

---

## Migration Guide (From Current Implementation)

### For Existing Users
- **No action required**: The new implementation will **automatically use Windows’ device list** and fall back to the same protocols as before.
- **Performance improvement**: Faster scans and lower battery usage due to reduced radio contention, smart caching, and deduplication.
- **Better reliability**: Fewer duplicates, more stable device tracking, and graceful handling of failures.

### For Developers
1. **Replace `DeviceAggregationPipeline`** with the new components (`DeviceWatcherService`, `BatteryReaderOrchestrator`, etc.).
2. **Update protocol readers** to implement `TryReadDeviceAsync` (instead of `ReadAllAsync`).
3. **Remove `ClassicBatteryReader`** (WMI/Win32_Battery is not for Bluetooth peripherals).
4. **Add `PhysicalDeviceIdentityResolver`, `DeviceCapabilityCache`, and `GattConnectionManager`** to the dependency graph.
5. **Update `BluetoothBatteryMonitor`** to use the new components (live device set, global cancellation, sleep/resume handling).
6. **Test edge cases** (Broadcom stack, HID devices, sleep/resume, radio contention).

---

## Why This Approach Wins

| **Metric** | **Old Approach (Custom Scanning)** | **New Approach (Windows Cooperation)** |
|------------|------------------------------------|----------------------------------------|
| **Bluetooth Radio Usage** | High (duplicate scanning) | Low (Windows does discovery) |
| **Battery Impact** | Medium-High | Low (smart caching) |
| **Correct APIs** | ❌ Wrong (`BluetoothDevice` for BLE) | ✅ Correct (`BluetoothLEDevice`) (ADR-003) |
| **GATT Characteristic Handling** | ❌ Incorrect (0x2A1B as %) | ✅ Correct (0x2A19 only) (ADR-004) |
| **Deduplication** | ❌ No | ✅ Yes (MAC/ContainerId) (ADR-005) |
| **Capability Caching** | ❌ No (or caches failures) | ✅ Yes (success-only) (ADR-006) |
| **Sleep/Resume Handling** | ❌ No | ✅ Yes (delay scans, invalidate caches) (ADR-012) |
| **Connection Lifecycle** | ❌ No | ✅ Yes (no handle leaks) (ADR-014) |
| **Global Cancellation** | ❌ No | ✅ Yes (clean shutdown) (ADR-016) |
| **Async Safety** | ❌ No (`async void`) | ✅ Yes (`Channel`) (ADR-013) |
| **Live Device Set** | ❌ No (`FindAllAsync` every poll) | ✅ Yes (PnP Watcher) (ADR-002) |
| **Device Classification** | ❌ No | ✅ Yes (transport + category) (ADR-015) |

**Result:**
✅ **Lower Bluetooth radio usage** → Better battery life.
✅ **Simpler code** → Fewer bugs, easier maintenance.
✅ **More reliable** → Uses Windows’ tested enumeration + handles edge cases gracefully.
✅ **Practical coverage** → Focus on what works, not theoretical completeness.
✅ **Production-ready** → Addresses **all expert critiques** (BLE API, 0x2A1B, caching, async, etc.).

---

## Appendix: Real-World Battery Reporting Coverage

The following table provides a **realistic estimate** of battery reporting coverage across common device types and protocols. **This is not a guarantee**—actual coverage depends on the device, its firmware, and the Windows Bluetooth stack.

| **Device Type** | **GATT 0x2A19** | **Total Coverage (Phase 1)** | **Notes** |
|----------------|----------------|-------------------------------|-----------|
| BLE Mice/Keyboards | ✅ High | **~80–90%** | Most modern devices support GATT 0x2A19. |
| AirPods | ✅ High | **~80–90%** | Uses GATT 0x2A19. |
| Sony WH-1000XM4 | ✅ High | **~80–90%** | Uses GATT 0x2A19. |
| Bose QC45 | ✅ High | **~80–90%** | Uses GATT 0x2A19. |
| JBL Speakers | ⚠️ Medium | **~50–60%** | Some models support GATT 0x2A19. |
| Xbox Controllers | ✅ High | **~80–90%** | Uses GATT 0x2A19. |
| Logitech MX Master | ✅ High | **~80–90%** | Uses GATT 0x2A19. |
| Legacy BT Headsets | ❌ No | **~0–10%** | Requires AVRCP/HFP (Phase 2). |
| Gaming Peripherals | ⚠️ Medium | **~60–70%** | Vendor-specific (Phase 2). |

**Key Takeaways:**
- **GATT 0x2A19** covers **~70–90% of devices** in practice.
- **AVRCP/HFP** would add **~10–15%** (mostly audio devices).
- **Vendor-Specific** would add **~5–10%** (Logitech, Razer, etc.).
- **No single protocol covers all devices**—**fallbacks are essential**.

---

## Appendix: Common Pitfalls and Mitigations

| **Pitfall** | **Impact** | **Mitigation** |
|-------------|------------|----------------|
| **Wrong BLE API** (`BluetoothDevice` for BLE) | Silent failures, missing data | Use `BluetoothLEDevice` for BLE devices (ADR-003). |
| **0x2A1B as percentage source** | Incorrect battery values | Only use 0x2A19 for percentage (ADR-004). |
| **Caching failures** | Stale state, missed updates | Cache only successes, retry after 5 min (ADR-006). |
| **`async void` event handlers** | Unobserved exceptions, shutdown races | Use `Channel<DeviceEvent>` + consumer loop (ADR-013). |
| **`FindAllAsync` every poll** | High CPU/radio usage | Use live set from watcher (ADR-002). |
| **No connection lifecycle** | Handle leaks, stale connections | Use `GattConnectionManager` with explicit rules (ADR-014). |
| **No global cancellation** | Pending calls survive shutdown | Use linked CTS hierarchy (ADR-016). |
| **No device classification** | Wasted operations | Use `DeviceProfileClassifier` (ADR-015). |
| **Duplicate devices** | Confusing UI, conflicting data | Use `PhysicalDeviceIdentityResolver` (ADR-005). |
| **Sleep/resume instability** | Crashes, failed scans | Delay scans + invalidate caches (ADR-012). |

---

## Appendix: Testing Checklist

### Unit Tests
- [ ] `BluetoothDeviceExtensions` correctly detects BLE vs. Classic devices (ADR-003).
- [ ] `DeviceProfileClassifier` classifies devices correctly by transport and category (ADR-015).
- [ ] `PhysicalDeviceIdentityResolver` correctly deduplicates devices by MAC/ContainerId (ADR-005).
- [ ] `DeviceCapabilityCache` caches successes and does NOT cache failures (ADR-006).
- [ ] `GattConnectionManager` reuses connections and respects idle timeout (ADR-014).
- [ ] `GattBatteryReader` reads 0x2A19 correctly and does NOT use 0x2A1B for percentage (ADR-004).
- [ ] `HidBatteryReader` reads GATT 0x2A19 for HID devices.
- [ ] `BatteryReaderOrchestrator` skips protocols only if explicitly unsupported (ADR-006).

### Integration Tests
- [ ] Full scan (5–10 devices) completes in **<2s (ideal)**, **<5s (acceptable)**, or **<10s (degraded)** (ADR-009).
- [ ] PnP events trigger **UI updates only** (no alerts) (ADR-011).
- [ ] Sleep/resume **invalidates caches** and **delays scans** for 10s (ADR-012).
- [ ] **Max 1 concurrent Bluetooth operation** by default (ADR-007 + ADR-014).
- [ ] **Capability cache** skips redundant protocol attempts (success-only) (ADR-006).
- [ ] **Deduplication** works for devices with multiple interfaces (ADR-005).
- [ ] **Global cancellation** aborts all pending scans on disposal (ADR-016).
- [ ] **Connection lifecycle** avoids handle leaks (ADR-014).

### Manual Tests
- [ ] Test with **BLE mice/keyboards** (GATT 0x2A19).
- [ ] Test with **HID devices** (GATT 0x2A19 via BLE).
- [ ] Test with **audio devices** (Sony, Bose, AirPods).
- [ ] Test **sleep/resume** (scans should recover after 10s delay) (ADR-012).
- [ ] Test **device reconnect** (caches should invalidate).
- [ ] Test **radio contention** (e.g., during file transfer).
- [ ] Test **Broadcom/Widcomm stacks** (fallback to GATT 0x2A19).
- [ ] Test **app shutdown** (no pending GATT calls survive) (ADR-016).

---

## Appendix: Future Work

### Vendor-Specific Adapters
If users report missing battery data for specific devices, implement **vendor-specific adapters** as separate `IBatteryReader` implementations:

```csharp
// Example: LogitechBatteryReader (Phase 2)
public class LogitechBatteryReader : IBatteryReader
{
    public Protocol Protocol { get; } = Protocol.VendorSpecific;

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
    public Protocol Protocol { get; } = Protocol.Avrcp;

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