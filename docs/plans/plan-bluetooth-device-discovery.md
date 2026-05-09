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
To maintain a **responsive UI** while adding multiple new protocols, scanning must be **phased** to prioritize fast, high-success methods first, while slower methods run in the background. This ensures the app **always shows fresh battery data** without blocking the UI.

**Rule:** All new battery monitoring methods must be integrated into a **phased scanning system** that:
1. Uses **PnP Watcher** to maintain a real-time device list (no caching of devices or battery levels).
2. Prioritizes fast/high-success protocols (e.g., GATT, Classic, HID) in **Phase 1** to populate battery data quickly.
3. Runs slower/low-success protocols (e.g., AVRCP, HFP, BLE Ads) in **Phase 2+** (background).
4. Merges results incrementally to update the UI as data becomes available.
5. Limits concurrent Bluetooth operations to avoid radio contention.

**Note:** The app does **not cache battery levels**, as its core purpose is real-time monitoring. The PnP Watcher provides the device list, and all protocols scan for **fresh battery data** on each poll cycle. Phased scanning ensures fast protocols run first to minimize perceived latency.

---

## Technical Specification: Phased Scanning for Bluetooth Battery Monitoring

### Goal
Ensure that adding multiple new Bluetooth battery monitoring protocols **does not degrade UX performance** (e.g., slow discovery, UI freezes, or Bluetooth radio contention). This is achieved through a **phased scanning approach**, where fast and high-success protocols run first to populate fresh battery data quickly, followed by slower or optional protocols in the background.

**Core Principle:** **No battery levels are cached.** The app always shows fresh data from the current scan cycle.

---

### Background and UX Requirements
Before optimizations, discovery took **3–5 seconds**. After optimizations, discovery is **fast and responsive**. Adding 6+ new protocols risks **reintroducing latency** if not implemented carefully.

**Key Insights:**
- **~80% of devices** are covered by **GATT, Classic, and HID** (fast protocols).
- **~20% of devices** require **AVRCP, HFP, or BLE Ads** (slower protocols).
- **Users expect fresh, real-time battery data** when opening the scan window.

**UX Requirements:**
1. **Fast Initial Results:** Users must see **device list immediately** and **battery data within 200–500ms** of opening the scan window.
2. **Non-Blocking UI:** The UI must **never freeze** during a scan.
3. **No Bluetooth Radio Contention:** Scans must not **interfere with active Bluetooth connections** (e.g., audio streaming, file transfers).
4. **Graceful Degradation:** If a protocol fails or is slow, the scan must **continue without it**.
5. **No Stale Data:** Battery levels must **always be fresh** (no caching).

---

### Architecture: Phased Scanning Model
Scanning is divided into **3 phases**, each with a **time budget**, **priority level**, and **user visibility**:

| Phase | Protocols | Time Budget | Priority | User Visibility | Purpose |
|-------|-----------|-------------|----------|-----------------|---------|
| **1** | PnP Watcher (device list) | ~50ms | Highest | Instant | Shows device list immediately (no battery levels). |
| **2** | GATT + Classic + HID | ~200–500ms | High | Immediate | Fresh battery data for most devices. |
| **3** | AVRCP + HFP | ~500ms–2s | Medium | Delayed | Slower, lower-success protocols (background). |
| **4** | BLE Ads + Vendor-Specific | ~1–3s | Low | Background | Optional, user-triggered deep scan. |

**Workflow:**
```
User Opens Scan Window
       ↓
Phase 1: Show Device List from PnP Watcher (Instant, no battery levels)
       ↓
Phase 2: Scan GATT/Classic/HID (Fast, ~500ms, fresh battery data)
       ↓
UI Updates with Fresh Battery Data
       ↓
Phase 3: Scan AVRCP/HFP (Background, ~2s)
       ↓
UI Updates Incrementally
       ↓
Phase 4: Scan BLE Ads/Vendor (Optional, User-Triggered)
```

**Note:** No battery levels are cached. Phase 1 shows the device list only, while Phase 2+ populates fresh battery data.

---

### Protocol Prioritization
Protocols are **prioritized by speed and success rate**:

| Protocol | Speed | Success Rate | Priority | Phase | Notes |
|----------|-------|--------------|----------|-------|-------|
| PnP Watcher | ⚡ Instant | 90%+ | 1 | 1 | Event-driven, device list only |
| GATT | ⚡ Fast | 80% | 1 | 2 | Primary method for BLE devices |
| Classic Bluetooth | ⚡ Fast | 70% | 1 | 2 | Paired devices only |
| HID | ⚡ Fast | 60% | 1 | 2 | Keyboards/mice |
| AVRCP | 🐢 Medium | 40% | 2 | 3 | Audio devices |
| HFP | 🐢 Slow | 20% | 2 | 3 | Legacy headsets |
| BLE Ads | ⚡ Fast* | 30% | 3 | 4 | Passive, high radio load |
| Vendor-Specific | 🐢 Slow | 10% | 3 | 4 | Optional |

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
    private readonly DeviceWatcherService _deviceWatcherService;
    private readonly IBatteryReader[] _phase2Readers; // GATT, Classic, HID
    private readonly IBatteryReader[] _phase3Readers; // AVRCP, HFP
    private readonly IBatteryReader[] _phase4Readers; // BLE Ads, Vendor
    private readonly SemaphoreSlim _bluetoothSemaphore = new(2); // Max 2 concurrent BT ops
    private readonly CancellationTokenSource _cts = new();

    public PhasedScanOrchestrator(
        DeviceWatcherService deviceWatcherService,
        GattBatteryReader gattReader,
        ClassicBatteryReader classicReader,
        HidBatteryReader hidReader,
        AvrcpBatteryReader avrcpReader,
        HfpBatteryReader hfpReader,
        BleAdvertisementBatteryReader bleAdReader,
        VendorBatteryReader vendorReader)
    {
        _deviceWatcherService = deviceWatcherService;
        _phase2Readers = new IBatteryReader[] { gattReader, classicReader, hidReader };
        _phase3Readers = new IBatteryReader[] { avrcpReader, hfpReader };
        _phase4Readers = new IBatteryReader[] { bleAdReader, vendorReader };
    }

    public async Task StartPhasedScanAsync(
        Action<IReadOnlyList<DeviceBatteryInfo>> onPhase1Complete,
        Action<IReadOnlyList<DeviceBatteryInfo>> onPhase2Complete,
        Action<IReadOnlyList<DeviceBatteryInfo>> onPhase3Complete)
    {
        // Phase 1: Device list only (no battery levels)
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
        // Phase 4: Slow protocols (user-triggered)
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

##### C. `DeviceWatcherService` (Enhanced)
**Purpose:** Maintains a real-time list of Bluetooth devices and provides it for Phase 1.

**Key Methods:**
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
        lock (_currentDevices)
        {
            return _currentDevices
                .Select(d => new DeviceBatteryInfo(d.Id, d.Name, null, null, null))
                .ToList();
        }
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

**Files:**
- `src/Monitoring/DeviceWatcherService.cs` (Enhanced with `GetCurrentDevicesAsync`)

---

#### 2. Integration Points

##### A. `BluetoothBatteryMonitor`
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

##### B. `ScanCoordinator`
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

##### C. `ScanWindow`
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

##### D. `BleAdvertisementBatteryReader`
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
1. **Implement PnP Device Watcher + Phased Scanning Infrastructure** (foundational).
2. **Add Phase 2 (GATT/Classic/HID)** and test performance.
3. **Add Phase 3 (AVRCP/HFP)** and monitor for issues.
4. **Add Phase 4 (BLE Ads/Vendor)** as opt-in features.

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

## Proposed Methods for Expanded Device Discovery and Battery Monitoring

The following methods are **realistic options** for expanding device coverage. Each method is evaluated for **feasibility**, **coverage**, and **alignment with existing design decisions**.

---

### Method 1: PnP Device Watcher
**Goal:** Improve **device discovery** and **connection state monitoring** using Windows' **`DeviceWatcher`** API.

#### Background
- **`DeviceWatcher`** (from `Windows.Devices.Enumeration`) monitors **PnP events** for device additions, removals, and property changes.
- Can detect **Bluetooth device connection/disconnection** in real-time.

#### Implementation
1. **Create a new `DeviceWatcherService`:**
   - Use **`DeviceInformation.CreateWatcher`** with the Bluetooth device selector.
   - Subscribe to `Added`, `Removed`, and `Updated` events.
   - Maintain a **real-time list of Bluetooth devices** and notify `ScanCoordinator` of changes.
   - Provide `GetCurrentDevicesAsync()` for Phase 1.

2. **Integrate with `BluetoothBatteryMonitor`:**
   - Start the `DeviceWatcherService` when `BluetoothBatteryMonitor` initializes.
   - Stop the watcher when the monitor is disposed.

3. **Handle Edge Cases:**
   - Avoid **duplicate scans** if multiple `Added`/`Updated` events fire in quick succession.
   - Ensure the watcher is **re-started** after suspend/resume.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/DeviceWatcherService.cs` | New file: Implement `DeviceWatcher` for Bluetooth devices. |
| `src/Monitoring/BluetoothBatteryMonitor.cs` | Start/stop `DeviceWatcherService` with the monitor. |
| `src/Tray/ScanCoordinator.cs` | Update device list on `DeviceWatcher` events. |
| `src/Tray/ScanWindow.cs` | Display real-time device list. |

#### Acceptance Criteria
- New Bluetooth devices are **automatically detected** and appear in the device list immediately.
- The watcher is **suspended/resumed** with the system power state.

---

### Method 2: HID Battery Reporting
**Goal:** Capture battery state from **HID-class Bluetooth devices** (e.g., keyboards, mice, gamepads) that do not support the GATT Battery Service.

#### Background
- Many HID devices (e.g., Logitech, Microsoft, Razer) report battery via **HID reports** or **vendor-specific GATT characteristics**.
- Windows exposes HID device battery via **`Windows.Devices.HumanInterfaceDevice`** (UWP) or **Win32 HID APIs**.

#### Implementation
1. **Create a new `HidBatteryReader`** implementing `IBatteryReader`.
2. **Integrate into `PhasedScanOrchestrator`:** Add `HidBatteryReader` to Phase 2.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Hid/HidBatteryReader.cs` | New file: Implement `IBatteryReader` for HID devices. |
| `src/Monitoring/PhasedScanOrchestrator.cs` | Add `HidBatteryReader` to Phase 2. |

#### Acceptance Criteria
- HID devices (e.g., Logitech MX Master) that do not support GATT Battery Service now have their battery levels displayed.

---

### Method 3: AVRCP Battery Reporting
**Goal:** Capture battery state from **audio devices** (e.g., headphones, speakers) that use the **AVRCP (Audio/Video Remote Control Profile)**.

#### Implementation
1. **Create a new `AvrcpBatteryReader`** implementing `IBatteryReader`.
2. **Integrate into `PhasedScanOrchestrator`:** Add `AvrcpBatteryReader` to Phase 3.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Avrcp/AvrcpBatteryReader.cs` | New file: Implement `IBatteryReader` for AVRCP devices. |
| `src/Monitoring/PhasedScanOrchestrator.cs` | Add `AvrcpBatteryReader` to Phase 3. |

---

### Method 4: HFP Battery Reporting (Optional)
**Goal:** Capture battery state from **audio devices** (e.g., headsets) that use the **HFP (Hands-Free Profile)**.

#### Background
- **Realism on Windows:** Low (~10–20% of older headsets). Most modern headsets use GATT or AVRCP.

#### Implementation
1. **Create a new `HfpBatteryReader`** implementing `IBatteryReader`.
2. **Integrate into `PhasedScanOrchestrator`:** Add `HfpBatteryReader` to Phase 3.

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Hfp/HfpBatteryReader.cs` | New file: Implement `IBatteryReader` for HFP devices. |
| `src/Monitoring/PhasedScanOrchestrator.cs` | Add `HfpBatteryReader` to Phase 3. |

---

### Method 5: BLE Advertisement Scanning
**Goal:** Capture battery state from **BLE devices that broadcast battery level in advertisements** (e.g., beacons, wearables).

#### Implementation
1. **Create a new `BleAdvertisementBatteryReader`** implementing `IBatteryReader`.
2. **Integrate into `PhasedScanOrchestrator`:** Add to Phase 4 (user-triggered).
3. **Throttle scanning** to 30-second intervals (skip on battery power).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/BleAdvertisement/BleAdvertisementBatteryReader.cs` | New file. |
| `src/Monitoring/PhasedScanOrchestrator.cs` | Add to Phase 4. |

---

### Method 6: Vendor-Specific APIs (Optional)
**Goal:** Support **proprietary battery monitoring** for devices from specific manufacturers.

#### Implementation
1. **Create a `VendorBatteryReader`** implementing `IBatteryReader`.
2. **Integrate into `PhasedScanOrchestrator`:** Add to Phase 4 (user-triggered).

#### Files Changed
| File | Change |
|------|--------|
| `src/Monitoring/Vendor/VendorBatteryReader.cs` | New file. |
| `src/Monitoring/Vendor/IVendorBatteryHandler.cs` | New file. |
| `src/Monitoring/PhasedScanOrchestrator.cs` | Add to Phase 4. |

---

## Future Considerations
- **Bluetooth LE Audio (LC3):** Skip for now; revisit in 2027–2028.
- **A2DP Battery Reporting:** Skip (not designed for battery monitoring).
- **Google Fast Pair:** Skip for now; most devices also support GATT.

---

## Prioritization and Implementation Order

| Method | Effort | Coverage | Priority |
|--------|--------|----------|----------|
| **PnP Device Watcher + Phased Scanning** | Medium | N/A | **1 (High)** |
| **HID Battery Reporting** | Medium | Medium | **2 (High)** |
| **AVRCP Battery Reporting** | High | Low | **3 (Medium)** |
| **HFP Battery Reporting** | Medium | Low | **4 (Low)** |
| **BLE Advertisement Scanning** | Medium | Low | **5 (Low)** |
| **Vendor-Specific APIs** | High | Low | **6 (Low)** |

---

## Data Model Changes
```csharp
public sealed record DeviceBatteryInfo(
    string DeviceId,
    string Name,
    int? Battery,
    bool? IsCharging = null,
    BatterySource? Source = null);

public enum BatterySource
{
    Unknown, Gatt, Classic, Hid, Avrcp, Hfp, BleAdvertisement, VendorSpecific
}
```

---

## Settings Changes
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

## Acceptance Criteria
1. **Phase 1 completes in <50ms** (device list only).
2. **Phase 2 completes in <500ms** (fresh battery data for most devices).
3. **Phase 3 completes in <2s** (background).
4. **No battery levels are cached**—all data is fresh.
5. **No UI freezes** during scans.
6. **No Bluetooth radio contention** (max 2–3 concurrent operations).

---

## Files Changed Summary

### New Files
- `PhasedScanOrchestrator.cs`, `ScanPhase.cs`
- `DeviceWatcherService.cs`
- Protocol readers: `HidBatteryReader.cs`, `AvrcpBatteryReader.cs`, `HfpBatteryReader.cs`, `BleAdvertisementBatteryReader.cs`, `Vendor/`
- `BatterySource.cs`

### Modified Files
- `BluetoothBatteryMonitor.cs`, `ScanCoordinator.cs`, `ScanWindow.cs`
- `BleAdvertisementBatteryReader.cs` (throttling)
- `ThresholdSettings.cs`

---

## Next Steps
1. Implement **PnP Device Watcher + Phased Scanning Infrastructure**.
2. Add **HID Battery Reporting** to Phase 2.
3. Add **AVRCP Battery Reporting** to Phase 3.
4. Test and validate.
5. Add **HFP/BLE Ads/Vendor** (optional).