# Feature Plan: Move to Windows Cooperation for Battery Monitoring

---

## Goal

**Rely on Windows’ built-in Bluetooth device discovery** as the primary source of truth, and **read battery levels from these devices using a minimal, transport-aware set of protocols**, while **acknowledging the fragmentation of battery reporting** across device classes, transports, and vendor implementations. This strategy **reduces Bluetooth radio pressure and saves battery**—**both for the host and the peripherals**—while ensuring practical coverage for devices used with the computer.

**Key Principle:**
> *"Windows discovers the devices; we **extract** the battery—**using the correct APIs, prioritizing peripheral battery life over reconnection speed, and handling Windows Bluetooth stack quirks**."*

**What This Means:**
✅ **Use Windows’ device list** (`DeviceInformation` + PnP Watcher) as the **primary source** for Bluetooth devices.
✅ **Read battery levels** using **transport-aware protocols** (BLE: `BluetoothLEDevice`, GATT 0x2A19).
✅ **Prioritize peripheral battery life** by **not caching `BluetoothLEDevice` objects** (prevents sleep blocking).
✅ **Handle edge cases** where Windows doesn’t expose battery (e.g., vendor-specific protocols) **only if users report missing data**.
✅ **Deduplicate devices** using **ContainerId as the primary key** (MAC as fallback for RPA devices).
✅ **Cache capabilities, not connections** to avoid blocking peripheral low-power states.
✅ **Use hard timeouts** for all WinRT calls to prevent hangs.

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

### Windows’ Built-in Capabilities and Limitations
Windows **already discovers and tracks** Bluetooth devices via:

| Mechanism | Devices Covered | Battery Access? | API | Notes |
|-----------|-----------------|-----------------|-----|-------|
| `DeviceInformation.FindAllAsync` | All paired/remembered devices (BLE + Classic) | ⚠️ Partial | `Windows.Devices.Enumeration` | Used **only on startup/resume/desync** (ADR-002). |
| PnP Device Watcher | Real-time device additions/removals | ❌ No (triggers scans) | `Windows.Devices.Enumeration` | **Maintains live device set** (no `FindAllAsync` in polling loop). |
| `BluetoothLEDevice` | BLE devices | ✅ Yes (GATT) | `Windows.Devices.Bluetooth` | **Correct API for BLE** (ADR-003). |
| GATT (0x2A19) | BLE Battery Level | ✅ Yes | `GenericAttributeProfile` | **Primary source for battery %** (ADR-004). |

**Critical Realities:**
1. **Not all Bluetooth devices report battery levels** (e.g., some legacy headsets, gaming peripherals).
2. **0x2A1B is metadata only** (charging state) → **0x2A19 is the only percentage source** (ADR-004).
3. **Random Private Addresses (RPA)** change MAC addresses → **Prioritize ContainerId** (ADR-005).
4. **Caching `BluetoothLEDevice` blocks peripheral sleep** → **Cache knowledge, not objects** (ADR-014).
5. **WinRT calls can hang** → **Hard timeouts mandatory** (ADR-017).
6. **HID via GATT coverage is ~30–40%** (not 80–90%) → **Vendor adapters needed for full coverage** (Phase 2).

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
- **Always prioritize BLE/GATT** for battery reading, even if a Classic interface exists (more battery-efficient for peripherals).

---

### ADR-004 — Correct GATT Characteristic Handling
**0x2A19 (Battery Level) is the primary source for battery percentage.** 0x2A1B (Battery Power State) is **metadata only** (charging state, power source) and **must not be treated as a percentage source**.

**Rule:**
- **0x2A19 (Battery Level):** Primary source for battery **percentage (0–100%)**.
- **0x2A1B (Battery Power State):** Supplemental **metadata** (charging state, power source).
- **Do NOT** use `0x2A1B` as a fallback for battery percentage.

---

### ADR-005 — Physical Device Identity Normalization
A single physical device may appear as **multiple `DeviceInformation` entries** in Windows. To avoid duplicates, we **normalize device identities** using **ContainerId as the primary key** (handles Random Private Addresses).

**Rule:**
- **Primary key:** `ContainerId` (stable across RPA changes).
- **Secondary key:** MAC address (fallback, may change for privacy-enabled devices).
- **Update MAC** if it changes (for RPA-enabled devices).

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
- **No caching of `BluetoothLEDevice` objects** (prevents peripheral sleep blocking).
- **Cache knowledge, not objects** (e.g., "Device supports GATT 0x2A19").

---

### ADR-008 — Graceful Degradation
If a protocol fails to read battery for a device, the app must **continue trying other protocols** without blocking the UI or crashing.

**Rule:**
- **Never fail silently** (log warnings for debugging).
- **Always try the next protocol** in the fallback chain.
- **Treat missing battery data as `null`** (not an error).
- **Skip devices after 3 consecutive failures** (configurable).

---

### ADR-009 — Realistic Performance Targets
Battery monitoring is **not a real-time system**. Latency is acceptable if it doesn’t block the UI.

**Targets:**
- **Ideal:** <2s (cached knowledge, no radio wakeups).
- **Acceptable:** <5s (some uncached reads).
- **Degraded:** <10s (skip slow protocols).
- **Per-operation timeout:** 2s (hard limit for WinRT calls).

---

### ADR-010 — SynchronizationContext Over Control.Invoke
All UI updates must use `SynchronizationContext.Post`.

---

### ADR-011 — Single Source of Alert Truth
`PollingOrchestrator` is the **only authority** on alerts.

