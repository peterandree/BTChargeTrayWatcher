# ADR-019 — Manual "Deep Scan" UX & operational limits

**Status:** Proposed
**Date:** 2026-05-20

## Context

Some Bluetooth recognition problems can only be resolved by performing a more exhaustive device scan than the regular background poll (e.g., when a device is newly paired, has transient connectivity, or is in an unusual power state). While an aggressive background scan risks waking devices and draining batteries, a user-initiated deep scan (manual) can be acceptable if it is timeboxed, cancellable, and opt-in.

Constraints:
- Deep scans must be user-initiated only; never scheduled or automatic.
- Timeboxed and cancellable; do not keep long-lived GATT sessions open after the scan completes.

## Decision

1. Add a `DeepScan` action to `ScanCoordinator` and expose it from the `ScanWindow` UI as a button labelled "Deep scan (diagnostic)". The button must present a one-line warning: "This scan may temporarily increase Bluetooth activity; recommended for troubleshooting only." The user must explicitly confirm the action.

2. Operational rules for `DeepScan`:

   - Single run per user invocation; prevent concurrent deep scans.
   - Global time budget: default 30 seconds. The scan must honour a `CancellationToken` and abort early when cancelled.
   - Per-reader tuning: reader calls within a deep scan may use a slightly longer per-device timeout (e.g., GATT: 6 s → 8 s) but must still obey the global cancel token.
   - Never subscribe to GATT characteristic notifications during a deep scan — only read the battery characteristic once per device.

3. UI behaviour:

   - Show a modal progress panel in `ScanWindow` with a cancel button and a short summary (devices found / devices with battery data).
   - On completion, present suggested alias mappings (ADR-015) and any filtered devices that match the filtering policy (ADR-016) so the user can act on them immediately.

4. Persist no state that increases background scanning frequency; deep scans are diagnostic and do not change polling behaviour unless the user explicitly takes action (e.g., includes a filtered device in monitored set or confirms an alias mapping).

## Rationale

- Gives users a controlled instrument to resolve recognition problems without changing the long-term power profile of the app.
- Timeboxing and explicit confirmation reduce the risk of accidental battery impact.

## Implementation Notes

- Reuse `TaskTracker` semantics so the deep scan participates in cooperative shutdown (ADR-007) and honours cancellation.
- Deep scan should call `DeviceAggregationPipeline.ReadMergedAsync` but pass a flag indicating "diagnostic mode" so readers may adjust timeouts safely.

## Consequences

- Minimal, temporary increase in device radio activity during the scan.
- Improved troubleshooting UX; fewer support requests when users can run the diagnostic scan and confirm mappings.

## Related ADRs

- ADR-002 — Dual Bluetooth reader strategy
- ADR-003 — Polling-based monitoring over event-driven push
- ADR-015 — Device alias migration & heuristics
- ADR-016 — Device class / type filtering policy
