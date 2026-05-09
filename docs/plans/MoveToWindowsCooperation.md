# Feature Plan: Move to Windows Cooperation for Battery Monitoring

---

## Goal

**Rely on Windows’ built-in Bluetooth device discovery** as the primary source of truth, and **only implement the minimal set of protocols needed to cover the most common devices** (GATT, Classic, HID). Augment with edge-case handling (AVRCP, HFP) **only if users report missing battery data** for specific devices. This strategy **reduces Bluetooth radio pressure and saves battery** while ensuring full coverage for devices used with the computer.

**Key Principle:**
> *"Windows discovers the devices; we read the battery—**starting with the 3 protocols that matter**."*

**What This Means:**
✅ **Use Windows’ device list** (`DeviceInformation.FindAllAsync` + PnP Watcher) as the **primary source** for Bluetooth devices.
✅ **Read battery levels** from each device using **GATT, Classic, and HID** (covers ~95% of devices).
✅ **Add AVRCP/HFP later** if users report missing battery for audio devices.
❌ **Do NOT scan for unpaired devices in range** (e.g., BLE advertisements for unknown devices).
❌ **Do NOT duplicate Windows’ discovery work** (e.g., custom device scanning).

**Why This Approach?**
- **Lower Bluetooth radio usage** → Better battery life and fewer disconnections.
- **Simpler code** → Less complexity, fewer bugs, easier maintenance.
- **More reliable** → Uses Windows’ tested device enumeration.
- **Practical coverage** → Focus on what users actually need.

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
Windows **already maintains a list of all Bluetooth devices** used with the computer (paired or previously connected). We can **leverage this list** and focus solely on **reading battery levels** from these devices.

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
- **Secondary:** Protocol-specific battery reading (GATT, Classic, HID).
- **No custom device discovery** (e.g., no `BluetoothLEAdvertisementWatcher` for unpaired devices).

---

### ADR-003 — Minimal Protocol Coverage First
To ensure **practical coverage with minimal complexity**, the app must first implement support for the **three most common protocols** (GATT, Classic, HID) before adding support for edge cases (AVRCP, HFP, Vendor-Specific).

**Rule:** The initial implementation must support:
1. **GATT (0x180F)** → Most BLE devices (headphones, mice, keyboards).
2. **Classic (WMI/SetupAPI)** → Paired Classic BT devices (older headsets).
3. **HID** → Keyboards, mice, gamepads.

AVRCP, HFP, and Vendor-Specific support may be added **later if users report missing battery data** for specific devices.

---

### ADR-004 — Polling Over Push (Clarified)
The project uses a **polling-based approach** (60-second interval) for battery monitoring, as documented in [ADR-003](../adr/adr-003-polling-over-push.md). **PnP Device Watcher is a supplementary mechanism** that triggers immediate scans for new/updated devices but **does not replace polling**.

**Rule:**
- **Primary mechanism:** Polling every 60s (`PollingOrchestrator` fires alerts).
- **Supplementary mechanism:** PnP Watcher triggers **UI updates only** (no alerts) for new/updated devices.
- **No conflict:** `PollingOrchestrator` remains the **single source of truth** for alert state (ADR-011).

---

### ADR-005 — Minimal Bluetooth Radio Usage
To **save battery and avoid disconnections**, the app must:
- **Reuse existing connections** (via `GattConnectionCache`).
- **Limit concurrent Bluetooth operations** (e.g., max 2–3 at once).
- **Skip scans on battery power** (for optional protocols).
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
[BatteryReaderOrchestrator] → Tries GATT → Classic → HID (Phase 1)
       ↓
[BluetoothBatteryMonitor] → Processes results, applies hysteresis, triggers alerts
       ↓
[ScanCoordinator] → Updates UI via SynchronizationContext
       ↓
[ScanWindow / TrayApp] → Displays battery data

Optional (Future):
[BatteryReaderOrchestrator] → + AVRCP → HFP (Phase 2, if needed)
```

**Key Components:**

| Component | Purpose | Priority | Files |
|-----------|---------|----------|-------|
| `DeviceEnumerator` | Enumerates Bluetooth devices from Windows | ⭐⭐⭐⭐⭐ | `src/Monitoring/DeviceEnumerator.cs` |
| `DeviceWatcherService` | Monitors PnP events for real-time updates | ⭐⭐⭐⭐⭐ | `src/Monitoring/DeviceWatcherService.cs` |
| `BatteryReaderOrchestrator` | Orchestrates protocol fallback chain | ⭐⭐⭐⭐⭐ | `src/Monitoring/BatteryReaderOrchestrator.cs` |
| `GattBatteryReader` | Reads battery via GATT (0x180F) | ⭐⭐⭐⭐⭐ | `src/Monitoring/Gatt/GattBatteryReader.cs` |
| `ClassicBatteryReader` | Reads battery via WMI/SetupAPI | ⭐⭐⭐⭐⭐ | `src/Monitoring/Classic/ClassicBatteryReader.cs` |
| `HidBatteryReader` | Reads battery via HID reports | ⭐⭐⭐⭐⭐ | `src/Monitoring/Hid/HidBatteryReader.cs` |
| `AvrcpBatteryReader` | Reads battery via AVRCP | ⭐⭐ (Future) | `src/Monitoring/Avrcp/AvrcpBatteryReader.cs` |
| `HfpBatteryReader` | Reads battery via HFP | ⭐⭐ (Future) | `src/Monitoring/Hfp/HfpBatteryReader.cs` |

---

## Proposed Implementation

---

### Step 1: Device Enumeration via Windows
**Goal:** Get the **complete list of Bluetooth devices** from Windows, including:
- Paired BLE devices.
- Paired Classic Bluetooth devices.
- HID devices (keyboards, mice).

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
        // Check if the device is a Bluetooth device by its interface class GUID.
        // Note: WMI often returns GUIDs as strings, so we compare as strings.
        if (device.Properties.TryGetValue("System.Devices.InterfaceClassGuid", out var ifaceGuidObj))
        {
            var bluetoothGuid = BluetoothDevice.BluetoothDeviceInterfaceClassGuid.ToString("B").ToUpper();
            var deviceGuid = ifaceGuidObj.ToString().ToUpper();
            return deviceGuid == bluetoothGuid;
        }

        // Fallback: Check if the device is a Bluetooth device by its name or class
        return device.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || device.IsKind(DeviceClass.Bluetooth);
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

    private async void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate deviceUpdate)
    {
        // Handle updates (e.g., name changes, connection state changes).
        // Remove the old device and add the updated one asynchronously.
        lock (_currentDevices)
        {
            var oldDevice = _currentDevices.FirstOrDefault(d => d.Id == deviceUpdate.Id);
            if (oldDevice != null)
            {
                _currentDevices.Remove(oldDevice);
            }
        }

        // Fetch the updated device asynchronously (no .Result deadlock!)
        var updatedDevice = await _deviceEnumerator.GetDeviceByIdAsync(deviceUpdate.Id);
        if (updatedDevice != null)
        {
            lock (_currentDevices) _currentDevices.Add(updatedDevice);
            DeviceAdded?.Invoke(updatedDevice); // Treat as "new" for UI purposes
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Stop();
    }
}
```

**Key Fixes:**
- **No `.Result` deadlock:** `OnDeviceUpdated` now uses `await` instead of `.Result`.
- **Robust GUID comparison:** `IsBluetoothDevice` compares GUIDs as strings (WMI returns them as strings).

#### Files Changed

| File | Change |
|------|--------|
| `src/Monitoring/DeviceEnumerator.cs` | New file: Enumerates Bluetooth devices from Windows. |
| `src/Monitoring/DeviceWatcherService.cs` | Enhanced: Integrates with `DeviceEnumerator`, fixes `.Result` deadlock and GUID comparison. |

---

### Step 2: Battery Reader Orchestrator
**Goal:** For each device from Windows, **try the core protocols (GATT → Classic → HID)** in order until battery data is found.

#### Implementation

1. **Create `BatteryReaderOrchestrator`** to coordinate the protocol fallback chain:

```csharp
/// <summary>
/// Orchestrates the protocol fallback chain to read battery from a device.
/// Tries protocols in order of priority (GATT → Classic → HID).
/// AVRCP/HFP may be added later if needed.
/// </summary>
public class BatteryReaderOrchestrator
{
    private readonly IBatteryReader[] _readers;
    private readonly SemaphoreSlim _bluetoothSemaphore = new(2); // Max 2 concurrent BT ops

    public BatteryReaderOrchestrator(
        GattBatteryReader gattReader,
        ClassicBatteryReader classicReader,
        HidBatteryReader hidReader)
    {
        _readers = new IBatteryReader[]
        {
            gattReader,
            classicReader,
            hidReader
        };
    }

    /// <summary>
    /// Reads battery from a device using the protocol fallback chain.
    /// </summary>
    /// <param name="device">The device to read battery from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DeviceBatteryInfo with battery data, or a default info with null battery if no data is available.</returns>
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
                Log.Warning(ex, $"Failed to read battery from {device.Name} using {reader.GetType().Name}");
            }
        }
        return new DeviceBatteryInfo(device.Id, device.Name, null, null, BatterySource.Unknown);
    }
}
```

2. **Update `IBatteryReader`** to support device-specific reads:

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
    /// <param name="ct">Cancellation token.</param>
    /// <returns>DeviceBatteryInfo if battery data is available, otherwise null.</returns>
    Task<DeviceBatteryInfo?> TryReadDeviceAsync(DeviceInformation device, CancellationToken ct);
}
```

#### Files Changed

| File | Change |
|------|--------|
| `src/Monitoring/BatteryReaderOrchestrator.cs` | New file: Orchestrates protocol fallback chain (GATT → Classic → HID). |
| `src/Monitoring/IBatteryReader.cs` | Modified: Add `TryReadDeviceAsync`. |

---

### Step 3: Protocol-Specific Readers (Core: GATT, Classic, HID)
Each reader implements `IBatteryReader` and **tries to read battery from a given `DeviceInformation` object**. If it fails or the device doesn’t support the protocol, it returns `null`.

#### A. GATT Battery Reader

```csharp
/// <summary>
/// Reads battery from BLE devices using the GATT Battery Service (0x180F).
/// </summary>
public class GattBatteryReader : IBatteryReader
{
    private readonly GattConnectionCache _connectionCache;

    public GattBatteryReader(GattConnectionCache connectionCache)
    {
        _connectionCache = connectionCache;
    }

    public async Task<DeviceBatteryInfo?> TryReadDeviceAsync(DeviceInformation device, CancellationToken ct)
    {
        try
        {
            // Check if the device is a BLE device
            if (!device.Properties.TryGetValue("System.Devices.Bluetooth.DeviceAddress", out _))
            {
                return null;
            }

            var bluetoothDevice = await BluetoothDevice.FromIdAsync(device.Id);
            if (bluetoothDevice == null)
            {
                return null;
            }

            // Use cached connection if available
            var batteryService = await _connectionCache.GetServiceAsync(bluetoothDevice, GattServiceUuids.Battery);
            if (batteryService == null)
            {
                return null;
            }

            var batteryCharacteristic = batteryService.GetCharacteristics(GattCharacteristicUuids.BatteryLevel).FirstOrDefault();
            if (batteryCharacteristic == null)
            {
                return null;
            }

            var result = await batteryCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
            {
                return null;
            }

            return new DeviceBatteryInfo(
                device.Id,
                device.Name,
                result.Value[0], // Battery % (0-100)
                null, // IsCharging not available via GATT 0x180F
                BatterySource.Gatt);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, $"GATT read failed for {device.Name}");
            return null;
        }
    }
}
```

#### B. Classic Battery Reader

```csharp
/// <summary>
/// Reads battery from Classic Bluetooth devices using WMI/SetupAPI.
/// </summary>
public class ClassicBatteryReader : IBatteryReader
{
    public async Task<DeviceBatteryInfo?> TryReadDeviceAsync(DeviceInformation device, CancellationToken ct)
    {
        try
        {
            // Use WMI to read battery for Classic Bluetooth devices
            var battery = await WmiBatteryReader.ReadBatteryAsync(device.Id);
            if (battery == null)
            {
                return null;
            }

            return new DeviceBatteryInfo(
                device.Id,
                device.Name,
                battery.Value.Level,
                battery.Value.IsCharging,
                BatterySource.Classic);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, $"Classic BT read failed for {device.Name}");
            return null;
        }
    }

    /// <summary>
    /// Helper class to read battery from WMI.
    /// </summary>
    private static class WmiBatteryReader
    {
        public static async Task<(int Level, bool IsCharging)?> ReadBatteryAsync(string deviceId)
        {
            try
            {
                // WMI query to find battery for the device
                var query = $@"SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery WHERE DeviceID LIKE '%{deviceId.Replace("\\", "\\\\")}%';
                using var searcher = new ManagementObjectSearcher(query);
                var results = searcher.Get();

                foreach (ManagementObject battery in results)
                {
                    var level = battery["EstimatedChargeRemaining"] as uint?;
                    var status = battery["BatteryStatus"] as uint?;

                    if (level.HasValue)
                    {
                        // BatteryStatus values:
                        // 1 = Discharging, 2 = AC power, 3 = Fully Charged, 4 = Low, 5 = Critical
                        // 6 = Charging, 7 = Charging and High, 8 = Charging and Low, 9 = Charging and Critical
                        var isCharging = status.HasValue && (status == 2 || (status >= 6 && status <= 9));
                        return (Convert.ToInt32(level.Value), isCharging);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to read battery from WMI");
            }
            return null;
        }
    }
}
```

#### C. HID Battery Reader

```csharp
/// <summary>
/// Reads battery from HID devices (keyboards, mice, gamepads) via HID reports or GATT 0x2A1B.
/// </summary>
public class HidBatteryReader : IBatteryReader
{
    public async Task<DeviceBatteryInfo?> TryReadDeviceAsync(DeviceInformation device, CancellationToken ct)
    {
        try
        {
            // Only try HID for HID-class devices
            if (!device.IsKind(DeviceClass.HumanInterfaceDevice))
            {
                return null;
            }

            // Try HID reports first
            var hidDevice = await HidDevice.FromIdAsync(device.Id, FileAccessMode.Read);
            if (hidDevice != null)
            {
                var battery = ReadBatteryFromHidReport(hidDevice);
                if (battery != null)
                {
                    return new DeviceBatteryInfo(
                        device.Id,
                        device.Name,
                        battery.Value.Battery,
                        battery.Value.IsCharging,
                        BatterySource.Hid);
                }
            }

            // Fall back to GATT 0x2A1B (Battery Power State) if the device supports BLE
            var bluetoothDevice = await BluetoothDevice.FromIdAsync(device.Id);
            if (bluetoothDevice != null)
            {
                var batteryService = await bluetoothDevice.GetGattServiceAsync(new Guid("0000180F-0000-1000-8000-00805F9B34FB"));
                if (batteryService != null)
                {
                    var batteryPowerStateChar = batteryService.GetCharacteristics(new Guid("00002A1B-0000-1000-8000-00805F9B34FB")).FirstOrDefault();
                    if (batteryPowerStateChar != null)
                    {
                        var result = await batteryPowerStateChar.ReadValueAsync(BluetoothCacheMode.Uncached);
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

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, $"HID read failed for {device.Name}");
            return null;
        }
    }

    private static (int Battery, bool IsCharging)? ReadBatteryFromHidReport(HidDevice device)
    {
        // Implementation depends on the device's HID report format.
        // This is a placeholder; actual implementation would parse vendor-specific reports.
        // Example: Logitech devices often report battery in usage page 0xFF00.
        try
        {
            // Placeholder: Return null for now
            return null;
        }
        catch
        {
            return null;
        }
    }
}
```

#### Files Changed

| File | Change |
|------|--------|
| `src/Monitoring/Gatt/GattBatteryReader.cs` | Modified: Implement `TryReadDeviceAsync`. |
| `src/Monitoring/Classic/ClassicBatteryReader.cs` | Modified: Implement `TryReadDeviceAsync`. |
| `src/Monitoring/Hid/HidBatteryReader.cs` | New file: Implement `IBatteryReader` for HID devices. |

---

### Step 4: Integration with BluetoothBatteryMonitor
**Goal:** Replace the existing polling logic with the new **Windows-first approach**.

#### Implementation

```csharp
/// <summary>
/// Monitors Bluetooth device battery levels using Windows' device list as the primary source.
/// </summary>
public class BluetoothBatteryMonitor : IAsyncDisposable
{
    private readonly DeviceWatcherService _deviceWatcherService;
    private readonly BatteryReaderOrchestrator _batteryReaderOrchestrator;
    private readonly Timer _pollingTimer;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _scanSemaphore = new(1); // Prevent overlapping scans

    public BluetoothBatteryMonitor(
        DeviceWatcherService deviceWatcherService,
        BatteryReaderOrchestrator batteryReaderOrchestrator)
    {
        _deviceWatcherService = deviceWatcherService;
        _batteryReaderOrchestrator = batteryReaderOrchestrator;
        _pollingTimer = new Timer(OnPollingTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));

        // Subscribe to PnP events (triggers UI updates only, no alerts)
        _deviceWatcherService.DeviceAdded += OnDeviceAdded;
        _deviceWatcherService.DeviceRemoved += OnDeviceRemoved;
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
        // Get current devices from Windows
        var currentDevices = await _deviceWatcherService.GetCurrentDevicesAsync();
        var results = new List<DeviceBatteryInfo>();

        foreach (var device in currentDevices)
        {
            // Skip if scan was cancelled
            if (_cts.Token.IsCancellationRequested) break;

            var batteryInfo = await _batteryReaderOrchestrator.ReadBatteryAsync(device, _cts.Token);
            results.Add(batteryInfo);
        }

        // Update UI and alert state via PollingOrchestrator
        await PollingOrchestrator.ProcessResultsAsync(results);
    }

    private async void OnDeviceAdded(DeviceInformation device)
    {
        // Trigger a one-time scan for the new device (UI update only, no alert)
        await _scanSemaphore.WaitAsync();
        try
        {
            var batteryInfo = await _batteryReaderOrchestrator.ReadBatteryAsync(device, _cts.Token);
            ScanCoordinator.OnDeviceBatteryUpdated(batteryInfo);
        }
        finally
        {
            _scanSemaphore.Release();
        }
    }

    private void OnDeviceRemoved(DeviceInformation device)
    {
        // Notify UI to remove the device
        ScanCoordinator.OnDeviceRemoved(device.Id);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _pollingTimer?.Dispose();
        _cts.Dispose();
        _scanSemaphore.Dispose();
        await _deviceWatcherService.DisposeAsync();
    }
}
```

**Key Notes:**
- **PnP events** trigger **UI updates only** (no alerts). Alerts are **only fired by `PollingOrchestrator`** (ADR-011).
- **Polling** remains the primary mechanism (ADR-004).
- **Radio throttling** via `_scanSemaphore` (ADR-005).

#### Files Changed

| File | Change |
|------|--------|
| `src/Monitoring/BluetoothBatteryMonitor.cs` | Modified: Integrate `DeviceWatcherService` and `BatteryReaderOrchestrator`. |

---

### Step 5: UI Integration
**Goal:** Update the UI to reflect the new architecture (no changes to the user-facing behavior).

#### Implementation

1. **Update `ScanCoordinator`** to handle results from `BluetoothBatteryMonitor`:

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

    public void OnDeviceRemoved(string deviceId)
    {
        _uiContext.Post(_ => _scanWindow.RemoveDevice(deviceId), null);
    }
}
```

2. **Update `ScanWindow`** to show loading indicators and battery data:
   - No changes needed to the existing UI logic.

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
    Classic,
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
  "MaxConcurrentBluetoothOperations": 2,
  "ScanTimeoutSeconds": 2
}
```

**Notes:**
- `EnableHidBatteryMonitoring` defaults to `true` (HID is a core protocol).
- `MaxConcurrentBluetoothOperations` limits radio usage to avoid contention (ADR-005).
- AVRCP/HFP/Vendor settings are **not included** (not implemented in Phase 1).

---

## Acceptance Criteria

### Phase 1 (Core Implementation)
1. **Windows-First Discovery:**
   - Device list is **always sourced from Windows** (`DeviceInformation` + PnP Watcher).
   - No custom device scanning is performed.

2. **Protocol Coverage:**
   - **GATT + Classic + HID** cover **95%+ of user devices**.
   - Battery data is **never cached**; all reads are fresh.

3. **Performance:**
   - Full scan (5–10 devices) completes in **<500ms**.
   - **No deadlocks** (fixed `.Result` bug in `DeviceWatcherService`).
   - **No radio contention** (max 2 concurrent operations).

4. **Error Handling:**
   - Failures in individual protocols are **logged but do not block** the scan.
   - Missing battery data is treated as `null` (unknown).

5. **UI Consistency:**
   - Battery data from all sources is displayed **uniformly** in the scan window and tray tooltip.
   - **Loading indicators** are shown during scans.

6. **Real-Time Updates:**
   - New devices are **automatically detected** via PnP Watcher and scanned immediately (UI update only).
   - Disconnected devices are **removed from the UI** within one poll cycle.
   - **No alerts** are fired for PnP-triggered scans (ADR-011).

7. **Backward Compatibility:**
   - All existing functionality (GATT, Classic) continues to work unchanged.
   - Existing `DeviceBatteryInfo` construction sites compile without changes.

---

## Files Changed Summary

### New Files
| File | Purpose |
|------|---------|
| `src/Monitoring/DeviceEnumerator.cs` | Enumerates Bluetooth devices from Windows. |
| `src/Monitoring/BatteryReaderOrchestrator.cs` | Orchestrates protocol fallback chain (GATT → Classic → HID). |
| `src/Monitoring/Hid/HidBatteryReader.cs` | Reads battery via HID reports. |
| `src/Monitoring/BatterySource.cs` | Enum for battery data sources. |

### Modified Files
| File | Change |
|------|--------|
| `src/Monitoring/DeviceWatcherService.cs` | Enhanced: Integrates with `DeviceEnumerator`, fixes `.Result` deadlock and GUID comparison. |
| `src/Monitoring/BluetoothBatteryMonitor.cs` | Modified: Uses `DeviceWatcherService` and `BatteryReaderOrchestrator`. |
| `src/Monitoring/IBatteryReader.cs` | Modified: Add `TryReadDeviceAsync`. |
| `src/Monitoring/Gatt/GattBatteryReader.cs` | Modified: Implement `TryReadDeviceAsync`. |
| `src/Monitoring/Classic/ClassicBatteryReader.cs` | Modified: Implement `TryReadDeviceAsync`. |
| `src/Monitoring/DeviceBatteryInfo.cs` | Add `BatterySource? Source = null` parameter. |
| `src/Tray/ScanCoordinator.cs` | Modified: Handle results from `BluetoothBatteryMonitor`. |
| `src/Settings/ThresholdSettings.cs` | Add new settings for enabling/disabling protocols. |

### Future Files (Not Implemented in Phase 1)
| File | Purpose |
|------|---------|
| `src/Monitoring/Avrcp/AvrcpBatteryReader.cs` | Reads battery via AVRCP (add later if needed). |
| `src/Monitoring/Hfp/HfpBatteryReader.cs` | Reads battery via HFP (add later if needed). |
| `src/Monitoring/Vendor/VendorBatteryReader.cs` | Reads battery via vendor-specific APIs (add later if needed). |

---

## Open Questions

1. **HID Battery Reporting:**
   - How should we handle HID devices that report battery via **vendor-specific reports** (e.g., Logitech, Razer)?
   - **Proposed Solution:** Start with **GATT 0x2A1B** (Battery Power State) for HID devices that support BLE. Add vendor-specific HID report parsing later if users report missing battery data.

2. **AVRCP/HFP Success Rate:**
   - Should we enable AVRCP/HFP by default, or keep them disabled until users request it?
   - **Proposed Solution:** **Do not implement in Phase 1**. Monitor user feedback and add only if needed.

3. **Radio Contention:**
   - How to handle cases where the Bluetooth radio is busy (e.g., during file transfer)?
   - **Proposed Solution:** Retry after a delay (e.g., 1–2 seconds) or skip the scan for that cycle.

---

## Next Steps

### Phase 1: Core Implementation (High Priority)
1. **Implement `DeviceEnumerator`** (foundational for Windows-first discovery).
2. **Fix `DeviceWatcherService`** (remove `.Result` deadlock, fix GUID comparison).
3. **Implement `BatteryReaderOrchestrator`** (GATT → Classic → HID fallback chain).
4. **Update protocol readers** (GATT, Classic, HID) to implement `TryReadDeviceAsync`.
5. **Integrate with `BluetoothBatteryMonitor`** and test performance.
6. **Update `DeviceBatteryInfo`** to include `BatterySource`.
7. **Add Settings** for enabling/disabling protocols.
8. **Test with real devices** (GATT, Classic, HID).

### Phase 2: Optional Enhancements (Low Priority)
- **Add AVRCP Battery Reader** (if users report missing battery for audio devices).
- **Add HFP Battery Reader** (if users report missing battery for legacy headsets).
- **Add Vendor-Specific Battery Reader** (if users report missing battery for specific vendor devices).

---

## Migration Guide (From Current Implementation)

### For Existing Users
- **No action required**: The new implementation will **automatically use Windows’ device list** and fall back to the same protocols as before.
- **Performance improvement**: Faster scans and lower battery usage due to reduced radio contention.

### For Developers
1. **Replace `DeviceAggregationPipeline`** with `DeviceEnumerator` + `BatteryReaderOrchestrator`.
2. **Update protocol readers** to implement `TryReadDeviceAsync` (instead of `ReadAllAsync`).
3. **Remove redundant scanning logic** (e.g., no custom device discovery).
4. **Test edge cases** (Broadcom stack, HID devices, audio devices).

---

## Why This Approach Wins

| **Metric** | **Old Approach (Custom Scanning)** | **New Approach (Windows Cooperation)** |
|------------|------------------------------------|----------------------------------------|
| **Bluetooth Radio Usage** | High (duplicate scanning) | Low (Windows does discovery) |
| **Battery Impact** | Medium-High | Low |
| **Code Complexity** | High | Low |
| **Device Coverage** | ~95% | ~95% (Phase 1), ~99% (Phase 2) |
| **Real-Time Updates** | ✅ Yes | ✅ Yes (PnP Watcher) |
| **Maintainability** | Medium | High |
| **Reliability** | Medium (custom scanning bugs) | High (Windows’ tested enumeration) |

**Result:**
✅ **Lower Bluetooth radio usage** → Better battery life.
✅ **Simpler code** → Fewer bugs, easier maintenance.
✅ **Practical coverage** → Focus on what users actually need.
✅ **Faster scans** → No duplicate discovery work.
