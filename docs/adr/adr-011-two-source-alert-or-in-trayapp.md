# ADR-011 — Two-source alert OR in TrayApp

**Status:** Accepted  
**Date:** 2026-05-08

## Context

The tray icon can show an alert overlay for two independent reasons:

1. One or more Bluetooth devices has a battery level outside its configured thresholds.
2. The laptop battery is outside its configured laptop thresholds.

Both conditions are monitored by separate classes (`BluetoothBatteryMonitor` / `PollingOrchestrator` and `LaptopBatteryMonitor`) that have no knowledge of each other. The tray icon must reflect the OR of both alert states.

## Decision

`TrayApp` maintains two independent `bool` fields:

```csharp
private bool _hasBluetoothAlert;
private bool _hasLaptopAlert;
```

Each is updated by its own event handler (`OnBluetoothAlertStateChanged` and `UpdateLaptopAlertState`). The tray icon is refreshed by `RefreshTrayIcon`, which combines them:

```csharp
private void RefreshTrayIcon() =>
    UpdateTrayIcon(_hasBluetoothAlert || _hasLaptopAlert);
```

## Rationale

- The two monitors are independently lifecycle-managed (each has its own `DisposeAsync`). Neither should be aware of the other’s state.
- Centralising the OR in `TrayApp` — the only class with visibility into both monitors — keeps the coupling boundary clear.
- Two separate fields make it easy to extend to a third alert source (e.g., a USB-C power adapter monitor) without modifying either existing monitor.
- The alternative — one combined `HasAlert` property on a shared state object — would couple the two pipelines and require that shared object to understand the semantics of both.

## Consequences

- Both fields are updated on the UI thread (via `_uiContext.Post` or directly in `OnSettingsChanged`). No synchronisation is needed.
- If `ExcludeLaptopFromTrayIconOverlay` is set, `_hasLaptopAlert` is forced to `false` in `UpdateLaptopAlertState`; `_hasBluetoothAlert` is unaffected.
- Adding a third alert source requires: one new `bool` field in `TrayApp`, one new event subscription, and one additional term in `RefreshTrayIcon`.
