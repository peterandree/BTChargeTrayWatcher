# ADR-009 — Device identity by ID, configuration by Name

**Status:** Accepted  
**Date:** 2026-05-08

## Context

Bluetooth devices have two distinct identifiers:

1. **`DeviceId`** — a stable, opaque hardware GUID assigned by Windows (e.g., `BluetoothLE#BluetoothLE...`). Guaranteed unique and stable across reboots for the same physical device.
2. **`Name`** — the human-readable label the user sees (e.g., `"WH-1000XM5"`). Can change if the user renames the device in Windows or if a firmware update changes it.

User-facing configuration (threshold overrides, ignored-device list, tray-overlay exclusion list) must be expressed in terms of names the user recognises, not GUIDs they never see.

Runtime state (last-known battery values, alert states, miss counts) must be keyed by a stable identifier so that two devices with the same display name are tracked independently.

## Decision

- Runtime dictionaries (`_lastKnown`, `_alertStates`, `_missCount` in `PollingOrchestrator`; `_lastKnown` shared with `Scanner`) are keyed by **`DeviceId`**.
- `ThresholdSettings` stores threshold overrides, ignored-device sets, and tray-overlay exclusion sets keyed by **`Name`**.
- `PollingOrchestrator.ClassifyBatteryState` resolves thresholds via `_settings.GetLow(device.Name)` / `_settings.GetHigh(device.Name)`, bridging the two key spaces at evaluation time.

## Rationale

- Keying runtime state by ID prevents two devices with identical names from colliding in the dictionaries.
- Keying configuration by Name matches the user’s mental model; they do not interact with raw device IDs anywhere in the UI.
- The bridge is localised to a single method (`ClassifyBatteryState`), making the dual-key design explicit and easy to audit.

## Consequences

- **If a device is renamed**, its threshold overrides silently stop applying until the user re-configures them under the new name. This is an accepted trade-off: renames are rare, and the alternative (keying configuration by ID) would require surfacing GUIDs in the settings UI.
- `DeviceId` and `Name` comparisons are both case-insensitive (`StringComparer.OrdinalIgnoreCase`) for resilience against minor casing differences across API calls.
- Future work: a migration path (rename override key when `Name` changes) would eliminate the silent-drop behaviour, but is out of scope for the current version.
