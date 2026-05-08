# ADR-012 — Two distinct settings-changed events

**Status:** Accepted  
**Date:** 2026-05-08

## Context

`ThresholdSettings` is mutated from the UI thread and observed by two independent background consumers:

1. `PollingOrchestrator` (via `BluetoothBatteryMonitor`) — reacts to changes in BT device thresholds, the ignored-device list, and the tray-overlay exclusion list by resetting BT alert states and triggering a fresh poll.
2. `LaptopBatteryMonitor` — reacts to changes in laptop thresholds by resetting its own alert state and triggering a fresh laptop read.

A single `Changed` event would cause both consumers to react to every mutation, regardless of relevance. When a user adjusts a BT device threshold, `LaptopBatteryMonitor` would unnecessarily reset its alert state and trigger a redundant laptop battery read.

## Decision

`ThresholdSettings` exposes two events:

- `event Action? Changed` — raised on every mutation that affects BT monitoring (thresholds, ignored devices, overlay exclusions).
- `event Action? LaptopSettingsChanged` — raised only when `LaptopLow`, `LaptopHigh`, or `ExcludeLaptopFromTrayIconOverlay` change.

`LaptopLow` and `LaptopHigh` setters raise **both** events (laptop changes are also general changes, so `TrayApp` and other `Changed` subscribers are notified). `ExcludeLaptopFromTrayIconOverlay` raises only `Changed` (it affects the tray icon overlay, not the laptop monitor’s poll cycle).

`LaptopBatteryMonitor` subscribes only to `LaptopSettingsChanged`. `PollingOrchestrator` (via its `SignalThresholdsChanged` hook) subscribes only to `Changed`.

## Rationale

- Prevents unnecessary alert-state resets and redundant reads in the BT pipeline when only laptop settings change, and vice versa.
- Each consumer subscribes to exactly the events it cares about, making the dependency explicit in code.
- Two events are easier to audit than a single event carrying a discriminated payload (which would require consumers to inspect a change-type enum and add branching logic).

## Consequences

- When adding a new settings property, the author must decide which event(s) to raise. The rule is: raise `LaptopSettingsChanged` if the property affects only the laptop monitoring pipeline; raise `Changed` for everything else; raise both if the property affects both pipelines.
- **Do not consolidate these two events into one.** The split is intentional and documented here.
