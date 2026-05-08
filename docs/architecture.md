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
            │   DeviceAggregationPipeline
            │   (parallel read + dedup merge)
            │               │
            │               ▼
            │   PollingOrchestrator
            │   (threshold evaluation, hysteresis, miss-count)
            └───────────────┘  fires NotifyLow / NotifyHigh

┌────────────────────────────────────────────────────────────┐
│  LaptopBatteryMonitor  (separate from BT pipeline)          │
│  – polls System.Windows.Forms.PowerStatus + WMI             │
│  – fires BatteryUpdated → TrayApp updates menu + icon       │
└────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────┐
│  ThresholdSettings  (shared read/write state)               │
│  – JSON file in %LOCALAPPDATA%\BTChargeTrayWatcher\         │
│  – per-device threshold overrides                           │
│  – ignored-device and tray-overlay-excluded-device lists    │
│  – raises Changed event on any mutation                     │
└────────────────────────────────────────────────────────────┘
```

---

## Component responsibilities

### Program.cs

Entry point. Enforces single-instance via a named `Mutex`. Constructs all objects in dependency order (`ThresholdSettings` → `NotificationService` → readers → `BluetoothBatteryMonitor` → `LaptopBatteryMonitor` → `TrayApp`) and calls `Application.Run()`. Disposes monitors on exit.

### TrayApp

Owns the `NotifyIcon` and the WinForms message loop context. Receives events from `BluetoothBatteryMonitor`, `LaptopBatteryMonitor`, and `ScanCoordinator`, then marshals all UI mutations through the captured `SynchronizationContext`. Manages tray icon alert state by ORing the Bluetooth alert flag and the laptop alert flag.

### ScanCoordinator

Bridges the background monitor and the UI. Owns the `ScanWindow` lifetime. Routes `ManualScanCompleted` and `BackgroundRefreshCompleted` events to the UI thread and re-evaluates the alert state on each result set by comparing device batteries against per-device thresholds.

### BluetoothBatteryMonitor

Public facade. Owns the 60-second polling `Timer`, reacts to `PowerModeChanged` (suspends/resumes the timer), and delegates actual reading to `Scanner` and `PollingOrchestrator`. Manages cooperative shutdown via `CancellationTokenSource` and `TaskTracker`.

### Scanner

Executes full device scans (used at startup and on user request). Calls `DeviceAggregationPipeline.ReadMergedAsync`, then writes results into the shared `_lastKnown` dictionary under the `PollingOrchestrator`'s lock so that background polls and manual scans cannot interleave.

### DeviceAggregationPipeline

Issues `GattBatteryReader.ReadAllAsync` and `ClassicBatteryReader.ReadAllAsync` concurrently via `Task.WhenAll`, then merges results with deduplication by `DeviceId` (GATT wins on collision). Faults in either reader are logged and treated as empty results so the other source is still used.

### PollingOrchestrator

Holds the `BatteryAlertState` finite state machine per device (`Normal`, `Low`, `High`). On each poll it re-reads devices via `DeviceAggregationPipeline` (quiet read — no UI events), updates `_lastKnown`, evaluates threshold transitions with hysteresis, and calls `NotificationService.NotifyLow` / `NotifyHigh` on state changes. Tracks consecutive miss-count per device and evicts absent devices after `PollingDefaults.MissCountThreshold` (3) misses.

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
              └─► DeviceAggregationPipeline.ReadMergedAsync (quiet)
                    ├─► GattBatteryReader.ReadAllAsync
                    └─► ClassicBatteryReader.ReadAllAsync
              └─► for each device:
                    update _lastKnown
                    classify BatteryAlertState (with hysteresis)
                    if state changed → NotificationService.Notify*
              └─► fire BackgroundRefreshCompleted
                    └─► ScanCoordinator → TrayApp: update tooltip + icon
```

## Data flow: manual scan

```
User clicks tray → ScanCoordinator.OpenScanWindowAndTriggerScan
  └─► ScanWindow shown
  └─► BluetoothBatteryMonitor.StartTrackedScanAsync
        └─► Scanner.ScanNowAsync
              └─► DeviceAggregationPipeline.ReadMergedAsync (raises DeviceFound events)
                    ├─► GattBatteryReader.ReadAllAsync
                    └─► ClassicBatteryReader.ReadAllAsync
              └─► update _lastKnown under PollingOrchestrator.PollLock
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
