# Feature Plan: Move to Windows Cooperation for Battery Monitoring

---

## Goal

**Rely on Windows’ built-in Bluetooth device discovery** as the primary source of truth, and **read battery levels from these devices using a minimal, transport-aware set of protocols**, while **acknowledging the fragmentation of battery reporting** across device classes, transports, and vendor implementations. This strategy **reduces Bluetooth radio pressure and saves battery**—**for both the computer and the peripherals**—while ensuring practical coverage for devices used with the computer.

**Key Principle:**
> *"Windows discovers the devices; we **extract** the battery—**using the correct APIs for each transport, and prioritizing peripheral battery life over reconnection speed**."*

**What This Means:**
✅ **Use Windows’ device list** (`DeviceInformation` + PnP Watcher) as the **primary source** for Bluetooth devices.
✅ **Read battery levels** using **transport-aware protocols** (BLE: `BluetoothLEDevice`, GATT 0x2A19).
✅ **Handle edge cases** where Windows doesn’t expose battery (e.g., vendor-specific protocols) **only if users report missing data**.
✅ **Deduplicate devices** using **ContainerId as the primary key** (MAC as fallback for RPA devices).
✅ **Cache knowledge, not objects** to avoid blocking peripheral low-power sleep states.
✅ **Use hard timeouts** for all WinRT calls to prevent hangs.
✅ **Embrace degraded performance** (timeouts, skipped devices) as normal.

❌ **Do NOT scan for unpaired devices in range** (e.g., BLE advertisements for unknown devices).
❌ **Do NOT duplicate Windows’ discovery work** (e.g., custom device scanning).
❌ **Do NOT assume uniform battery reporting** across devices or Windows versions.
❌ **Do NOT use `BluetoothDevice` for BLE-only devices** (use `BluetoothLEDevice`).
❌ **Do NOT treat 0x2A1B as a battery percentage source** (it’s metadata only).
❌ **Do NOT cache `BluetoothLEDevice` objects** (blocks peripheral sleep).

**Why This Approach?**
- **Lower Bluetooth radio usage** → Better battery life for the computer.
- **Peripheral-friendly** → Allows peripherals to enter low-power sleep states.
- **Simpler code** → Less complexity, fewer bugs, easier maintenance.
- **More reliable** → Uses Windows’ tested device enumeration + handles edge cases gracefully.
- **Production-ready** → Addresses all expert critiques (BLE API, 0x2A1B, caching, async, lifecycle, timeouts, RPA).

---

## Background and Constraints

### Current Implementation
The project currently supports:
- **GATT Battery Service (0x180F)** for BLE devices.

**Problem:**
The existing approach **assumes responsibility for device discovery**, **uses incorrect APIs for BLE devices**, and **caches objects that block peripheral sleep**, which:
- **Duplicates Windows’ work** (inefficient).
- **Increases Bluetooth radio usage** (drains computer battery).
- **Blocks peripheral low-power sleep** (drains peripheral battery).
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
6. **Random Private Addresses (RPA)** on modern BLE devices (e.g., AirPods, some earbuds) **change MAC addresses periodically** → **Prioritize ContainerId for deduplication** (ADR-005).
7. **Caching `BluetoothLEDevice` objects prevents peripherals from entering low-power sleep states** → **Cache knowledge, not objects** (ADR-013).
8. **WinRT GATT calls can hang indefinitely** → **Hard timeouts are mandatory** (ADR-014).
9. **High-performance gaming mice (Logitech, Razer) often do NOT expose battery via GATT** → **HID coverage in Phase 1 is ~30–40%**, not 80–90%.

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
- **No `FindAllAsync` in polling loop** (use live set from watcher).

---

### ADR-003 — Transport-Aware API Usage
**BLE and Classic Bluetooth require different APIs.** Using the wrong API (e.g., `BluetoothDevice` for BLE-only devices) will cause **intermittent failures**.

**Rule:**
- Use **`BluetoothLEDevice.FromIdAsync`** for **BLE devices** (detected via `DeviceProfileClassifier`).
- **Never use `BluetoothDevice` for BLE-only devices** (it targets Classic/dual-mode abstractions).
- **Always prioritize BLE/GATT** for battery reading, even if a Classic interface exists (more efficient).

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

### ADR-005 — Physical Device Identity Normalization
A single physical device may appear as **multiple `DeviceInformation` entries** in Windows. To avoid duplicates, we **normalize device identities** using **ContainerId as the primary key** (handles Random Private Addresses).

**Rule:**
- **Primary key:** `ContainerId` (stable across RPA changes).
- **Secondary key:** MAC address (fallback, may change for privacy-enabled devices).
- **Update MAC** if it changes for an existing device (RPA rotation).

**Why ContainerId First?**
- Some BLE devices use **Random Private Addresses (RPA)** that change periodically.
- **ContainerId** is Windows’ attempt to group different interfaces (BLE, Classic, HID) of the same physical device.
- If the MAC changes but the pairing persists, **ContainerId is the only stable anchor**.

---

### ADR-006 — Success-Only Capability Caching
**Transient failures ≠ lack of support.** Caching failures permanently is **dangerous** and can lead to **stale state** (e.g., a device that was asleep is now marked as unsupported forever).

**Rule:**
- Cache **only confirmed successes** (not failures).
- **Retry failed protocols after 5 minutes** (configurable) to allow for temporary issues (e.g., device asleep, radio busy).
- Invalidate cache on reconnect/resume/radio state change.

---

### ADR-007 — Minimal Bluetooth Radio Usage
Limit radio usage to **avoid disconnections** and **save battery** (for both the computer and peripherals).

**Rule:**
- **1 concurrent Bluetooth operation** (default, configurable up to 3).

---

### ADR-008 — Realistic Performance Targets
Battery monitoring is **not a real-time system**. Latency is acceptable if it doesn’t block the UI or drain batteries.

**Targets:**
- **Ideal:** <2s (cached reads, no radio wakeups).
- **Acceptable:** <5s (some uncached reads).
- **Degraded:** <10s (skip slow protocols).

---

### ADR-009 — SynchronizationContext Over Control.Invoke
All UI updates must use `SynchronizationContext.Post`.

---

### ADR-010 — Single Source of Alert Truth
`PollingOrchestrator` is the **only authority** on alerts.

---

### ADR-011 — Sleep/Resume Handling
Delay scans for **10 seconds after resume** to let the Bluetooth stack stabilize.

---

### ADR-012 — Serialized Event Handling
Use **`Channel<DeviceEvent>`** to avoid `async void` pitfalls (unobserved exceptions, shutdown races).

---

### ADR-013 — GATT Connection Lifecycle
**Caching `BluetoothLEDevice` objects prevents peripherals from entering low-power sleep states.** To avoid draining peripheral batteries, we must **cache knowledge, not objects**.

**Rules:**
1. **Never cache `BluetoothLEDevice` objects** (blocks peripheral sleep).
2. **Cache only knowledge** (e.g., "Device supports GATT 0x2A19").
3. **Dispose all WinRT objects immediately** after use.
4. **Recreate connections on every poll** (or after a short idle timeout).

**Trade-off:** Slightly higher reconnection overhead (~200–500ms per device), but **peripherals can sleep** (better battery life).

**Why This Trade-Off is Worth It:**
| **Metric** | **Cache Objects** | **Cache Knowledge** |
|------------|-------------------|----------------------|
| Peripheral Battery Life | ❌ Poor (blocks sleep) | ✅ Good (allows sleep) |
| Reconnection Overhead | ✅ Low | ⚠️ Medium (200–500ms per device) |
| Radio Contention | ❌ High (persistent connections) | ✅ Low (short-lived connections) |
| User Experience | ❌ Poor (devices drain fast) | ✅ Good (devices last longer) |

---

### ADR-014 — Hard Timeouts for WinRT Calls
Some WinRT GATT calls **hang indefinitely** (even with `CancellationToken`). To prevent the polling timer from blocking forever, **all WinRT calls must have a hard timeout**.

**Rules:**
- **Per-operation timeout:** 2 seconds (hard limit).
- **Use `Task.WaitAsync(timeout)`** or a custom timeout wrapper for all WinRT calls.
- **Propagate timeouts** to avoid zombie tasks.

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
[DeviceProfileClassifier] → Classifies by transport/category (ADR-015)
       ↓
[PhysicalDeviceIdentityResolver] → Deduplicates (ContainerId primary, MAC secondary) (ADR-005)
       ↓
[DeviceCapabilityCache] → Caches successes (retries failures after 5 min) (ADR-006)
       ↓
[BatteryReaderOrchestrator] → Prioritized fallback (GATT 0x2A19 only) (ADR-004)
       ↓
[BluetoothBatteryMonitor] → Polling + PnP, global cancellation, sleep/resume (ADR-012, ADR-016)
       ↓
[PollingOrchestrator] → Single source of alerts (ADR-011)
       ↓
[ScanCoordinator] → UI updates via SynchronizationContext (ADR-010)

Note: No BluetoothLEDevice caching (ADR-013). No async void (ADR-012). Hard timeouts (ADR-014).
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

**Files Changed**

| File | Change |
|------|--------|
| `src/Monitoring/BluetoothDeviceExtensions.cs` | New file: Transport detection (BLE vs. Classic) (ADR-003). |

---

### Step 2: Task Extensions for Hard Timeouts
**Goal:** Add **hard timeouts** for all WinRT calls to prevent hangs (ADR-014).

#### Background
- Some WinRT GATT calls (e.g., `GetGattServiceAsync`, `ReadValueAsync`) **can hang indefinitely**, even with a `CancellationToken`.
- Without hard timeouts, the **polling timer can block forever** on a zombie device.

#### Implementation

```csharp
/// <summary>
/// Extension methods for adding hard timeouts to tasks.
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Waits for the task to complete within the specified timeout.
    /// If the task does not complete in time, it is cancelled and a TimeoutException is thrown.
    /// </summary>
    /// <typeparam name="T">The type of the task result.</typeparam>
    /// <param name="task">The task to wait for.</param>
    /// <param name="timeout">The timeout duration.</param>
    /// <returns>The result of the task if it completes within the timeout.</returns>
    /// <exception cref="TimeoutException">Thrown if the task does not complete within the timeout.</exception>
    public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var completedTask = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
        if (completedTask == task)
        {
            return await task; // Task completed successfully
        }
        else
        {
            cts.Cancel(); // Cancel the original task
            throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds}s");
        }
    }
}
```

**Files Changed**

| File | Change |
|------|--------|
| `src/Extensions/TaskExtensions.cs` | New file: Hard timeout support for WinRT calls (ADR-014). |

---

### Step 3: Device Classification
**Goal:** Classify devices by **transport** and **category** to optimize protocol selection and reduce wasted operations.

#### Background
Different device types have different **likely battery reporting methods**:
- **Audio devices** (headphones, speakers) → GATT 0x2A19.
- **HID devices** (keyboards, mice) → GATT 0x2A19 (if BLE) or vendor-specific.
- **Controllers** (Xbox, PlayStation) → GATT 0x2A19 or HID.
- **Dual-mode devices** (e.g., Sony WH-1000XM4) → **Prioritize GATT 0x2A19** (more efficient).

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

**Files Changed**

| File | Change |
|------|--------|
| `src/Monitoring/DeviceProfileClassifier.cs` | New file: Classifies devices by transport and category (ADR-015). |

---

### Step 4: Physical Device Identity Normalization
**Goal:** Map multiple `DeviceInformation` entries to a **single physical device** to avoid duplicates in the UI, **prioritizing ContainerId over MAC** (to handle RPA devices).

#### Background
A single physical device (e.g., a Bluetooth headset) may appear as **multiple entries** in Windows’ device list:
- One for **BLE** (e.g., for battery reporting via GATT).
- One for **Classic Bluetooth** (e.g., for audio via A2DP or HFP).
- One for **HID** (e.g., for media controls).

Without normalization, the app will show **duplicate devices** with **conflicting battery values** and **UI instability**.

**Critical Note:**
- **Random Private Addresses (RPA)** on modern BLE devices (e.g., AirPods, some earbuds) **change MAC addresses periodically**.
- **ContainerId** is Windows’ **stable anchor** for grouping interfaces of the same physical device.

#### Implementation

```csharp
/// <summary>
/// Resolves multiple DeviceInformation entries to a single physical device.
/// Uses ContainerId as the primary key (stable across RPA changes) and MAC as fallback.
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
            var containerId = GetContainerId(device);
            var macAddress = GetMacAddress(device);

            // Prioritize ContainerId (stable across RPA changes and interface types)
            var existing = _physicalDevices.Values.FirstOrDefault(pd =>
                pd.ContainerId == containerId ||  // Primary key
                (!string.IsNullOrEmpty(pd.MacAddress) && pd.MacAddress == macAddress)); // Fallback

            if (existing != null)
            {
                existing.DeviceIds.Add(device.Id);
                existing.MacAddress = macAddress; // Update MAC if it changed (RPA)
                existing.ContainerId = containerId; // Update ContainerId if available
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
- **ContainerId is the primary key** (handles RPA changes).
- **MAC is updated** if it changes for an existing device (RPA rotation).
- **Thread-safe** (locks all access to `_physicalDevices`).

**Files Changed**

| File | Change |
|------|--------|
| `src/Monitoring/PhysicalDeviceIdentityResolver.cs` | New file: Deduplicates devices by ContainerId + MAC (ADR-005). |

