# Feature Plan: Move to Windows Cooperation for Battery Monitoring

---

## Goal

**Rely on Windows’ built-in Bluetooth device discovery** as the primary source of truth, and **read battery levels from these devices using a minimal, transport-aware set of protocols**, while **acknowledging the fragmentation of battery reporting** across device classes, transports, and vendor implementations. This strategy **reduces Bluetooth radio pressure and saves battery** (both on the host *and* peripherals) while ensuring practical coverage for devices used with the computer.

**Key Principle:**
> *"Windows discovers the devices; we **extract** the battery—**using the correct APIs for each transport, without blocking peripheral sleep states**."*

**What This Means:**
✅ **Use Windows’ device list** (`DeviceInformation` + PnP Watcher) as the **primary source** for Bluetooth devices.
✅ **Read battery levels** using **transport-aware protocols** (BLE: `BluetoothLEDevice`, GATT 0x2A19).
✅ **Handle edge cases** where Windows doesn’t expose battery (e.g., vendor-specific protocols) **only if users report missing data**.
✅ **Deduplicate devices** using **ContainerId as the primary key** (handles Random Private Addresses).
✅ **Cache capabilities, not connections** to avoid blocking peripheral sleep states.
✅ **Use hard timeouts** for all WinRT calls to prevent hangs.
✅ **Embrace degraded performance** (timeouts, skipped devices) as normal.

❌ **Do NOT scan for unpaired devices in range** (e.g., BLE advertisements for unknown devices).
❌ **Do NOT duplicate Windows’ discovery work** (e.g., custom device scanning).
❌ **Do NOT assume uniform battery reporting** across devices or Windows versions.
❌ **Do NOT cache `BluetoothLEDevice` objects** (blocks peripheral sleep).
❌ **Do NOT treat 0x2A1B as a battery percentage source** (it’s metadata only).
❌ **Do NOT rely on MAC addresses alone** for deduplication (RPA changes).

**Why This Approach?**
- **Lower Bluetooth radio usage** → Better battery life on host and peripherals.
- **Simpler code** → Less complexity, fewer bugs, easier maintenance.
- **More reliable** → Uses Windows’ tested device enumeration + transport-aware APIs.
- **Peripheral-friendly** → Allows devices to enter low-power sleep states.
- **Production-ready** → Handles Windows Bluetooth stack quirks (sleep/resume, connection leaks, hangs, RPA, etc.).

---

## Background and Constraints

### Current Implementation
The project currently supports:
- **GATT Battery Service (0x180F)** for BLE devices.

**Problem:**
The existing approach **assumes responsibility for device discovery** and **uses incorrect APIs for BLE devices**, which:
- **Duplicates Windows’ work** (inefficient).
- **Increases Bluetooth radio usage** (drains battery).
- **Blocks peripheral sleep states** (caching `BluetoothLEDevice` objects).
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
6. **Random Private Addresses (RPA)** on modern BLE devices (e.g., AirPods) **change MAC addresses periodically** → **ContainerId is the only stable anchor**.
7. **Caching `BluetoothLEDevice` objects prevents peripherals from entering low-power sleep** → **Cache knowledge, not objects** (ADR-013).
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

---

### ADR-003 — Transport-Aware API Usage
**BLE and Classic Bluetooth require different APIs.** Using the wrong API (e.g., `BluetoothDevice` for BLE-only devices) will cause **intermittent failures**.

**Rule:**
- Use **`BluetoothLEDevice.FromIdAsync`** for **BLE devices** (detected via `DeviceProfileClassifier`).
- **Never use `BluetoothDevice` for BLE-only devices** (it targets Classic/dual-mode abstractions).
- **Prioritize BLE/GATT** for battery reading, even if a Classic interface exists (more efficient).

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

---

### ADR-006 — Success-Only Capability Caching
**Transient failures ≠ lack of support.** Caching failures permanently is **dangerous** and can lead to **stale state**.

**Rule:**
- Cache **only confirmed successes** (not failures).
- **Retry after 5 minutes** for unknown/failed protocols.
- Invalidate cache on reconnect/resume/radio state change.

---

### ADR-007 — Minimal Bluetooth Radio Usage
Limit radio usage to **avoid disconnections** and **save battery**.

**Rule:**
- **1 concurrent Bluetooth operation** (default, configurable up to 3).
- **Cached reads** for regular polls (reduces radio wakeups).
- **Uncached reads** only on reconnect/resume.

---

### ADR-008 — Realistic Performance Targets
Battery monitoring is **not a real-time system**. Latency is acceptable if it doesn’t block the UI.

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
**Caching `BluetoothLEDevice` objects prevents peripherals from entering low-power sleep.** To avoid draining peripheral batteries, we must **cache knowledge, not objects**.

**Rules:**
1. **Never cache `BluetoothLEDevice` objects** (blocks peripheral sleep).
2. **Cache only knowledge** (e.g., "Device supports GATT 0x2A19").
3. **Dispose all WinRT objects immediately** after use.
4. **Recreate connections on every poll** (or after a short idle timeout).

**Trade-off:** Slightly higher reconnection overhead (~200–500ms per device), but **peripherals can sleep** (better battery life).

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

#### Files Changed

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
            throw new TimeoutException($