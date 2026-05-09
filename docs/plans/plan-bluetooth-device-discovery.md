# Feature Plan: Expand Bluetooth Device Discovery and Battery State Capture

---

## Goal

Expand the range of Bluetooth devices that can be discovered and have their battery states monitored by incorporating additional methods beyond the existing **GATT Battery Service (0x180F)** and **Classic Bluetooth (SetupAPI + WMI)** approaches. This plan aims to maximize device coverage while adhering to the existing architecture, design decisions, and polling-based monitoring strategy.

---

## Background and Constraints

### Current Implementation
The project currently supports two methods for monitoring Bluetooth device battery states:

| Method | Transport | Battery Level | Charging State | Source |
|--------|-----------|---------------|----------------|--------|
| **GATT Battery Service** | BLE | ✅ `0x2A19` | ⚠️ `0x2BEA` (optional, rare) | `Windows.Devices.Bluetooth.GenericAttributeProfile` |
| **Classic Bluetooth (SetupAPI + WMI)** | Classic BT | ✅ `Win32_Battery.EstimatedChargeRemaining` | ✅ `Win32_Battery.BatteryStatus == 2` | SetupAPI P/Invoke + WMI |

### Limitations
- **GATT Battery Service (`0x180F`)** is not universally supported by all BLE devices.
- **Classic Bluetooth (SetupAPI + WMI)** relies on Windows device enumeration, which may not always report battery levels for all devices, especially when not actively connected.
- Some devices (e.g., HID devices, audio devices, or vendor-specific peripherals) use alternative methods to expose battery state.

### Key Observations
- **HID Devices (Keyboards, Mice, Gamepads):** Often report battery via **HID reports** or **vendor-specific GATT characteristics**.
- **Audio Devices (Headphones, Speakers):** May use **AVRCP (Audio/Video Remote Control Profile)** or **HFP (Hands-Free Profile)** or **vendor-specific protocols** (e.g., Sony, Bose).
- **BLE Advertisements:** Some devices broadcast battery level in **manufacturer data** or **service data** within BLE advertisements.
- **Vendor-Specific APIs:** Manufacturers like Intel, Broadcom, or Qualcomm provide proprietary SDKs for advanced Bluetooth features, including battery monitoring.
- **PnP Device Watcher:** Windows provides a **`DeviceWatcher`** API to monitor Plug and Play (PnP) events for Bluetooth devices, which can detect connection/disconnection and property changes.

---

## Design Decisions That Govern This Feature

### ADR-001 — Single Non-Nullable Constructor per Class
All data models (e.g., `DeviceBatteryInfo`) must adhere to the principle of **immutability** and **single non-nullable constructors**. Any new fields (e.g., `IsCharging`, `BatterySource`) must be added as optional parameters with defaults to avoid breaking existing code.

**Rule:** New fields in `DeviceBatteryInfo` or related records must be added as optional constructor parameters with default values (e.g., `bool? IsCharging = null`).

---

### ADR-002 — Dual Bluetooth Reader Strategy
The existing architecture isolates **GATT** and **Classic Bluetooth** readers as independent implementations of `IBatteryReader`. This isolation must be preserved. New methods for battery monitoring (e.g., HID, AVRCP, BLE advertisements) must be implemented as **separate readers** or **extensions to existing readers**, but they must not couple the GATT and Classic strategies.

**Rule:** New battery monitoring methods must be implemented as:
- A new `IBatteryReader` implementation (e.g., `HidBatteryReader`, `AvrcpBatteryReader`), **or**
- An extension to an existing reader (e.g., adding BLE advertisement scanning to `GattBatteryReader`), **but only if the extension is logically aligned with the existing reader's responsibilities**.

---
### ADR-003 — Polling Over Push
The project uses a **polling-based approach** (60-second interval) for battery monitoring, as documented in [ADR-003](docs/adr/adr-003-polling-over-push.md). This decision must be respected:
- **No event-driven subscriptions** (e.g., GATT notifications) are introduced for battery monitoring.
- **Polling intervals** may be adjusted for specific methods (e.g., BLE advertisements may require more frequent scanning).
- **Cache invalidation** must be handled for stale connections (e.g., `GattConnectionCache.RemoveDevice` for GATT devices).

**Rule:** All new battery monitoring methods must integrate into the existing **60-second polling cycle** or define a justified alternative interval.

---

### ADR-004 — Threshold Hysteresis
The `PollingOrchestrator` is the **single source of truth** for alert state classification (e.g., `BatteryAlertState.Normal`, `Low`, `High`). Any new battery data (e.g., from HID or AVRCP) must be processed through the same hysteresis logic.

**Rule:** New battery monitoring methods must feed data into `PollingOrchestrator` via the existing `DeviceAggregationPipeline`. No new alert logic may be introduced outside `PollingOrchestrator`.

---

### ADR-010 — SynchronizationContext Over Control.Invoke
All UI updates must be dispatched through the existing `SynchronizationContext.Post` pattern (via `ScanCoordinator`). No direct `Control.Invoke` or `Dispatcher.Invoke` calls may be introduced.

**Rule:** New UI updates (e.g., displaying battery data from HID devices) must use the existing `PostToUi` pattern.

---

### ADR-011 — Single Source of Alert Truth
The `PollingOrchestrator` remains the **only authority** on alert state. New battery data sources must not introduce separate alert logic.

**Rule:** All battery data, regardless of source, must be processed by `PollingOrchestrator.ClassifyBatteryState`.

---

### ADR-012 — Two Distinct Settings Events
Settings changes (e.g., thresholds, ignored devices) are propagated via the existing `ThresholdSettings.Changed` event. New battery monitoring methods must not introduce new settings events unless absolutely necessary.

**Rule:** If new settings are required (e.g., to enable/disable a specific monitoring method), they must be added to `ThresholdSettings` and use the existing event mechanism.

---

## Proposed Methods for Expanded Device Discovery and Battery Monitoring

The following methods are **realistic options** for expanding device coverage. Each method is evaluated for **feasibility**, **coverage**, and **alignment with existing design decisions**.

---

### Method 1: HID Battery Reporting
**Goal:** Capture battery state from **HID-class Bluetooth devices** (e.g., keyboards, mice, gamepads) that do not support the GATT Battery Service.

#### Background
- Many HID devices (e.g., Logitech, Microsoft, Razer) report battery via **HID reports** or **vendor-specific GATT characteristics**.
- Windows exposes HID device battery via **`Windows.Devices.HumanInterfaceDevice`** (UWP) or **Win32 HID APIs**.
- Some HID devices use the **`0x2A1B` (Battery Power State)** GATT characteristic, which is already partially supported in the existing GATT reader (see [plan-charging-indicator.md](docs/plans/plan-charging-indicator.md)).

#### Implementation
1. **Create a new `HidBatteryReader`** implementing `IBatteryReader`.
   - Use **`Windows.Devices.HumanInterfaceDevice`** (UWP) or **Win32 HID APIs** to enumerate HID devices.
   - For each HID device, attempt to read battery level from:
     - **HID reports** (vendor-specific usage pages).
     - **GATT `0x2A1B` (Battery Power State)** if the device supports BLE.
   - Return `DeviceBatteryInfo` with `Battery` and `IsCharging` (if available).

2. **Integrate into `DeviceAggregationPipeline`**:
   - Add `HidBatteryReader.ReadAllAsync` to the existing `Task.WhenAll` call in `DeviceAggregationPipeline.ReadMergedAsync`.
   - Merge results with GATT and Classic readers, deduplicating by `DeviceId`.

3. **Handle Cache Invalidation**:
   - HID devices do not require connection caching (unlike GATT), so no changes to `GattConnectionCache` are needed.

4. **UI Updates**:
   - No changes required. The existing `ScanWindow` and `TrayApp` will display battery data from `DeviceBatteryInfo`.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Hid/HidBatteryReader.cs` | New file: Implement `IBatteryReader` for HID devices. |
| `src/Monitoring/DeviceAggregationPipeline.cs` | Add `HidBatteryReader.ReadAllAsync` to the pipeline. |
| `src/Monitoring/IBatteryReader.cs` | No changes (interface already supports new reader). |

#### Acceptance Criteria
- HID devices (e.g., Logitech MX Master, Microsoft Sculpt Keyboard) that do not support GATT Battery Service now have their battery levels displayed in the scan window and tray tooltip.
- Battery data from HID devices is merged with GATT and Classic data, with no duplicates.
- No new UI logic is introduced; existing `DeviceBatteryInfo` handling suffices.

---

### Method 2: AVRCP Battery Reporting
**Goal:** Capture battery state from **audio devices** (e.g., headphones, speakers) that use the **AVRCP (Audio/Video Remote Control Profile)**.

#### Background
- AVRCP is a Bluetooth profile used for remote control of audio/video devices.
- Some audio devices (e.g., Sony WH-1000XM4, Bose QC45) report battery level via **AVRCP** or **vendor-specific extensions**.
- AVRCP battery reporting is **not standardized** and often vendor-specific.

#### Implementation
1. **Create a new `AvrcpBatteryReader`** implementing `IBatteryReader`.
   - Use **`Windows.Devices.Bluetooth.Rfcomm`** or **Win32 Bluetooth APIs** to interact with AVRCP.
   - For each paired audio device, attempt to read battery level via AVRCP commands.
   - Fall back to **vendor-specific GATT characteristics** (e.g., Sony's `0xFE00` service) if AVRCP is unavailable.

2. **Integrate into `DeviceAggregationPipeline`**:
   - Add `AvrcpBatteryReader.ReadAllAsync` to the pipeline.
   - Merge results with GATT, Classic, and HID readers.

3. **Handle Vendor-Specific Logic**:
   - Maintain a **mapping of vendor IDs to known battery characteristics** (e.g., Sony, Bose, Jabra).
   - If a device's vendor ID matches a known entry, attempt to read its proprietary battery characteristic.

4. **UI Updates**:
   - No changes required. Battery data will be displayed via existing `DeviceBatteryInfo` handling.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Avrcp/AvrcpBatteryReader.cs` | New file: Implement `IBatteryReader` for AVRCP devices. |
| `src/Monitoring/DeviceAggregationPipeline.cs` | Add `AvrcpBatteryReader.ReadAllAsync` to the pipeline. |
| `src/Monitoring/VendorBatteryMappings.cs` | New file: Map vendor IDs to proprietary battery characteristics. |

#### Acceptance Criteria
- Audio devices (e.g., Sony WH-1000XM4) that do not support GATT Battery Service now have their battery levels displayed.
- Vendor-specific battery characteristics are read for known devices.
- No duplicates in the merged device list.

---

### Method 3: HFP Battery Reporting
**Goal:** Capture battery state from **audio devices** (e.g., headsets) that use the **HFP (Hands-Free Profile)**.

#### Background
- HFP is a Bluetooth profile used for call handling.
- Some audio devices (e.g., Plantronics, Jabra, older Sony/Bose models) report battery level via **HFP AT commands** (e.g., `AT+BTRH?`).
- HFP battery reporting is **not standardized** and often vendor-specific.

#### Implementation
1. **Create a new `HfpBatteryReader`** implementing `IBatteryReader`.
   - Use **`Windows.Devices.Bluetooth.Rfcomm`** to connect to the **HFP service UUID** (`0x111F`).
   - Send **AT commands** (e.g., `AT+BTRH?`) to query battery level.
   - Parse the response (e.g., `+BTRH: 1,80` = 80% battery).
   - Return `DeviceBatteryInfo` with `Battery` and `IsCharging` (if available).

2. **Integrate into `DeviceAggregationPipeline`**:
   - Add `HfpBatteryReader.ReadAllAsync` to the pipeline.
   - Merge results with GATT, Classic, HID, and AVRCP readers.

3. **Handle Vendor-Specific Logic**:
   - Maintain a **mapping of vendor IDs to known HFP battery commands** (e.g., Plantronics, Jabra).
   - If a device's vendor ID matches a known entry, attempt to read its proprietary battery command.

4. **UI Updates**:
   - No changes required. Battery data will be displayed via existing `DeviceBatteryInfo` handling.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Hfp/HfpBatteryReader.cs` | New file: Implement `IBatteryReader` for HFP devices. |
| `src/Monitoring/DeviceAggregationPipeline.cs` | Add `HfpBatteryReader.ReadAllAsync` to the pipeline. |
| `src/Monitoring/VendorBatteryMappings.cs` | Extend to include HFP battery commands. |

#### Acceptance Criteria
- Audio devices (e.g., Plantronics Voyager, Jabra Elite) that do not support GATT or AVRCP Battery Service now have their battery levels displayed.
- Vendor-specific HFP battery commands are read for known devices.
- No duplicates in the merged device list.

---

### Method 4: BLE Advertisement Scanning
**Goal:** Capture battery state from **BLE devices that broadcast battery level in advertisements** (e.g., beacons, wearables).

#### Background
- Some BLE devices (e.g., Tile trackers, certain wearables) include battery level in their **advertisement packets** (manufacturer data or service data).
- Windows provides **`BluetoothLEAdvertisementWatcher`** (UWP) to scan for BLE advertisements.
- Advertisement scanning is **passive** (no connection required) but **short-range**.

#### Implementation
1. **Create a new `BleAdvertisementBatteryReader`** implementing `IBatteryReader`.
   - Use **`BluetoothLEAdvertisementWatcher`** to scan for BLE advertisements.
   - Parse **manufacturer data** and **service data** for known battery level formats (e.g., Tile's manufacturer data includes battery %).
   - Map advertisement data to `DeviceId` (if possible) or use a **temporary identifier** (e.g., MAC address) for deduplication.

2. **Integrate into `DeviceAggregationPipeline`**:
   - Add `BleAdvertisementBatteryReader.ReadAllAsync` to the pipeline.
   - Merge results with other readers, prioritizing **connected device data** over advertisement data (since advertisements may be stale).

3. **Adjust Polling Interval**:
   - BLE advertisement scanning may require a **shorter interval** (e.g., 10–30 seconds) to capture transient advertisements.
   - Use a **separate timer** for advertisement scanning, but synchronize results with the main 60-second poll.

4. **Handle Deduplication**:
   - Advertisement-based battery data may not include a stable `DeviceId`. Use **MAC address** or **advertisement address** as a fallback for deduplication.

5. **UI Updates**:
   - Display advertisement-based battery data in the scan window with a **visual indicator** (e.g., "✧" to denote advertisement-sourced data).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/BleAdvertisement/BleAdvertisementBatteryReader.cs` | New file: Implement `IBatteryReader` for BLE advertisements. |
| `src/Monitoring/DeviceAggregationPipeline.cs` | Add `BleAdvertisementBatteryReader.ReadAllAsync` to the pipeline. |
| `src/Monitoring/PollingOrchestrator.cs` | Add a separate timer for advertisement scanning. |
| `src/Tray/ScanWindow.cs` | Add "✧" indicator for advertisement-sourced battery data. |

#### Acceptance Criteria
- BLE devices that broadcast battery level in advertisements (e.g., Tile trackers) now have their battery levels displayed.
- Advertisement data is merged with connected device data, with connected data taking precedence.
- Advertisement scanning does not interfere with the main 60-second polling cycle.

---

### Method 5: PnP Device Watcher
**Goal:** Improve **device discovery** and **connection state monitoring** using Windows' **`DeviceWatcher`** API.

#### Background
- **`DeviceWatcher`** (from `Windows.Devices.Enumeration`) monitors **PnP events** for device additions, removals, and property changes.
- Can detect **Bluetooth device connection/disconnection** in real-time.
- Useful for **triggering immediate scans** when a new device is paired or connected.

#### Implementation
1. **Create a new `DeviceWatcherService`**:
   - Use **`DeviceInformation.CreateWatcher`** with the Bluetooth device selector:
     ```csharp
     var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
     var watcher = DeviceInformation.CreateWatcher(selector);
     ```
   - Subscribe to `Added`, `Removed`, and `Updated` events.
   - On `Added` or `Updated`, trigger a **manual scan** via `ScanCoordinator.RequestOpenScanWindow`.

2. **Integrate with `BluetoothBatteryMonitor`**:
   - Start the `DeviceWatcherService` when `BluetoothBatteryMonitor` initializes.
   - Stop the watcher when the monitor is disposed.

3. **Handle Edge Cases**:
   - Avoid **duplicate scans** if multiple `Added`/`Updated` events fire in quick succession.
   - Ensure the watcher is **re-started** after suspend/resume (see [ADR-003](docs/adr/adr-003-polling-over-push.md)).

4. **UI Updates**:
   - No direct changes, but the scan window will open automatically when new devices are detected.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/DeviceWatcherService.cs` | New file: Implement `DeviceWatcher` for Bluetooth devices. |
| `src/Monitoring/BluetoothBatteryMonitor.cs` | Start/stop `DeviceWatcherService` with the monitor. |
| `src/Tray/ScanCoordinator.cs` | Trigger manual scan on `DeviceWatcher` events. |

#### Acceptance Criteria
- New Bluetooth devices are **automatically detected** and trigger a scan window to display their battery state.
- The watcher is **suspended/resumed** with the system power state.
- No duplicate scans are triggered for rapid device connection/disconnection events.

---

### Method 6: Vendor-Specific APIs (Optional)
**Goal:** Support **proprietary battery monitoring** for devices from specific manufacturers (e.g., Intel, Broadcom, Logitech, Sony).

#### Background
- Some manufacturers provide **SDKs or APIs** for advanced Bluetooth features, including battery monitoring.
- Examples:
  - **Intel Wireless Bluetooth**: Proprietary APIs for Intel-based Bluetooth adapters.
  - **Logitech BLE**: Some Logitech devices use proprietary GATT services for battery reporting.
  - **Sony/Bose Audio**: Vendor-specific GATT characteristics for battery and charging state.

#### Implementation
1. **Create a `VendorBatteryReader`** implementing `IBatteryReader`.
   - Maintain a **registry of vendor-specific handlers** (e.g., `IVendorBatteryHandler` for Intel, Logitech, Sony).
   - For each paired device, check its **vendor ID** and delegate to the appropriate handler.

2. **Vendor-Specific Handlers**:
   - **IntelHandler**: Use Intel's proprietary API to read battery state.
   - **LogitechHandler**: Read from Logitech's proprietary GATT services (e.g., `0xFF00`).
   - **SonyHandler**: Read from Sony's proprietary characteristics (e.g., `0xFE00`).

3. **Integrate into `DeviceAggregationPipeline`**:
   - Add `VendorBatteryReader.ReadAllAsync` to the pipeline.
   - Merge results with other readers.

4. **Configuration**:
   - Add a **setting** to enable/disable vendor-specific monitoring (default: enabled).
   - Log **warnings** if a vendor-specific API fails to load (e.g., Intel SDK not installed).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Vendor/VendorBatteryReader.cs` | New file: Implement `IBatteryReader` for vendor-specific devices. |
| `src/Monitoring/Vendor/IVendorBatteryHandler.cs` | New file: Interface for vendor handlers. |
| `src/Monitoring/Vendor/IntelBatteryHandler.cs` | New file: Intel-specific handler. |
| `src/Monitoring/Vendor/LogitechBatteryHandler.cs` | New file: Logitech-specific handler. |
| `src/Monitoring/Vendor/SonyBatteryHandler.cs` | New file: Sony-specific handler. |
| `src/Monitoring/DeviceAggregationPipeline.cs` | Add `VendorBatteryReader.ReadAllAsync` to the pipeline. |
| `src/Settings/ThresholdSettings.cs` | Add `EnableVendorBatteryMonitoring` setting. |

#### Acceptance Criteria
- Devices from supported vendors (e.g., Intel, Logitech, Sony) have their battery levels displayed even if they do not support standard GATT/Classic methods.
- Vendor-specific monitoring can be **disabled** via settings.
- Failures in vendor-specific APIs are **logged but do not crash** the application.

---

## Future Considerations

### Bluetooth LE Audio (LC3) Battery Reporting
- **Bluetooth LE Audio** (introduced in **Bluetooth 5.2**) includes a **new battery reporting mechanism** for hearing aids and other audio devices.
- This is **very new** (2020+) and **not widely adopted yet**, but may become relevant for future-proofing.
- **Action:** Monitor Bluetooth SIG specifications for LE Audio battery reporting. Skip implementation for now, but revisit if users report missing battery data for LE Audio devices.

### A2DP Battery Reporting
- **A2DP (Advanced Audio Distribution Profile)** is primarily for audio streaming, but **some devices** (e.g., certain Sony headphones) may expose battery via **A2DP vendor-specific extensions**.
- This is **very rare** and often overlaps with AVRCP or GATT.
- **Action:** Skip implementation for now. If users report missing battery data for A2DP devices, investigate whether they can be covered by AVRCP or GATT.

### Google Fast Pair Battery Reporting
- **Google Fast Pair** (used by Pixel Buds, some Sony/Bose headphones) exposes battery level via a **proprietary Bluetooth service**.
- This is **Android-specific** and **not directly accessible on Windows**, but some devices may still advertise battery in a way that can be read via **BLE advertisements** or **GATT**.
- **Action:** Skip implementation for now. If users report missing battery data for Fast Pair devices, investigate whether they broadcast battery in advertisements.

---

## Prioritization and Implementation Order

The following table prioritizes the proposed methods based on **feasibility**, **coverage**, and **alignment with existing architecture**.

| Method | Effort | Coverage | Real-Time? | Priority | Notes |
|--------|--------|----------|------------|----------|-------|
| **PnP Device Watcher** | Low | Medium | ✅ Yes | **1 (High)** | Improves device discovery; minimal changes. |
| **HID Battery Reporting** | Medium | Medium | ❌ No | **2 (High)** | Covers keyboards/mice; aligns with existing GATT/Classic. |
| **HFP Battery Reporting** | Medium | Low | ❌ No | **3 (Medium)** | Covers legacy headsets; uses RFCOMM + AT commands. |
| **BLE Advertisement Scanning** | Medium | Low | ✅ Yes | **4 (Medium)** | Passive monitoring; limited to devices that broadcast battery. |
| **AVRCP Battery Reporting** | High | Low | ❌ No | **5 (Medium)** | Audio devices only; vendor-specific. |
| **Vendor-Specific APIs** | High | Low | ❌ No | **6 (Low)** | Hardware-dependent; optional. |

**Recommended Implementation Order:**
1. **PnP Device Watcher** (Quick win for device discovery).
2. **HID Battery Reporting** (Covers common peripherals).
3. **HFP Battery Reporting** (Covers legacy headsets).
4. **BLE Advertisement Scanning** (Passive monitoring for wearables/beacons).
5. **AVRCP Battery Reporting** (Audio devices).
6. **Vendor-Specific APIs** (Optional, for advanced users).

---

## Data Model Changes

### `DeviceBatteryInfo`
To support the new methods, extend `DeviceBatteryInfo` with the following **optional fields** (defaulting to `null` to maintain backward compatibility):

```csharp
public sealed record DeviceBatteryInfo(
    string DeviceId,
    string Name,
    int? Battery,
    bool? IsCharging = null,
    BatterySource? Source = null);  // New: Indicates the source of the battery data
```

**`BatterySource` Enum:**
```csharp
public enum BatterySource
{
    Unknown,
    Gatt,
    Classic,
    Hid,
    Avrcp,
    Hfp,
    BleAdvertisement,
    VendorSpecific
}
```

**Purpose:**
- Helps with **debugging** (e.g., "Why is this device's battery not updating?").
- Enables **UI indicators** (e.g., "✧" for BLE advertisements, "🎧" for AVRCP, "📞" for HFP).
- Preserves **immutability** (ADR-001).

---

### `DeviceAggregationPipeline`
Update the pipeline to:
1. **Track the source** of each battery reading.
2. **Prioritize connected data** over advertisement data (e.g., if a device is connected via GATT, use GATT data even if BLE advertisement data is available).
3. **Deduplicate by `DeviceId`** (or MAC address for advertisement data).

---

## Threading Model
All new methods must adhere to the existing **threading model**:
- **Polling** occurs on the **thread pool** (via `System.Threading.Timer`).
- **UI updates** are dispatched via `SynchronizationContext.Post` (ADR-010).
- **No blocking calls** on the UI thread.

---

## Settings Changes
Add the following settings to `ThresholdSettings` to control the new methods:

```json
{
  "Version": 2,
  "Low": 20,
  "High": 80,
  "EnableHidBatteryMonitoring": true,
  "EnableHfpBatteryMonitoring": true,
  "EnableAvrcpBatteryMonitoring": true,
  "EnableBleAdvertisementMonitoring": true,
  "EnableVendorBatteryMonitoring": false,
  "BleAdvertisementScanIntervalSeconds": 30
}
```

**Notes:**
- All new settings default to `true` (enabled) except `EnableVendorBatteryMonitoring` (disabled by default due to dependency risks).
- `BleAdvertisementScanIntervalSeconds` allows users to adjust the frequency of BLE advertisement scanning (default: 30 seconds).

---

## Acceptance Criteria (Overall)
1. **Backward Compatibility:**
   - All existing functionality (GATT, Classic) continues to work unchanged.
   - Existing `DeviceBatteryInfo` construction sites compile without changes.

2. **No New Dependencies:**
   - New methods use **existing Windows APIs** (UWP, Win32) or **open-source libraries** (e.g., 32feet.NET for HID).
   - Vendor-specific APIs are **optional** and gracefully degraded if unavailable.

3. **Performance:**
   - Polling intervals remain **configurable** and **non-blocking**.
   - BLE advertisement scanning does not **drain battery** or **overload the Bluetooth radio**.

4. **UI Consistency:**
   - Battery data from all sources is displayed **uniformly** in the scan window and tray tooltip.
   - Optional **source indicators** (e.g., "✧" for advertisements, "🎧" for AVRCP, "📞" for HFP) are added but do not clutter the UI.

5. **Error Handling:**
   - Failures in new methods (e.g., HID read error, AVRCP unsupported, HFP unsupported) are **logged** but do not crash the application.
   - Stale or missing data is treated as `null` (unknown) rather than an error.

6. **Testing:**
   - Unit tests are added for new readers (e.g., `HidBatteryReaderTests`, `AvrcpBatteryReaderTests`, `HfpBatteryReaderTests`).
   - Integration tests verify that battery data from all sources is **merged correctly** in `DeviceAggregationPipeline`.

---

## Files Changed Summary

### New Files
| File | Purpose |
|------|---------|
| `src/Monitoring/Hid/HidBatteryReader.cs` | HID battery monitoring. |
| `src/Monitoring/Avrcp/AvrcpBatteryReader.cs` | AVRCP battery monitoring. |
| `src/Monitoring/Hfp/HfpBatteryReader.cs` | HFP battery monitoring. |
| `src/Monitoring/BleAdvertisement/BleAdvertisementBatteryReader.cs` | BLE advertisement scanning. |
| `src/Monitoring/DeviceWatcherService.cs` | PnP device watcher for real-time discovery. |
| `src/Monitoring/Vendor/VendorBatteryReader.cs` | Vendor-specific battery monitoring. |
| `src/Monitoring/Vendor/IVendorBatteryHandler.cs` | Interface for vendor handlers. |
| `src/Monitoring/Vendor/IntelBatteryHandler.cs` | Intel-specific handler. |
| `src/Monitoring/Vendor/LogitechBatteryHandler.cs` | Logitech-specific handler. |
| `src/Monitoring/Vendor/SonyBatteryHandler.cs` | Sony-specific handler. |
| `src/Monitoring/BatterySource.cs` | Enum for battery data sources. |

### Modified Files
| File | Change |
|------|--------|
| `src/Monitoring/DeviceBatteryInfo.cs` | Add `BatterySource? Source = null` parameter. |
| `src/Monitoring/DeviceAggregationPipeline.cs` | Add new readers to the pipeline; prioritize connected data. |
| `src/Monitoring/PollingOrchestrator.cs` | Add timer for BLE advertisement scanning. |
| `src/Monitoring/BluetoothBatteryMonitor.cs` | Start/stop `DeviceWatcherService`. |
| `src/Tray/ScanCoordinator.cs` | Trigger manual scan on `DeviceWatcher` events. |
| `src/Tray/ScanWindow.cs` | Add source indicators (e.g., "✧" for advertisements, "🎧" for AVRCP, "📞" for HFP). |
| `src/Settings/ThresholdSettings.cs` | Add new settings for enabling/disabling methods. |

---

## Open Questions
1. **BLE Advertisement Deduplication:**
   - How should devices be deduplicated if they lack a stable `DeviceId` (e.g., only a MAC address is available)?
   - **Proposed Solution:** Use **MAC address** as a fallback `DeviceId` for advertisement-sourced data.

2. **Vendor-Specific API Dependencies:**
   - Should vendor-specific APIs (e.g., Intel SDK) be **bundled** with the application, or should users install them separately?
   - **Proposed Solution:** Treat vendor-specific APIs as **optional**. Log a warning if they are missing, but continue without them.

3. **AVRCP Complexity:**
   - AVRCP battery reporting is **highly vendor-specific**. Should we limit support to **known devices** (e.g., Sony, Bose) or attempt a **generic AVRCP implementation**?
   - **Proposed Solution:** Start with **known devices** and expand as users report compatibility.

4. **HFP Complexity:**
   - HFP battery reporting is **highly vendor-specific**. Should we limit support to **known devices** (e.g., Plantronics, Jabra) or attempt a **generic HFP implementation**?
   - **Proposed Solution:** Start with **known devices** and expand as users report compatibility.

5. **Performance Impact of BLE Advertisement Scanning:**
   - How will frequent BLE scanning (e.g., every 10 seconds) impact **battery life** on laptops?
   - **Proposed Solution:** Default to **30-second intervals** and allow users to adjust or disable it.

---

## Next Steps
1. **Implement PnP Device Watcher** (high priority, low effort).
2. **Implement HID Battery Reporting** (high priority, medium effort).
3. **Implement HFP Battery Reporting** (medium priority, medium effort).
4. **Update `DeviceBatteryInfo`** to include `BatterySource`.
5. **Add Settings** for enabling/disabling new methods.
6. **Test and Validate** with a variety of Bluetooth devices (HID, audio, wearables).
7. **Implement BLE Advertisement Scanning** (medium priority).
8. **Implement AVRCP Battery Reporting** (medium priority, if time permits).
9. **Implement Vendor-Specific APIs** (low priority, optional).