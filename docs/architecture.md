# Architecture

## Purpose

`BTChargeTrayWatcher` runs as a Windows system-tray process. It periodically reads battery levels from all connected Bluetooth devices (both BLE/GATT and Bluetooth Classic) as well as the host laptop battery, compares the values against configurable thresholds, and fires Windows Toast notifications when a device crosses a low or high boundary.

---

## High-level component diagram

```
┌──────────────────────────────────────────────────────────────────┐
│  Program.cs  (entry point)                                        │
│  – single-instance mutex                                          │
│  – manual DI: constructs and wires all objects                    │
│  – runs the WinForms message pump (Application.Run)               │
└───────────┬──────────────────────────────────────┬───────────────┘
            │                                      │
            ▼                                      ▼
┌───────────────────────┐            ┌─────────────────────────────┐
│  TrayApp              │            │  BluetoothBatteryMonitor     │
│  (WinForms UI host)   │◄──events───│  (BT polling facade)         │
│  – NotifyIcon         │            │  – System.Threading.Timer    │
│  – context menu       │            │  – power-mode listener       │
│  – tooltip            │            │  – TaskTracker               │
│  – ScanCoordinator    │            │  – Scanner                   │
│  – TrayMenuBuilder    │            │  – PollingOrchestrator        │
│  – TrayIconRenderer   │            └──────┬──────────┬────────────┘
└───────────────────────┘                   │          │
            │                               │          │
            ▼                               ▼          ▼
┌──────────────────────┐     ┌──────────────────────┐  ┌──────────────────────┐
│  NotificationService │     │  GattConnectionManager│ │ ClassicBatteryReader │
│  (WinRT Toast)       │     │  (BLE 0x180F per-dev) │  │ (SetupAPI + WMI)     │
└──────────────────────┘     └──────────────────────┘  └──────────────────────┘
            ▲                        │                       │
            │               ┌────────┴───────────────────────┘
            │               ▼
            │   BatteryReaderOrchestrator
            │   (parallel read + dedup merge)
            │               │
            │               ▼
            │   PollingOrchestrator
            │   (threshold evaluation, hysteresis, miss-count)
            └───────────────┘  fires NotifyLow / NotifyHigh

┌────────────────────────────────────────────────────────────────────┐
│  LaptopBatteryMonitor  (separate from BT pipeline)                  │
│  – polls System.Windows.Forms.PowerStatus + WMI                     │
│  – fires BatteryUpdated → TrayApp updates menu + icon               │
└────────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────────┐
│  ThresholdSettings  (shared read/write state)                       │
│  – JSON file in %LOCALAPPDATA%\BTChargeTrayWatcher\                 │
│  – per-device threshold overrides                                   │
│  – ignored-device and tray-overlay-excluded-device lists            │
│  – raises Changed event on any mutation                             │
└────────────────────────────────────────────────────────────────────┘
```

---

## Component responsibilities

### Program.cs

Entry point. Enforces single-instance via a named `Mutex`. Constructs all objects in dependency order (`ThresholdSettings` → `NotificationService` → readers → `BluetoothBatteryMonitor` → `LaptopBatteryMonitor` → `TrayApp`) and calls `Application.Run()`. Disposes monitors on exit.

Uses a low-arity constructor for `BluetoothBatteryMonitor` that accepts a parameter object (infrastructure record) for the cooperation stack. Legacy 2-argument and 4-argument constructors have been removed.

### TrayApp

Owns the `NotifyIcon` and the WinForms message loop context. Receives events from `BluetoothBatteryMonitor`, `LaptopBatteryMonitor`, and `ScanCoordinator`, then marshals all UI mutations through the captured `SynchronizationContext`. Manages tray icon alert state by ORing the Bluetooth alert flag and the laptop alert flag.

### ScanCoordinator

Bridges the background monitor and the UI. Owns the `ScanWindow` lifetime. Routes `ManualScanCompleted` and `AlertStateChanged` events to the UI thread. Alert state is driven exclusively by `PollingOrchestrator`'s classified, hysteresis-consistent state (ADR-011).

It also owns the user-initiated diagnostic **deep scan** through `DeepScanRunner` (`src/Tray/`): the runner enforces ADR-019's single run per invocation, the `PollingDefaults.DeepScanTimeBudget` (30 s) and cooperative cancellation, and `DeepScanPolicy` classifies and words the outcome. The window's own scan (opening it, and its auto-refresh) is deliberately passive — active probing is reachable only through the window's confirmed `Deep scan (diagnostic)` action.

### BluetoothBatteryMonitor

Public facade. Owns the 60-second polling `Timer`, reacts to `PowerModeChanged` (suspends/resumes the timer), and delegates actual reading to `Scanner` and `PollingOrchestrator`. Manages cooperative shutdown via `CancellationTokenSource` and `TaskTracker`.

The production entry point is a constructor that accepts a single infrastructure record, wired in `Program.cs`, which provides the full cooperation stack (`DeviceWatcherService`, `BatteryReaderOrchestrator`, `GattConnectionManager`, `DeviceCapabilityCache`). All legacy multi-argument constructors have been removed.

### Scanner

Executes full device scans (used at startup and on user request). On the cooperation-stack path, both readers are delegates over `BatteryReaderOrchestrator.ReadAllAsync` (fed by `DeviceWatcherService.CurrentDevices`), one per read mode: `ScannerOptions.ReadDevices` → `BatteryReadMode.Background` for the quiet read, `ScannerOptions.DeepReadDevices` → `BatteryReadMode.DeepScan` for the confirmed diagnostic scan (ADR-019). They are separate so a scan can never silently run on the passive path (issue #164). Writes results into the shared `_lastKnown` dictionary so that background polls and manual scans cannot interleave. Fires `DeviceFound` events as each device is discovered.

### BatteryReaderOrchestrator

**Cooperation-stack (production) path only.** Reads each connected BLE device via `GattConnectionManager.TryReadBatteryAsync` and runs `ClassicBatteryReader.ReadAllAsync` concurrently via `Task.WhenAll`, then merges results with deduplication (GATT wins on name/ID collision, Classic tagged with `BatterySource.Classic`). Updates `DeviceCapabilityCache` after each GATT attempt. Faults in either reader are logged and treated as empty results.

The read mode is an explicit parameter (`BatteryReadMode.Background` / `DeepScan`, issue #164) rather
than a boolean, and it drives both readers: `Background` skips the active Classic connection check
(ADR-017) and lets a BLE device hold a bounded notification subscription; `DeepScan` verifies each
Classic candidate and reads GATT uncached without ever subscribing (ADR-019). The mode also drives the
two `ScannerOptions` delegates, so a caller cannot inherit the wrong one by accident.

All ADR-015 (alias resolution), ADR-016 (device class filtering), and ADR-018 (discovery logging) implementations that affect aggregation live here.

### Removed: DeviceAggregationPipeline

The legacy `IBatteryReader`-based merge class no longer exists (removed in #149).
`BatteryReaderOrchestrator` is now the **single** merge point for GATT + Classic results; `Scanner`
consumes its output through one delegate per read mode (`ScannerOptions.ReadDevices`,
`ScannerOptions.DeepReadDevices`) and performs no merge of its own. There is no legacy parallel path
left to describe — see the ADR-002 amendment.

### PollingOrchestrator

Holds the `BatteryAlertState` finite state machine per device (`Normal`, `Low`, `High`). On each poll it re-reads devices via the injected `ReadDevices` delegate (backed by `BatteryReaderOrchestrator`), updates `_lastKnown`, evaluates threshold transitions with hysteresis, and calls `NotificationService.NotifyLow` / `NotifyHigh` on state changes. Tracks consecutive miss-count per device and evicts absent devices after `PollingDefaults.MissCountThreshold` (3) misses. Before evicting, it calls `PollingOrchestratorCallbacks.OnDeviceEvicted` so any GATT notification subscription for that device is released first (#161). Fires `AlertStateChanged` (bool) as the authoritative tray-overlay signal.

### GattConnectionManager

Long-lived per-device reader for BLE devices enumerated by `DeviceWatcherService`. Opens `BluetoothLEDevice.FromIdAsync`, reads the Battery Level characteristic (UUID `0x2A19`, looked up through `GattBatteryCharacteristicLocator` in the standard Battery Service `0x180F` first and the Common Battery Service `0x182B` second) with `BluetoothCacheMode.Uncached`, and applies a hard 2-second timeout to every WinRT call via `WaitAsync`. Caches only *knowledge* (which device IDs expose the service), never WinRT objects — all device references are dropped after each read so peripherals can sleep (#78). Limits concurrency to `PollingDefaults.GattMaxConcurrentReads` (2) via a `SemaphoreSlim`. The read contract, concurrency gate, and cancellation are unit-tested through an injectable override; the real WinRT read path is integration-only (see `TESTING.md`).

Since the #158 amendment of ADR-003/ADR-017 the manager also owns the **bounded notification
subscription set** (`GattSubscriptionCoordinator`, policy in `GattSubscriptionPolicy`, constants in
`GattSubscriptionDefaults`, WinRT side in `WinRtGattNotificationSubscription`). A device that
advertises `Notify` on `0x2A19` and is already connected may hold one of at most two subscriptions;
for such a device the watchdog poll reads `BluetoothCacheMode.Cached` (falling back to the last pushed
value, then to one uncached read), while charging-state reads stay uncached so the #146 contract does
not depend on the OS cache. Subscriptions are released on disconnect, suspend, eviction and disposal,
and one that never notifies within the settling window is dropped for the rest of the session. Setting
`GattSubscriptionDefaults.MaxConcurrentSubscriptions` to `0` restores plain polling.

### ClassicBatteryReader

Uses SetupAPI P/Invoke (`ClassicBluetoothDeviceEnumerator`) to enumerate Classic Bluetooth device instance IDs, checks live connection status via `ClassicBluetoothConnectionChecker`, then reads the `DEVPKEY_Bluetooth_Battery` property via `ClassicBatteryPropertyReader` (WMI). Per-device connection check timeout is 3 seconds.

### LaptopBatteryMonitor

Polls `System.Windows.Forms.PowerStatus` and WMI `Win32_Battery` on a separate `Timer`. Fires `BatteryUpdated` with `LaptopBatteryInfo` (percent, charging flag, AC power flag). Has its own threshold evaluation feeding into `TrayApp` for the laptop alert overlay.

### NotificationService

Wraps `Windows.UI.Notifications.ToastNotificationManager`. Registers an AUMID in `HKCU\Software\Classes\AppUserModelId\BTChargeTrayWatcher` so toasts are attributed correctly. Fires `OnNotificationClicked` when the user activates a toast, which routes back to `ScanCoordinator.RequestOpenScanWindow`.

### ThresholdSettings

All configuration in one class. Persists to `%LOCALAPPDATA%\BTChargeTrayWatcher\settings.json` using an atomic write (write to `.tmp`, then `File.Move` with overwrite). Supports:
- Global low/high thresholds (default 20 % / 80 %)
- Separate laptop low/high thresholds
- Per-device threshold overrides
- Ignored-device list (monitoring suppressed)
- Tray-icon-overlay exclusion list (monitoring continues; icon badge suppressed)
- Startup autorun via `StartupRegistration` (HKCU Run key)

---

## Data flow: background poll

```
Timer tick (every 60 s)
  └─► PollingOrchestrator.OnTimerTick
        └─► TaskTracker.Start(SafePollAsync)
              └─► BatteryReaderOrchestrator.ReadAllAsync (BatteryReadMode.Background)
                    ├─► GattConnectionManager.TryReadBatteryAsync (per BLE device;
                    │     cached read if subscribed, may subscribe, skip classic checks)
                    └─► ClassicBatteryReader.ReadAllAsync (skipConnectionCheck: true)
              └─► for each device:
                    update _lastKnown
                    classify BatteryAlertState (with hysteresis)
                    if state changed → NotificationService.Notify*
              └─► fire AlertStateChanged(bool)
                    └─► ScanCoordinator → TrayApp: update tooltip + icon
```

## Data flow: scan window (passive)

```
User clicks tray → ScanCoordinator.OpenScanWindowAndTriggerScan
  └─► ScanWindow shown
  └─► BluetoothBatteryMonitor.StartTrackedScanAsync               └─► Scanner.ScanNowAsync(Background)
                    └─► BatteryReaderOrchestrator.ReadAllAsync (BatteryReadMode.Background,
                          raises DeviceFound events)
                          ├─► GattConnectionManager.TryReadBatteryAsync (per BLE device; may use the
                          │     Windows cache for a subscribed device)
                          └─► ClassicBatteryReader.ReadAllAsync (skipConnectionCheck: true —
                                passive, no radio query per candidate)
              └─► update _lastKnown
              └─► fire ManualScanCompleted
                    └─► ScanWindow.OnScanComplete
                    └─► ScanCoordinator → TrayApp: update icon
```

## Data flow: confirmed deep scan (ADR-019)

```
User presses "Deep scan (diagnostic)" → warning + confirmation (default: No)
  └─► ScanWindow.DeepScanRequested → ScanCoordinator.RequestDeepScan
        └─► DeepScanRunner.RunAsync (single run, 30 s budget, cancellable)
              └─► BluetoothBatteryMonitor.StartTrackedDeepScanAsync(budget token)
                    └─► Scanner.ScanNowAsync(DeepScan)
                          └─► BatteryReaderOrchestrator.ReadAllAsync (BatteryReadMode.DeepScan)
                                ├─► GattConnectionManager.TryReadBatteryAsync (uncached;
                                │     never subscribes)
                                └─► ClassicBatteryReader.ReadAllAsync (skipConnectionCheck: false —
                                      actively verifies each candidate)
  └─► DeepScanPolicy.Classify/Describe → ScanWindow summary
        "Deep scan complete: N device(s) found, M with battery data."
  └─► Cancel button (enabled only while running) → ScanCoordinator.RequestDeepScanCancel
  └─► Closing the window cancels a run in progress
```

---

## Threading model

| Thread | Responsibilities |
|---|---|
| UI thread (STA) | WinForms message pump, all `NotifyIcon` / menu mutations, `ScanWindow` |
| `System.Threading.Timer` callback (thread pool) | Fires `PollingOrchestrator.OnTimerTick` |
| Thread pool | All async BT reads, `TaskTracker`-managed tasks |

All transitions from thread pool → UI go through `SynchronizationContext.Post`. There are no `Dispatcher.Invoke` or `Control.Invoke` calls except inside `ScanCoordinator` (which uses `BeginInvoke` for `ScanWindow`).

---

## Settings file format

Location: `%LOCALAPPDATA%\BTChargeTrayWatcher\settings.json`

```json
{
  "Version": 1,
  "Low": 20,
  "High": 80,
  "LaptopLow": 20,
  "LaptopHigh": 80,
  "IgnoredDevices": [],
  "TrayIconOverlayExcludedDevices": [],
  "ExcludeLaptopFromTrayIconOverlay": false,
  "DeviceOverrides": {
    "My Headphones": { "Low": 15, "High": 90 }
  }
}
```

The file is written atomically. Corrupt or missing files reset to defaults (20 / 80) without crashing.

---

## Recent architectural enhancements (ADR-015 to ADR-019)

### Device alias migration & heuristics (ADR-015)
To improve resilience to device re-pairing and renaming, `ThresholdSettings` now includes an alias/history mapping (`AliasMap`) that links historical display-name variants to a canonical name. `BatteryReaderOrchestrator` (production path) applies a multi-stage alias resolution pipeline (exact match, alias lookup, normalized equivalence, and high-confidence fuzzy match). Only high-confidence matches are auto-applied; fuzzy matches are surfaced as suggestions in the UI for user confirmation. The Options UI exposes a surface for managing and confirming alias mappings.

> **Note:** `BatteryReaderOrchestrator` is the only aggregation path in the app; the legacy counterpart (`DeviceAggregationPipeline`) was removed in #149. ADR-015 alias resolution applies there and nowhere else.

### Device class/type filtering policy (ADR-016)
`BatteryReaderOrchestrator` (production path) filters out devices that do not expose battery data or are not in a known battery-bearing category (audio, keyboard, mouse, gamepad, wearable). Users can override this in the Options UI to show or include filtered devices. The default category list is conservative and can be extended via an advanced setting.

> **Note:** ADR-016 filtering applies inside `BatteryReaderOrchestrator` (there is no other aggregation path since #149).

### Passive Windows.Devices.Enumeration reader (ADR-017)
An optional `EnumerationBatteryReader` (if present) passively enumerates Bluetooth devices using `Windows.Devices.Enumeration` without opening connections or waking radios. Its results are merged at lower precedence than GATT or Classic. This increases device coverage without additional battery impact.

### Centralized discovery logging (ADR-018)
All device discovery and aggregation operations in `BatteryReaderOrchestrator` now log to a structured, centralized `DiscoveryLogger`. Logs are local-only (Debug.WriteLine or optional file sink) and use compact JSON with error codes for easier debugging and support.

> **Note:** ADR-018 logging applies to `BatteryReaderOrchestrator` (the only aggregation path since #149).

### Manual "Deep Scan" UX & operational limits (ADR-019)
The Scan UI exposes a **Deep scan (diagnostic)** action for diagnostic purposes. It presents ADR-019's warning and requires explicit confirmation (default *No*), runs at most once per invocation under a 30 s `PollingDefaults.DeepScanTimeBudget`, is cancellable from the window, and reports a summary (devices found / with battery data) that the auto-refresh tick cannot overwrite. Deep scans never increase background scan frequency, and no automatic path (startup, window open, auto-refresh) reaches the active read mode — the window's own scan is passive.

Not implemented: a separate modal progress panel (the run is surfaced inline) and the completion-time presentation of alias suggestions / category-filtered devices; see the ADR status note.
