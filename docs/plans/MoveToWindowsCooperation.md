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
| `DeviceInformation.FindAllAsync` | All paired/remembered devices (BLE + Classic) | ⚠️ | `Windows.Devices.Enumeration` | Used **only on startup/resume/desync** |
| PnP Device Watcher | Real-time device additions/removals | ❌ | `Windows.Devices.Enumeration` | **Maintains live device set** |
| `BluetoothLEDevice` | BLE devices | ✅ | `Windows.Devices.Bluetooth` | **Correct API for BLE** |
