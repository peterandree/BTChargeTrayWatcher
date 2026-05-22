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
┌──────────────────────┐     ┌──────────────────┐  ┌──────────────────────┐
│  NotificationService │     │  GattBatteryReader│  │ ClassicBatteryReader │
│  (WinRT Toast)       │     │  (BLE 0x180F svc) │  │ (SetupAPI + WMI)     │
└──────────────────────┘     └──────────────────┘  └──────────────────────┘
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

### BluetoothBatteryMonitor

Public facade. Owns the 60-second polling `Timer`, reacts to `PowerModeChanged` (suspends/resumes the timer), and delegates actual reading to `Scanner` and `PollingOrchestrator`. Manages cooperative shutdown via `CancellationTokenSource` and `TaskTracker`.

The production entry point is a constructor that accepts a single infrastructure record, wired in `Program.cs`, which provides the full cooperation stack (`DeviceWatcherService`, `BatteryReaderOrchestrator`, `GattConnectionManager`, `DeviceCapabilityCache`). All legacy multi-argument constructors have been removed.

### Scanner

Executes full device scans (used at startup and on user request). On the cooperation-stack path, the injected GATT reader is `OrchestratorBatteryReaderAdapter` (which delegates to `BatteryReaderOrchestrator`) and the Classic reader is `NullBatteryReader`. Writes results into the shared `_lastKnown` dictionary so that background polls and manual scans cannot interleave. Fires `DeviceFound` events as each device is discovered.

### BatteryReaderOrchestrator

**Cooperation-stack (production) path only.** Issues `GattBatteryReader.ReadAllAsync` (per-device via `GattConnectionManager`) and `ClassicBatteryReader.ReadAllAsync` concurrently via `Task.WhenAll`, then merges results with deduplication (GATT wins on name/ID collision, Classic tagged with `BatterySource.Classic`). Updates `DeviceCapabilityCache` after each GATT attempt. Faults in either reader are logged and treated as empty results.

All ADR-015 (alias resolution), ADR-016 (device class filtering), and ADR-018 (discovery logging) implementations that affect aggregation live here.

### DeviceAggregationPipeline

**Legacy IBatteryReader path only — not reached by `Program.cs`.** Previously used by `Scanner` when constructed with explicit `IBatteryReader` instances via now-removed legacy constructors. Performs the same parallel-read-and-merge responsibility as `BatteryReaderOrchestrator` but without the cooperation-stack features (no `GattConnectionManager`, no `DeviceCapabilityCache`, no per-device GATT connection reuse).

Retained only to keep `ScannerTests` and `DeviceAggregationPipelineTests` green. All legacy constructors have now been eliminated (issue #100 follow-up complete).

### PollingOrchestrator

Holds the `BatteryAlertState` finite state machine per device (`Normal`, `Low`, `High`). On each poll it re-reads devices via the injected `ReadDevices` delegate (backed by `BatteryReaderOrchestrator`), updates `_lastKnown`, evaluates threshold transitions with hysteresis, and calls `NotificationService.NotifyLow` / `NotifyHigh` on state changes. Tracks consecutive miss-count per device and evicts absent devices after `PollingDefaults.MissCountThreshold` (3) misses. Fires `AlertStateChanged` (bool) as the authoritative tray-overlay signal.

### GattBatteryReader

Uses `Windows.Devices.Bluetooth.GenericAttributeProfile` to enumerate all devices advertising the standard Battery Service (UUID `0x180F`). Reads the Battery Level characteristic. Limits concurrency to `PollingDefaults.GattMaxConcurrentReads` (2) via a `SemaphoreSlim`. Per-device timeout is 4 seconds. Caches open GATT connections in `GattConnectionCache` and prunes stale entries.

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
              └─► BatteryReaderOrchestrator.ReadAllAsync (quiet, via OrchestratorBatteryReaderAdapter)
                    ├─► GattConnectionManager.TryReadBatteryAsync (per BLE device)
                    └─► ClassicBatteryReader.ReadAllAsync
              └─► for each device:
                    update _lastKnown
                    classify BatteryAlertState (with hysteresis)
                    if state changed → NotificationService.Notify*
              └─► fire AlertStateChanged(bool)
                    └─► ScanCoordinator → TrayApp: update tooltip + icon
```

## Data flow: manual scan

```
User clicks tray → ScanCoordinator.OpenScanWindowAndTriggerScan
  └─► ScanWindow shown
  └─► BluetoothBatteryMonitor.StartTrackedScanAsync
        └─► Scanner.ScanNowAsync
              └─► OrchestratorBatteryReaderAdapter.ReadAllAsync
                    └─► BatteryReaderOrchestrator.ReadAllAsync (raises DeviceFound events)
                          ├─► GattConnectionManager.TryReadBatteryAsync (per BLE device)
                          └─► ClassicBatteryReader.ReadAllAsync
              └─► update _lastKnown
              └─► fire ManualScanCompleted
                    └─► ScanWindow.OnScanComplete
                    └─► ScanCoordinator → TrayApp: update icon
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

> **Note:** `DeviceAggregationPipeline` is the legacy-path counterpart and is not reached by `Program.cs`. ADR-015 alias resolution applies to `BatteryReaderOrchestrator` only.

### Device class/type filtering policy (ADR-016)
`BatteryReaderOrchestrator` (production path) filters out devices that do not expose battery data or are not in a known battery-bearing category (audio, keyboard, mouse, gamepad, wearable). Users can override this in the Options UI to show or include filtered devices. The default category list is conservative and can be extended via an advanced setting.

> **Note:** ADR-016 filtering applies to `BatteryReaderOrchestrator` only, not `DeviceAggregationPipeline`.

### Passive Windows.Devices.Enumeration reader (ADR-017)
An optional `EnumerationBatteryReader` (if present) passively enumerates Bluetooth devices using `Windows.Devices.Enumeration` without opening connections or waking radios. Its results are merged at lower precedence than GATT or Classic. This increases device coverage without additional battery impact.

### Centralized discovery logging (ADR-018)
All device discovery and aggregation operations in `BatteryReaderOrchestrator` now log to a structured, centralized `DiscoveryLogger`. Logs are local-only (Debug.WriteLine or optional file sink) and use compact JSON with error codes for easier debugging and support.

> **Note:** ADR-018 logging applies to `BatteryReaderOrchestrator` only, not `DeviceAggregationPipeline`.

### Manual "Deep Scan" UX & operational limits (ADR-019)
The Scan UI now exposes a "Deep Scan" action for diagnostic purposes. Deep scans are user-initiated, timeboxed, and cancellable, and never increase background scan frequency. They allow users to resolve recognition issues, confirm alias suggestions, and include filtered devices, all without increasing long-term battery impact.
