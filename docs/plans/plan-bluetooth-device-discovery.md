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

### ADR-013 — Phased Scanning for Responsiveness
To maintain a **responsive UI** while adding multiple new protocols, scanning must be **phased** to prioritize fast, high-success methods first, while slower methods run in the background. This ensures users see **immediate device lists** and **fresh battery data** without blocking the UI.

**Rule:** All new battery monitoring methods must be integrated into a **phased scanning system** that:
1. Prioritizes fast/high-success protocols (e.g., GATT, Classic, HID) in **Phase 2** to deliver fresh battery data quickly.
2. Uses **PnP Device Watcher** in **Phase 1** to show the device list immediately (without battery levels).
3. Runs slower/low-success protocols (e.g., AVRCP, HFP, BLE Ads) in **Phase 3+** in the background.
4. Merges results incrementally to update the UI as data becomes available.
5. Limits concurrent Bluetooth operations to avoid radio contention.
6. **Does not cache battery levels**, as the app's core purpose is real-time monitoring.

---

## Technical Specification: Phased Scanning for Bluetooth Battery Monitoring

### Goal
Ensure that adding multiple new Bluetooth battery monitoring protocols **does not degrade UX performance** (e.g., slow discovery, UI freezes, or Bluetooth radio contention). This is achieved through a **phased scanning approach**, where fast and high-success protocols run first, followed by slower or optional protocols in the background. **No battery levels are cached**, as the app's purpose is to monitor real-time changes.

---

### Background and UX Requirements
Before optimizations, discovery took **3–5 seconds**, which was unacceptable for users. After optimizations, discovery is **fast and responsive**. Adding 6+ new protocols risks **reintroducing latency** if not implemented carefully.

**Key Insights:**
- **~80% of devices** are covered by **GATT, Classic, and HID** (fast protocols).
- **~20% of devices** require **AVRCP, HFP, or BLE Ads** (slower protocols).
- **Users expect fresh, real-time battery data** when opening the scan window.

**UX Requirements:**
1. **Fast Battery Data:** Users must see **fresh battery levels within 200–500ms** of opening the scan window.
2. **Non-Blocking UI:** The UI must **never freeze** during a scan.
3. **No Bluetooth Radio Contention:** Scans must not **interfere with active Bluetooth connections** (e.g., audio streaming, file transfers).
4. **Graceful Degradation:** If a protocol fails or is slow, the scan must **continue without it**.
5. **No Stale Data:** Battery levels must **always be fresh** (no caching).

---

### Architecture: Phased Scanning Model
Scanning is divided into **4 phases**, each with a **time budget**, **priority level**, and **user visibility**:

| Phase | Protocols | Time Budget | Priority | User Visibility | Notes |
|-------|-----------|-------------|----------|-----------------|-------|
| **1** | PnP Watcher (device list) | ~50ms | Highest | Instant | Shows device list immediately (no battery levels). |
| **2** | GATT + Classic + HID | ~200–500ms | High | Immediate | Fresh battery data for most devices. |
| **3** | AVRCP + HFP | ~500ms–2s | Medium | Delayed | Slower, lower-success protocols (background). |
| **4** | BLE Ads + Vendor-Specific | ~1–3s | Low | Background | Optional, user-triggered. |

**Workflow:**
```
User Opens Scan Window
       ↓
Phase 1: Show Device List from PnP Watcher (Instant, no battery levels)
       ↓
Phase 2: Scan GATT/Classic/HID (Fast, ~500ms, fresh battery data)
       ↓
Phase 3: Scan AVRCP/HFP (Background, ~2s)
       ↓
Phase 4: Scan BLE Ads/Vendor (Optional, User-Triggered)
```

**Note:** The app **does not cache battery levels**, as its core purpose is real-time monitoring. The PnP Watcher provides the device list, and all protocols scan for fresh battery data on each poll cycle.

---

### Protocol Prioritization
Protocols are **prioritized by speed and success rate**:

| Protocol | Speed | Success Rate | Priority | Phase | Notes |
|----------|-------|--------------|----------|-------|-------|
| PnP Watcher | ⚡ Instant | 90%+ | 1 | 1 | Event-driven, device list only |
| GATT | ⚡ Fast | 80% | 2 | 2 | Primary method for BLE devices |
| Classic Bluetooth | ⚡ Fast | 70% | 2 | 2 | Paired devices only |
| HID | ⚡ Fast | 60% | 2 | 2 | Keyboards/mice |
| AVRCP | 🐢 Medium | 40% | 3 | 3 | Audio devices |
| HFP | 🐢 Slow | 20% | 3 | 3 | Legacy headsets |
| BLE Ads | ⚡ Fast* | 30% | 4 | 4 | Passive, high radio load |
| Vendor-Specific | 🐢 Slow | 10% | 4 | 4 | Optional |

**"*BLE Ads is fast per-scan but requires frequent scans (high radio load).**

---

### Implementation

#### 1. Core Components

##### A. `PhasedScanOrchestrator`
**Purpose:** Coordinates the phased scanning process and merges results incrementally.

**Responsibilities:**
- Trigger phased scans (Phase 1 → Phase 2 → Phase 3+).
- Merge results from all phases, deduplicating by `DeviceId`.
- Prioritize data sources (GATT/Classic > HID > AVRCP/HFP > BLE Ads > Vendor).
- Limit concurrent Bluetooth operations to avoid radio contention.

**Key Methods:**
```csharp
public class PhasedScanOrchestrator : IAsyncDisposable
{
    private readonly IBatteryReader[] _phase2Readers; // GATT, Classic, HID
    private readonly IBatteryReader[] _phase3Readers; // AVRCP, HFP
    private readonly IBatteryReader[] _phase4Readers; // BLE Ads, Vendor
    private readonly SemaphoreSlim _bluetoothSemaphore = new(2); // Max 2 concurrent BT ops
    private readonly CancellationTokenSource _cts = new();

    public async Task StartPhasedScanAsync(
        Action<IReadOnlyList<DeviceBatteryInfo>> onPhase1Complete,
        Action<IReadOnlyList<DeviceBatteryInfo>> onPhase2Complete,
        Action<IReadOnlyList<DeviceBatteryInfo>> onPhase3Complete)
    {
        // Phase 1: Instant (PnP device list only, no battery levels)
        var phase1Results = await _deviceWatcherService.GetCurrentDevicesAsync();
        onPhase1Complete?.Invoke(phase1Results);

        // Phase 2: Fast protocols (synchronous, fresh battery data)
        var phase2Results = await RunPhaseAsync(_phase2Readers, _cts.Token);
        onPhase2Complete?.Invoke(phase2Results);

        // Phase 3: Medium protocols (background)
        _ = Task.Run(async () =>
        {
            var phase3Results = await RunPhaseAsync(_phase3Readers, _cts.Token);
            onPhase3Complete?.Invoke(phase3Results);
        });
    }

    public async Task StartDeepScanAsync(Action<IReadOnlyList<DeviceBatteryInfo>> onComplete)
    {
        var phase4Results = await RunPhaseAsync(_phase4Readers, _cts.Token);
        onComplete?.Invoke(phase4Results);
    }

    private async Task<IReadOnlyList<DeviceBatteryInfo>> RunPhaseAsync(
        IBatteryReader[] readers, CancellationToken ct)
    {
        var tasks = readers.Select(reader => RunReaderAsync(reader, ct)).ToArray();
        await Task.WhenAll(tasks);
        return MergeAndDeduplicateResults(tasks.Select(t => t.Result).ToList());
    }

    private async Task<IReadOnlyList<DeviceBatteryInfo>> RunReaderAsync(
        IBatteryReader reader, CancellationToken ct)
    {
        await _bluetoothSemaphore.WaitAsync(ct);
        try
        {
            return await reader.ReadAllAsync(ct);
        }
        finally
        {
            _bluetoothSemaphore.Release();
        }
    }

    private IReadOnlyList<DeviceBatteryInfo> MergeAndDeduplicateResults(
        IReadOnlyList<IReadOnlyList<DeviceBatteryInfo>> results)
    {
        var merged = new Dictionary<string, DeviceBatteryInfo>();
        var sourcePriority = new Dictionary<BatterySource, int>
        {
            { BatterySource.Gatt, 0 },
            { BatterySource.Classic, 0 },
            { BatterySource.Hid, 1 },
            { BatterySource.Avrcp, 2 },
            { BatterySource.Hfp, 2 },
            { BatterySource.BleAdvertisement, 3 },
            { BatterySource.VendorSpecific, 3 },
            { BatterySource.Unknown, 4 }
        };

        foreach (var resultList in results)
        foreach (var deviceInfo in resultList)
        {
            if (!merged.TryGetValue(deviceInfo.DeviceId, out var existing) ||
                sourcePriority[deviceInfo.Source] < sourcePriority[existing.Source])
            {
                merged[deviceInfo.DeviceId] = deviceInfo;
            }
        }
        return merged.Values.ToList();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        _bluetoothSemaphore.Dispose();
    }
}
```

**Files:**
- `src/Monitoring/PhasedScanOrchestrator.cs` (New)

---

##### B. `ScanPhase` Enum
**Purpose:** Defines the scan phases for clarity.

```csharp
public enum ScanPhase
{
    Phase1, // Instant (PnP device list only)
    Phase2, // Fast (GATT/Classic/HID, fresh battery data)
    Phase3, // Medium (AVRCP/HFP)
    Phase4  // Slow (BLE Ads/Vendor)
}
```

**Files:**
- `src/Monitoring/ScanPhase.cs` (New)

---

#### 2. Integration Points

##### A. `DeviceWatcherService` (Enhanced)
**Changes:**
- Add `GetCurrentDevicesAsync()` to return the current list of Bluetooth devices (without battery levels).

```csharp
public class DeviceWatcherService : IAsyncDisposable
{
    private readonly DeviceWatcher _watcher;
    private readonly List<DeviceInformation> _currentDevices = new();

    public DeviceWatcherService()
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        _watcher = DeviceInformation.CreateWatcher(selector);
        _watcher.Added += OnDeviceAdded;
        _watcher.Removed += OnDeviceRemoved;
        _watcher.Updated += OnDeviceUpdated;
        _watcher.Start();
    }

    public async Task<IReadOnlyList<DeviceBatteryInfo>> GetCurrentDevicesAsync()
    {
        // Return device list only (no battery levels)
        return _currentDevices
            .Select(d => new DeviceBatteryInfo(d.Id, d.Name, null, null, null))
            .ToList();
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
    {
        lock (_currentDevices) _currentDevices.Add(device);
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate deviceUpdate)
    {
        lock (_currentDevices)
        {
            var device = _currentDevices.FirstOrDefault(d => d.Id == deviceUpdate.Id);
            if (device != null) _currentDevices.Remove(device);
        }
    }

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate deviceUpdate)
    {
        // Handle updates (e.g., name changes)
    }

    public void Dispose()
    {
        _watcher.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
    }
}
```

**Files Modified:**
- `src/Monitoring/DeviceWatcherService.cs` (Enhanced with `GetCurrentDevicesAsync`)

---

##### B. `BluetoothBatteryMonitor`
**Changes:**
- Replace polling logic with `PhasedScanOrchestrator`.
- Trigger phased scans on timer ticks.
- Handle deep scans separately.

```csharp
public class BluetoothBatteryMonitor : IAsyncDisposable
{
    private readonly PhasedScanOrchestrator _phasedScanOrchestrator;
    private readonly DeviceWatcherService _deviceWatcherService;
    private readonly Timer _pollingTimer;

    public BluetoothBatteryMonitor(
        PhasedScanOrchestrator phasedScanOrchestrator,
        DeviceWatcherService deviceWatcherService)
    {
        _phasedScanOrchestrator = phasedScanOrchestrator;
        _deviceWatcherService = deviceWatcherService;
        _pollingTimer = new Timer(OnPollingTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    }

    private async void OnPollingTick(object? state)
    {
        if (PowerStatus.IsBatteryPower && !UserIsActive)
        {
            return; // Skip scan if on battery and user is idle
        }
        await _phasedScanOrchestrator.StartPhasedScanAsync(
            onPhase1Complete: results => OnScanResults(ScanPhase.Phase1, results),
            onPhase2Complete: results => OnScanResults(ScanPhase.Phase2, results),
            onPhase3Complete: results => OnScanResults(ScanPhase.Phase3, results));
    }

    public async Task StartDeepScanAsync()
    {
        await _phasedScanOrchestrator.StartDeepScanAsync(results =>
            OnScanResults(ScanPhase.Phase4, results));
    }

    private void OnScanResults(ScanPhase phase, IReadOnlyList<DeviceBatteryInfo> results)
    {
        ScanCoordinator.OnScanResults(phase, results);
    }

    public async ValueTask DisposeAsync()
    {
        _pollingTimer?.Dispose();
        await _phasedScanOrchestrator.DisposeAsync();
        await _deviceWatcherService.DisposeAsync();
    }
}
```

**Files Modified:**
- `src/Monitoring/BluetoothBatteryMonitor.cs`

---

##### C. `ScanCoordinator`
**Changes:**
- Handle incremental UI updates as results arrive from each phase.
- Merge results from all phases.
- Show device list immediately from Phase 1 (no battery levels).

```csharp
public class ScanCoordinator
{
    private readonly SynchronizationContext _uiContext;
    private readonly ScanWindow _scanWindow;
    private readonly Dictionary<ScanPhase, IReadOnlyList<DeviceBatteryInfo>> _phaseResults = new();

    public ScanCoordinator(SynchronizationContext uiContext, ScanWindow scanWindow)
    {
        _uiContext = uiContext;
        _scanWindow = scanWindow;
    }

    public void OnScanResults(ScanPhase phase, IReadOnlyList<DeviceBatteryInfo> results)
    {
        _phaseResults[phase] = results;
        var mergedResults = MergePhaseResults();
        _uiContext.Post(_ => UpdateScanWindow(mergedResults), null);
    }

    private IReadOnlyList<DeviceBatteryInfo> MergePhaseResults()
    {
        var merged = new Dictionary<string, DeviceBatteryInfo>();
        foreach (var resultList in _phaseResults.Values)
        foreach (var deviceInfo in resultList)
        {
            if (!merged.ContainsKey(deviceInfo.DeviceId))
            {
                merged[deviceInfo.DeviceId] = deviceInfo;
            }
        }
        return merged.Values.ToList();
    }

    private void UpdateScanWindow(IReadOnlyList<DeviceBatteryInfo> results)
    {
        _scanWindow.UpdateDeviceList(results);
        if (_phaseResults.ContainsKey(ScanPhase.Phase2))
        {
            _scanWindow.HideLoadingIndicator();
        }
    }
}
```

**Files Modified:**
- `src/Tray/ScanCoordinator.cs`

---

##### D. `ScanWindow`
**Changes:**
- Show loading spinner during scans.
- Add "Deep Scan" button for Phase 4.
- Display device list immediately from Phase 1 (battery levels appear as they arrive from Phase 2+).

**UI Updates:**
- Add a **loading spinner** (`ProgressBar` with `Style = ProgressBarStyle.Marquee`).
- Add a **"Deep Scan" button** to trigger Phase 4.
- Add a **status label** (e.g., "Scanning devices…").
- Show battery levels as **"—%"** until fresh data arrives from Phase 2.

**Files Modified:**
- `src/Tray/ScanWindow.cs`

---

##### E. `BleAdvertisementBatteryReader`
**Changes:**
- Skip scanning if on battery power (configurable).
- Throttle scanning to 30-second intervals.

```csharp
public class BleAdvertisementBatteryReader : IBatteryReader
{
    private readonly PowerStatus _powerStatus;
    private readonly TimeSpan _scanInterval = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastScanTime;

    public async Task<IReadOnlyList<DeviceBatteryInfo>> ReadAllAsync(CancellationToken ct)
    {
        if (_powerStatus.IsBatteryPower)
        {
            return Array.Empty<DeviceBatteryInfo>(); // Skip on battery
        }

        if (DateTimeOffset.UtcNow - _lastScanTime < _scanInterval)
        {
            return Array.Empty<DeviceBatteryInfo>(); // Throttle
        }

        _lastScanTime = DateTimeOffset.UtcNow;
        return await ScanAdvertisementsAsync(ct);
    }
}
```

**Files Modified:**
- `src/Monitoring/BleAdvertisement/BleAdvertisementBatteryReader.cs`

---

#### 3. Throttling and Concurrency Control

##### A. Bluetooth Radio Throttling
- **`SemaphoreSlim`** limits concurrent Bluetooth operations to **2–3** at once.
- Applied in `PhasedScanOrchestrator.RunReaderAsync`.

##### B. Timeout Handling
- **2-second timeout** per protocol.
- Implemented via `CancellationTokenSource` in `RunReaderAsync`.

##### C. Power-Aware Scanning
- **Skip BLE Ads** if on battery power.
- **Increase polling interval** to 120s if system is idle.

**Code:**
```csharp
// In BluetoothBatteryMonitor.OnPollingTick
if (PowerStatus.IsBatteryPower && !UserIsActive)
{
    return; // Skip scan if on battery and user is idle
}
```

---

#### 4. Deduplication and Data Priority
- **Deduplicate by `DeviceId`** (fall back to MAC address for BLE Ads).
- **Prioritize data sources:**
  - **GATT/Classic > HID > AVRCP/HFP > BLE Ads > Vendor-Specific**.
- Implemented in `PhasedScanOrchestrator.MergeAndDeduplicateResults`.

---

#### 5. User Feedback
- **Loading Spinner:** Shown during Phase 2+ scans.
- **Status Updates:**
  - "Scanning devices…" (Phase 1)
  - "Reading battery levels…" (Phase 2)
  - "Checking audio devices…" (Phase 3)
  - "Ready" (All phases complete)
- **Deep Scan Button:** Triggers Phase 4 (BLE Ads + Vendor-Specific).

---

### Testing Requirements
1. **Performance Tests:**
   - Verify **Phase 1 < 50ms** (device list only).
   - Verify **Phase 2 < 500ms** (fresh battery data).
   - Verify **Phase 3 < 2s** (background).
2. **Stress Tests:**
   - Simulate **10+ devices** and verify **no UI freezes**.
   - Test **concurrent Bluetooth operations** (max 2–3).
3. **Battery Tests:**
   - Run **BLE Ads every 10s** on battery power and measure impact.
4. **User Tests:**
   - Validate that **device list appears instantly** (Phase 1).
   - Validate that **battery levels appear within 500ms** (Phase 2).
   - Validate that **slow protocols don’t block the UI** (Phase 3+).

---

### Rollout Plan
1. **Phase 1:** Implement **PnP Device Watcher + Phased Scanning Infrastructure** (low risk).
2. **Phase 2:** Add **GATT/Classic/HID** to Phase 2 and test performance.
3. **Phase 3:** Add **AVRCP/HFP** to Phase 3 and monitor for issues.
4. **Phase 4:** Enable **BLE Ads and Vendor-Specific** as opt-in features.

---

### Settings
Add to `ThresholdSettings`:
```json
{
  "PhasedScanning": {
    "EnablePhase2": true,
    "EnablePhase3": true,
    "EnablePhase4": false,
    "MaxConcurrentBluetoothOperations": 2,
    "ScanTimeoutSeconds": 2,
    "BleAdvertisementScanIntervalSeconds": 30
  }
}
```

---

## Proposed Methods for Expanded Device Discovery and Battery State Capture

The following methods are **realistic options** for expanding device coverage. Each method is evaluated for **feasibility**, **coverage**, and **alignment with existing design decisions**.

---

### Method 1: HID Battery Reporting
**Goal:** Capture battery state from **HID-class Bluetooth devices** (e.g., keyboards, mice, gamepads) that do not support the GATT Battery Service.

#### Background
- Many HID devices (e.g., Logitech, Microsoft, Razer) report battery via **HID reports** or **vendor-specific GATT characteristics**.
- Windows exposes HID device battery via **`Windows.Devices.HumanInterfaceDevice`** (UWP) or **Win32 HID APIs**.
- Some HID devices use the **`0x2A1B` (Battery Power State)** GATT characteristic.

#### Implementation
1. **Create a new `HidBatteryReader`** implementing `IBatteryReader`.
   - Use **`Windows.Devices.HumanInterfaceDevice`** or **Win32 HID APIs** to enumerate HID devices.
   - For each HID device, attempt to read battery level from:
     - **HID reports** (vendor-specific usage pages).
     - **GATT `0x2A1B` (Battery Power State)** if the device supports BLE.
   - Return `DeviceBatteryInfo` with `Battery` and `IsCharging` (if available).

2. **Integrate into `DeviceAggregationPipeline`:**
   - Add `HidBatteryReader.ReadAllAsync` to the existing `Task.WhenAll` call in `DeviceAggregationPipeline.ReadMergedAsync`.
   - Merge results with GATT and Classic readers, deduplicating by `DeviceId`.

3. **UI Updates:**
   - No changes required. The existing `ScanWindow` and `TrayApp` will display battery data from `DeviceBatteryInfo`.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Hid/HidBatteryReader.cs` | New file: Implement `IBatteryReader` for HID devices. |
| `src/Monitoring/DeviceAggregationPipeline.cs` | Add `HidBatteryReader.ReadAllAsync` to the pipeline. |

#### Acceptance Criteria
- HID devices (e.g., Logitech MX Master, Microsoft Sculpt Keyboard) that do not support GATT Battery Service now have their battery levels displayed in the scan window and tray tooltip.
- Battery data from HID devices is merged with GATT and Classic data, with no duplicates.

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

2. **Integrate into `DeviceAggregationPipeline`:**
   - Add `AvrcpBatteryReader.ReadAllAsync` to the pipeline.
   - Merge results with GATT, Classic, and HID readers.

3. **Handle Vendor-Specific Logic:**
   - Maintain a **mapping of vendor IDs to known battery characteristics** (e.g., Sony, Bose, Jabra).

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

### Method 3: BLE Advertisement Scanning
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

2. **Integrate into `DeviceAggregationPipeline`:**
   - Add `BleAdvertisementBatteryReader.ReadAllAsync` to the pipeline.
   - Merge results with other readers, prioritizing **connected device data** over advertisement data (since advertisements may be stale).

3. **Adjust Polling Interval:**
   - BLE advertisement scanning may require a **shorter interval** (e.g., 10–30 seconds) to capture transient advertisements.
   - Use a **separate timer** for advertisement scanning, but synchronize results with the main 60-second poll.

4. **Handle Deduplication:**
   - Advertisement-based battery data may not include a stable `DeviceId`. Use **MAC address** or **advertisement address** as a fallback for deduplication.

5. **UI Updates:**
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

### Method 4: PnP Device Watcher
**Goal:** Improve **device discovery** and **connection state monitoring** using Windows' **`DeviceWatcher`** API.

#### Background
- **`DeviceWatcher`** (from `Windows.Devices.Enumeration`) monitors **PnP events** for device additions, removals, and property changes.
- Can detect **Bluetooth device connection/disconnection** in real-time.
- Useful for **triggering immediate scans** when a new device is paired or connected.

#### Implementation
1. **Create a new `DeviceWatcherService`:**
   - Use **`DeviceInformation.CreateWatcher`** with the Bluetooth device selector:
     ```csharp
     var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
     var watcher = DeviceInformation.CreateWatcher(selector);
     ```
   - Subscribe to `Added`, `Removed`, and `Updated` events.
   - On `Added` or `Updated`, trigger a **manual scan** via `ScanCoordinator.RequestOpenScanWindow`.

2. **Integrate with `BluetoothBatteryMonitor`:**
   - Start the `DeviceWatcherService` when `BluetoothBatteryMonitor` initializes.
   - Stop the watcher when the monitor is disposed.

3. **Handle Edge Cases:**
   - Avoid **duplicate scans** if multiple `Added`/`Updated` events fire in quick succession.
   - Ensure the watcher is **re-started** after suspend/resume (see [ADR-003](docs/adr/adr-003-polling-over-push.md)).

4. **UI Updates:**
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

### Method 5: HFP Battery Reporting (Optional)
**Goal:** Capture battery state from **audio devices** (e.g., headsets) that use the **HFP (Hands-Free Profile)**.

#### Background
- HFP is a Bluetooth profile used for call handling.
- Some **legacy audio devices** (e.g., Plantronics, Jabra, older Sony/Bose models) report battery level via **HFP AT commands** (e.g., `AT+BTRH?`).
- HFP battery reporting is **not standardized** and often vendor-specific.
- **Realism on Windows:** Low (~10–20% of older headsets). Most modern headsets use GATT or AVRCP for battery reporting.

#### Implementation
1. **Create a new `HfpBatteryReader`** implementing `IBatteryReader`.
   - Use **`Windows.Devices.Bluetooth.Rfcomm`** to connect to the **HFP service UUID** (`0x111F`).
   - Send **AT commands** (e.g., `AT+BTRH?`) to query battery level.
   - Parse the response (e.g., `+BTRH: 1,80` = 80% battery).
   - Return `DeviceBatteryInfo` with `Battery` and `IsCharging` (if available).

2. **Integrate into `DeviceAggregationPipeline`:**
   - Add `HfpBatteryReader.ReadAllAsync` to the pipeline.
   - Merge results with GATT, Classic, HID, and AVRCP readers.

3. **Handle Vendor-Specific Logic:**
   - Maintain a **mapping of vendor IDs to known HFP battery commands** (e.g., Plantronics, Jabra).

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

2. **Vendor-Specific Handlers:**
   - **IntelHandler**: Use Intel's proprietary API to read battery state.
   - **LogitechHandler**: Read from Logitech's proprietary GATT services (e.g., `0xFF00`).
   - **SonyHandler**: Read from Sony's proprietary characteristics (e.g., `0xFE00`).

3. **Integrate into `DeviceAggregationPipeline`:**
   - Add `VendorBatteryReader.ReadAllAsync` to the pipeline.
   - Merge results with other readers.

4. **Configuration:**
   - Add a **setting** to enable/disable vendor-specific monitoring (default: disabled).
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
- **Realism on Windows:** Very low (~1–2% of users in 2026). Windows 11 22H2+ includes experimental LE Audio support, but it is not yet widely adopted.
- **Action:** Skip implementation for now. Most LE Audio devices will also support **GATT 0x180F** for backward compatibility. Revisit in 2027–2028 if adoption increases.

### A2DP Battery Reporting
- **A2DP (Advanced Audio Distribution Profile)** is primarily for audio streaming and **does not include battery reporting** in the standard.
- **Realism on Windows:** Nonexistent. A2DP is not designed for battery monitoring.
- **Action:** Skip implementation. Focus on **GATT, AVRCP, or HID** for audio devices.

### Google Fast Pair Battery Reporting
- **Google Fast Pair** (used by Pixel Buds, some Sony/Bose headphones) exposes battery level via a **proprietary Bluetooth service**.
- **Realism on Windows:** Very low (~1% of users). Fast Pair is Android-specific, and most devices also support **GATT 0x180F** or **BLE advertisements**.
- **Action:** Skip implementation for now. If users report missing battery data for Fast Pair devices, investigate whether they broadcast battery in advertisements or support GATT.

---

## Prioritization and Implementation Order

| Method | Effort | Coverage | Real-Time? | Priority | Notes |
|--------|--------|----------|------------|----------|-------|
| **Implement Phased Scanning Infrastructure** | Medium | N/A | ✅ Yes | **1 (High)** | Foundational for all new protocols. |
| **PnP Device Watcher** | Low | Medium | ✅ Yes | **2 (High)** | Improves device discovery; minimal changes. |
| **HID Battery Reporting** | Medium | Medium | ❌ No | **3 (High)** | Covers keyboards/mice; aligns with existing GATT/Classic. |
| **BLE Advertisement Scanning** | Medium | Low | ✅ Yes | **4 (Medium)** | Passive monitoring; limited to devices that broadcast battery. |
| **AVRCP Battery Reporting** | High | Low | ❌ No | **5 (Medium)** | Audio devices only; vendor-specific. |
| **HFP Battery Reporting** | Medium | Low | ❌ No | **6 (Low)** | Legacy headsets only; optional. |
| **Vendor-Specific APIs** | High | Low | ❌ No | **7 (Low)** | Hardware-dependent; optional. |

**Recommended Implementation Order:**
1. **Implement Phased Scanning Infrastructure** (High priority, foundational for all new protocols).
2. **PnP Device Watcher** (Quick win for device discovery).
3. **HID Battery Reporting** (Covers common peripherals).
4. **BLE Advertisement Scanning** (Passive monitoring for wearables/beacons).
5. **AVRCP Battery Reporting** (Audio devices).
6. **HFP Battery Reporting** (Optional, for legacy headsets).
7. **Vendor-Specific APIs** (Optional, for advanced users).

---

## Data Model Changes

### `DeviceBatteryInfo`
To support the new methods, extend `DeviceBatteryInfo` with the following **optional fields**:

```csharp
public sealed record DeviceBatteryInfo(
    string DeviceId,
    string Name,
    int? Battery,
    bool? IsCharging = null,
    BatterySource? Source = null);  // Indicates the source of the battery data
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
  "EnableAvrcpBatteryMonitoring": true,
  "EnableBleAdvertisementMonitoring": true,
  "EnableHfpBatteryMonitoring": false,
  "EnableVendorBatteryMonitoring": false,
  "BleAdvertisementScanIntervalSeconds": 30,
  "PhasedScanning": {
    "MaxConcurrentBluetoothOperations": 2,
    "ScanTimeoutSeconds": 2
  }
}
```

---

## Acceptance Criteria (Overall)
1. **Backward Compatibility:**
   - All existing functionality (GATT, Classic) continues to work unchanged.
   - Existing `DeviceBatteryInfo` construction sites compile without changes.

2. **Performance:**
   - **Phase 1 (device list) appears instantly** (<50ms).
   - **Phase 2 (battery data) appears within 500ms**.
   - **Phase 3 completes within 2s** (background).
   - Polling intervals remain **configurable** and **non-blocking**.
   - BLE advertisement scanning does not **drain battery** or **overload the Bluetooth radio**.

3. **No Stale Data:**
   - Battery levels are **always fresh** (no caching).
   - Users see **real-time data** as intended.

4. **UI Consistency:**
   - Battery data from all sources is displayed **uniformly** in the scan window and tray tooltip.
   - Optional **source indicators** (e.g., "✧" for advertisements, "🎧" for AVRCP, "📞" for HFP) are added but do not clutter the UI.
   - **Loading indicators** are shown during scans.

5. **Error Handling:**
   - Failures in new methods (e.g., HID read error, AVRCP unsupported, HFP unsupported) are **logged** but do not crash the application.
   - Stale or missing data is treated as `null` (unknown) rather than an error.
   - **Timeouts** prevent UI freezes.

6. **Testing:**
   - Unit tests are added for new readers (e.g., `HidBatteryReaderTests`, `AvrcpBatteryReaderTests`, `HfpBatteryReaderTests`).
   - Integration tests verify that battery data from all sources is **merged correctly** in `DeviceAggregationPipeline`.
   - Performance tests verify **scan times** and **Bluetooth radio usage**.

---

## Files Changed Summary

### New Files
| File | Purpose |
|------|---------|
| `src/Monitoring/Hid/HidBatteryReader.cs` | HID battery monitoring. |
| `src/Monitoring/Avrcp/AvrcpBatteryReader.cs` | AVRCP battery monitoring. |
| `src/Monitoring/Hfp/HfpBatteryReader.cs` | HFP battery monitoring (optional). |
| `src/Monitoring/BleAdvertisement/BleAdvertisementBatteryReader.cs` | BLE advertisement scanning. |
| `src/Monitoring/DeviceWatcherService.cs` | PnP device watcher for real-time discovery. |
| `src/Monitoring/Vendor/VendorBatteryReader.cs` | Vendor-specific battery monitoring (optional). |
| `src/Monitoring/Vendor/IVendorBatteryHandler.cs` | Interface for vendor handlers. |
| `src/Monitoring/Vendor/IntelBatteryHandler.cs` | Intel-specific handler. |
| `src/Monitoring/Vendor/LogitechBatteryHandler.cs` | Logitech-specific handler. |
| `src/Monitoring/Vendor/SonyBatteryHandler.cs` | Sony-specific handler. |
| `src/Monitoring/BatterySource.cs` | Enum for battery data sources. |
| `src/Monitoring/PhasedScanOrchestrator.cs` | Orchestrates phased scanning. |
| `src/Monitoring/ScanPhase.cs` | Enum for scan phases. |

### Modified Files
| File | Change |
|------|--------|
| `src/Monitoring/DeviceBatteryInfo.cs` | Add `BatterySource? Source = null` parameter. |
| `src/Monitoring/DeviceAggregationPipeline.cs` | Add new readers to the pipeline; prioritize connected data. |
| `src/Monitoring/PollingOrchestrator.cs` | Add timer for BLE advertisement scanning. |
| `src/Monitoring/BluetoothBatteryMonitor.cs` | Integrate `PhasedScanOrchestrator` and `DeviceWatcherService`. |
| `src/Monitoring/DeviceWatcherService.cs` | Add `GetCurrentDevicesAsync` for Phase 1. |
| `src/Monitoring/BleAdvertisement/BleAdvertisementBatteryReader.cs` | Skip on battery power; throttle to 30s. |
| `src/Tray/ScanCoordinator.cs` | Handle incremental UI updates; trigger manual scan on `DeviceWatcher` events. |
| `src/Tray/ScanWindow.cs` | Add source indicators (e.g., "✧" for advertisements, "🎧" for AVRCP, "📞" for HFP); add loading spinner and "Deep Scan" button. |
| `src/Settings/ThresholdSettings.cs` | Add new settings for phased scanning and enabling/disabling methods. |

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
1. **Implement Phased Scanning Infrastructure** (High priority, foundational for all new protocols).
2. **Implement PnP Device Watcher** (Quick win for device discovery).
3. **Implement HID Battery Reporting** (Covers common peripherals).
4. **Implement BLE Advertisement Scanning** (Passive monitoring for wearables/beacons).
5. **Implement AVRCP Battery Reporting** (Audio devices).
6. **Update `DeviceBatteryInfo`** to include `BatterySource`.
7. **Add Settings** for enabling/disabling new methods and phased scanning.
8. **Test and Validate** with a variety of Bluetooth devices (HID, audio, wearables).
9. **Implement HFP Battery Reporting** (Optional, for legacy headsets).
10. **Implement Vendor-Specific APIs** (Optional, for advanced users).