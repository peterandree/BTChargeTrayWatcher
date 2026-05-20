# ADR-016 — Device class / type filtering policy

**Status:** Proposed
**Date:** 2026-05-20

## Context

Bluetooth device lists can include many devices that are irrelevant to battery monitoring (printers, embedded sensors, legacy peripherals without battery info). These entries increase UI noise and make it harder for users to find the devices they care about. The project already aggregates multiple sources (GATT + Classic) per ADR-002; we need a concrete policy for filtering at the aggregation boundary.

Constraints:
- Filtering must be performed inside the Monitoring pipeline (not in UI) to avoid leaking platform-specific calls into the UI layer.
- Users must be able to override or view filtered items; the project must not silently hide devices without giving a recovery path.

## Decision

1. Implement a default *include* policy in `DeviceAggregationPipeline` that keeps only devices that satisfy at least one of the following criteria:

   - The device exposes the GATT Battery Service (UUID 0x180F) (GATT reader).
   - The Classic reader returns a non-null `DEVPKEY_Bluetooth_Battery` property.
   - The device's class or category is one of the known, battery-bearing categories (audio/headset, wearable, keyboard, mouse, gamepad). The mapping from platform device class -> category is maintained by the `Scanner` and documented in code.

2. Devices that do not meet any of these criteria are *filtered out by default* from the device list used for thresholds and tray overlays.

3. Expose a user-visible override in `OptionsForm` (Devices tab):

   - A checkbox `Show filtered devices` that temporarily displays filtered devices in the grid.
   - A per-device action `Include` that moves a device from the filtered set into the monitored set (persists via `ThresholdSettings`).

4. Make the default category list conservative; add an advanced `ThresholdSettings` option `AllowlistDeviceCategories` (optional) so power users or automation scripts can extend the allowed categories.

## Rationale

- Reduces the cognitive load and clutter in the Scan UI and Options UI.
- Keeps the monitoring pipeline focused on devices that are likely to provide battery data without increasing polling or device wakeups.

## Implementation Notes

- The classification step belongs inside the `Scanner` or `DeviceAggregationPipeline` so that both GATT and Classic results are eligible for the same filtering logic.
- The default category list must be documented and maintained in code comments; any additions require an ADR or documented justification.
- Filtered devices should still appear in debug logs for troubleshooting (see ADR-018).

## Consequences

- Potential false-negatives: a legitimate battery-bearing device may be filtered if it does not expose battery metadata the pipeline expects. The UI override mitigates this risk.
- Small increases in complexity: new settings surface and additional UI affordances in `OptionsForm`.

## Related ADRs

- ADR-002 — Dual Bluetooth reader strategy
- ADR-009 — Device identity vs display name
- ADR-018 — Centralized discovery logging & error classification
