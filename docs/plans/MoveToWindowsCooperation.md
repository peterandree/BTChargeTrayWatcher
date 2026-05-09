# Feature Plan: Move to Windows Cooperation for Battery Monitoring

---

## Goal

**Rely on Windows’ built-in Bluetooth device discovery** as the primary source of truth, and **only augment with edge-case handling** to ensure comprehensive battery monitoring. This strategy **reduces Bluetooth radio pressure and saves battery** while maintaining full coverage for all devices used with the computer.

**Key Principle:**
> *"Windows discovers the devices; we read the battery."*

**What This Means:**
✅ **Use Windows’ device list** (`DeviceInformation.FindAllAsync` + PnP Watcher) as the **primary source** for Bluetooth devices.
✅ **Read battery levels** from each device using the appropriate protocol (GATT, Classic, HID, AVRCP, HFP).
✅ **Handle edge cases** where Windows doesn’t expose battery (e.g., Broadcom stack, vendor-specific protocols).
❌ **Do NOT scan for unpaired devices in range** (e.g., BLE advertisements for unknown devices).
❌ **Do NOT duplicate Windows’ discovery work** (e.g., custom device scanning).

**Why This Approach?**
- **Lower Bluetooth radio usage** → Better battery life and fewer disconnections.
- **Simpler code** → Less complexity, fewer bugs, easier maintenance.
- **More reliable** → Uses Windows’ tested device enumeration.
- **Full coverage** → Edge-case handling ensures no device is missed.

---

## Background and Constraints

### Current Implementation
The project currently supports:
- **GATT Battery Service (0x180F)** for BLE devices.
- **Classic Bluetooth (SetupAPI + WMI)** for paired Classic BT devices.

**Problem:**
The existing approach **assumes responsibility for device discovery**, which:
- **Duplicates Windows’ work** (inefficient).
- **Increases Bluetooth radio usage** (drains battery).
- **Adds complexity** (custom scanning logic for edge cases).

### Windows’ Built-in Capabilities
Windows **already discovers and tracks** Bluetooth devices via:

| Mechanism | Devices Covered | Battery Access? | API |
|-----------|-----------------|-----------------|-----|
| `DeviceInformation.FindAllAsync` | All paired/remembered devices (BLE + Classic) | ⚠️ Partial | `Windows.Devices.Enumeration` |
| PnP Device Watcher | Real-time device additions/removals | ❌ No (triggers scans) | `Windows.Devices.Enumeration` |
| GATT (0x180F) | BLE devices with Battery Service | ✅ Yes | `Windows.Devices.Bluetooth.GenericAttributeProfile` |
| WMI/SetupAPI | Classic Bluetooth devices | ⚠️ Partial | `SetupAPI` + `Win32_Battery` |
| HID | Keyboards, mice, gamepads | ✅ Yes | `Windows.Devices.HumanInterfaceDevice` |

**Key Insight:**
Windows **already maintains a list of all Bluetooth devices** used with the computer (paired or previously connected). We can **leverage this list** and focus solely on **reading battery levels**.

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
- **Secondary:** Protocol-specific battery reading (GATT, Classic, HID, AVRCP, HFP).
- **No custom device discovery** (e.g., no `BluetoothLEAdvertisementWatcher` for unpaired devices).

---

### ADR-003 — Protocol Fallback Chain
To ensure **full battery coverage**, each device must be **tried against all supported protocols** in a prioritized order until battery data is found.

**Rule:** The fallback chain for reading battery from a device is:
1. **GATT (0x180F)** → Most BLE devices.
2. **Classic (WMI/SetupAPI)** → Paired Classic BT devices.
3. **HID** → Keyboards, mice, gamepads.
4. **AVRCP** → Audio devices (headphones, speakers).
5. **HFP** → Legacy audio devices (headsets).
6. **Vendor-Specific** → Optional, for devices with proprietary battery reporting.

---

### ADR-004 — Polling Over Push
The project uses a **polling-based approach** (60-second interval) for battery monitoring, as documented in [ADR-003](../adr/adr-003-polling-over-push.md). This decision must be respected.

**Rule:** All battery reads must integrate into the existing **60-second polling cycle**.

---

### ADR-005 — Minimal Bluetooth Radio Usage
To **save battery and avoid disconnections**, the app must:
- **Reuse existing connections** (via `GattConnectionCache`).
- **Limit concurrent Bluetooth operations** (e.g., max 2–3 at once).
- **Skip scans on battery power** (for optional protocols like HFP/AVRCP).
- **Throttle retries** for failed connections.

**Rule:** All protocol readers must respect radio usage limits and power-aware throttling.

---

### ADR-006 — Graceful Degradation
If a protocol fails to read battery for a device, the app must **continue trying other protocols** without blocking the UI or crashing.

**Rule:**
- **Never fail silently** (log warnings for debugging).
- **Always try the next protocol** in the fallback chain.
- **Treat missing battery data as `null`** (not an error).

---

### ADR-010 — SynchronizationContext Over Control.Invoke
All UI updates must be dispatched through the existing `SynchronizationContext.Post` pattern (via `ScanCoordinator`). No direct `Control.Invoke` or `Dispatcher.Invoke` calls may be introduced.

**Rule:** New UI updates must use the existing `PostToUi` pattern.

---

### ADR-011 — Single Source of Alert Truth
The `PollingOrchestrator` remains the **only authority** on alert state. New battery data sources must not introduce separate alert logic.

**Rule:** All battery data, regardless of source, must be processed by `PollingOrchestrator.ClassifyBatteryState`.

---

### ADR-012 — Two Distinct Settings Events
Settings changes (e.g., thresholds, ignored devices) are propagated via the existing `ThresholdSettings.Changed` event. New battery monitoring methods must not introduce new settings events unless absolutely necessary.

**Rule:** If new settings are required, they must be added to `ThresholdSettings` and use the existing event mechanism.

---

## Architecture Overview

### High-Level Design
```
Windows Device List (DeviceInformation + PnP Watcher)
       ↓
[DeviceEnumerator] → Gets all Bluetooth devices from Windows
       ↓
[BatteryReaderOrchestrator] → Tries all protocols per device (GATT → Classic → HID → AVRCP → HFP)
       ↓
[BluetoothBatteryMonitor] → Processes results, applies hysteresis, triggers alerts
       ↓
[ScanCoordinator] → Updates UI via SynchronizationContext
       ↓
[ScanWindow / TrayApp] → Displays battery data
```

**Key Components:**

| Component | Purpose | Files |
|-----------|---------|-------|
| `DeviceEnumerator` | Enumerates Bluetooth devices from Windows | `src/Monitoring/DeviceEnumerator.cs` |
| `DeviceWatcherService` | Monitors PnP events for real-time updates | `src/Monitoring/DeviceWatcherService.cs` |
| `BatteryReaderOrchestrator` | Orchestrates protocol fallback chain | `src/Monitoring/BatteryReaderOrchestrator.cs` |
| `GattBatteryReader` | Reads battery via GATT (0x180F) | `src/Monitoring/Gatt/GattBatteryReader.cs` |
| `ClassicBatteryReader` | Reads battery via WMI/SetupAPI | `src/Monitoring/Classic/ClassicBatteryReader.cs` |
| `HidBatteryReader` | Reads battery via HID reports | `src/Monitoring/Hid/HidBatteryReader.cs` |
| `AvrcpBatteryReader` | Reads battery via AVRCP | `src/Monitoring/Avrcp/AvrcpBatteryReader.cs` |
| `HfpBatteryReader` | Reads battery via HFP | `src/Monitoring/Hfp/HfpBatteryReader.cs` |
| `VendorBatteryReader` | Reads battery via vendor-specific APIs | `src/Monitoring/Vendor/VendorBatteryReader.cs` |

---

## Proposed Implementation

---

### Step 1: Device Enumeration via Windows
**Goal:** Get the **complete list of Bluetooth devices** from Windows, including:
- Paired BLE devices.
- Paired Classic Bluetooth devices.
- HID devices (keyboards, mice).
- Audio devices (headphones, speakers).

#### Implementation

1. **Create `DeviceEnumerator`** to fetch the device list from Windows:

```csharp
/// <summary>
/// Enumerates Bluetooth devices from Windows using DeviceInformation APIs.
/// This is the primary source of truth for device discovery.
/// </summary>
public class DeviceEnumerator
{
    /// <summary>
    /// Gets all paired/remembered Bluetooth devices from Windows.
    /// </summary>
    public async Task<IReadOnlyList<DeviceInformation>> GetBluetoothDevicesAsync()
    {
        // Get all paired/remembered Bluetooth devices
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector);

        // Filter to only Bluetooth devices (exclude non-BT)
        return devices.Where(IsBluetoothDevice).ToList();
    }

    /// <summary>
    /// Gets a specific Bluetooth device by its ID.
    /// </summary>
    public async Task<DeviceInformation?> GetDeviceByIdAsync(string deviceId)
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromId(deviceId);
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices.FirstOrDefault();
    }

    private static bool IsBluetoothDevice(DeviceInformation device)
    {
        // Check if the device is a Bluetooth device by its interface class GUID
        if (device.Properties.TryGetValue("System.Devices.InterfaceClassGuid", out var ifaceGuid))
        {
            return ifaceGuid == BluetoothDevice.BluetoothDeviceInterfaceClassGuid;
        }
        return false;
    }
}
```

2. **Enhance `DeviceWatcherService`** to integrate with `DeviceEnumerator` and provide real-time updates:

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

    public event Action<DeviceInformation> DeviceAdded;
    public event Action<DeviceInformation> DeviceRemoved;

    public DeviceWatcherService(DeviceEnumerator deviceEnumerator)
    {
        _deviceEnumerator = deviceEnumerator;
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        _watcher = DeviceInformation.CreateWatcher(selector);
        _watcher.Added += OnDeviceAdded;
        _watcher.Removed += OnDeviceRemoved;
        _watcher.Updated += OnDeviceUpdated;
        _watcher.Start();
    }

    /// <summary>
    /// Gets the current list of Bluetooth devices from Windows.
    /// </summary>
    public async Task<IReadOnlyList<DeviceInformation>> GetCurrentDevicesAsync()
    {
        return await _deviceEnumerator.GetBluetoothDevicesAsync();
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
    {
        lock (_currentDevices) _currentDevices.Add(device);
        DeviceAdded?.Invoke(device);
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate deviceUpdate)
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

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate deviceUpdate)
    {
        // Handle updates (e.g., name changes, connection state changes)
        // For simplicity, we treat updates as removals + additions
        OnDeviceRemoved(sender, deviceUpdate);
        var updatedDevice = _deviceEnumerator.GetDeviceByIdAsync(deviceUpdate.Id).Result;
        if (updatedDevice != null)
        {
            OnDeviceAdded(sender, updatedDevice);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Stop();
    }
}
```

#### Files Changed

| File | Change |
|------|--------|
| `src/Monitoring/DeviceEnumerator.cs` | New file: Enumerates Bluetooth devices from Windows. |
| `src/Monitoring/DeviceWatcherService.cs` | Enhanced: Integrates with `DeviceEnumerator`. |

---

### Step 2: Battery Reader Orchestrator
**Goal:** For each device from Windows, **try all supported protocols** in the fallback chain until battery data is found.

#### Implementation

1. **Create `BatteryReaderOrchestrator`** to coordinate protocol fallbacks:

```csharp
/// <summary>
/// Orchestrates the protocol fallback chain to read battery from a device.
/// Tries protocols in order of priority (GATT → Classic → HID → AVRCP → HFP → Vendor).
/// </summary>
public class BatteryReaderOrchestrator
{
    private readonly IBatteryReader[] _readers;
    private readonly SemaphoreSlim _bluetoothSemaphore = new(2); // Max 2 concurrent BT ops

    public BatteryReaderOrchestrator(
        GattBatteryReader gattReader,
        ClassicBatteryReader classicReader,
        HidBatteryReader hidReader,
        AvrcpBatteryReader avrcpReader,
        HfpBatteryReader hfpReader,
        VendorBatteryReader vendorReader)
    {
        _readers = new IBatteryReader[]
        {
            gattReader,
            classicReader,
            hidReader,
            avrcpReader,
            hfpReader,
            vendorReader
        };
    }

    /// <summary>
    /// Reads battery from a device using the protocol fallback chain.
    /// </summary>
    /// <param name="device">The device to read battery from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DeviceBatteryInfo with battery data, or null if no battery data is available.</returns>
    public async Task<DeviceBatteryInfo> ReadBatteryAsync(DeviceInformation device, CancellationToken ct)
    {
        foreach (var reader in _readers)
        {
            try
            {
                await _bluetoothSemaphore.WaitAsync(ct);
                try
                {
                    var result = await reader.TryReadDeviceAsync(device, ct);
                    if (result != null)
                    {
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
                Log.Warning(ex, $