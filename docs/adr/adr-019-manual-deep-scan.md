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

### Status of this ADR (2026-09-15)

**Read-side mode distinction: implemented.** `BatteryReadMode.DeepScan` (`src/Monitoring/BatteryReadMode.cs`)
replaced the negative `skipConnectionCheck` boolean, and `ScannerOptions` now carries one read delegate
per mode (`BluetoothBatteryMonitor` wires `ReadDevices` → `Background`, `DeepReadDevices` → `DeepScan`).
The mode is chosen by the caller of the scan, because only the caller knows whether the scan was
user-initiated: `ScanCoordinator.RunManualScanAsync` (user clicked *Scan*) uses
`BluetoothBatteryMonitor.StartTrackedDeepScanAsync()`, while the automatic
`ScanCoordinator.RunStartupScanAsync` uses the passive `StartTrackedScanAsync()` — a startup scan must
never probe actively without confirmation, which is the §1 rule below.
In `DeepScan` mode:

- the Classic reader actively verifies each candidate (`skipConnectionCheck: false`, the #147 intent);
- GATT reads use `BluetoothCacheMode.Uncached`, even for a device that holds a subscription; and
- **no subscription is created** — §2's *"never subscribe to GATT characteristic notifications during a
deep scan"* is enforced by `GattSubscriptionPolicy.ShouldAttemptSubscribe`, which only returns `true`
for a background read. Existing subscriptions are left untouched: a diagnostic read must not mutate
background state.

Before #164 the `skipConnectionCheck: false` branch was unreachable in production, so §2 was enforced
only by accident (the scan ran the same passive path as the poll, which also meant the active
verification promised above never happened and a scan *could* create subscriptions as a side effect).

**§1–§3 implemented (issue #165).** The diagnostic path now has its own confirmed action and its own
operational limits:

- `ScanWindow` shows the **`Deep scan (diagnostic)`** button (`ScanViewModel.DeepScanButtonText`, §1's
  exact label). It presents §1's warning verbatim (`ScanViewModel.DeepScanWarningText`) and starts
  nothing unless the user answers *Yes* to a confirmation whose default answer is *No*.
- `DeepScanRunner` (`src/Tray/DeepScanRunner.cs`) owns §2's operational rules: **one run per invocation**
  (a second request is refused, not queued), the **global budget**
  (`PollingDefaults.DeepScanTimeBudget`, 30 s) passed to the readers as a `CancellationToken`, and an
  explicit **user cancel** that is reported distinctly from the budget expiring
  (`DeepScanPolicy.Classify`).
- On completion the window shows §3's summary — devices found / devices with battery data — and the
  Cancel control is enabled only while a run is in progress. The summary outranks the 1 s auto-refresh
  tick, which would otherwise overwrite it a second later, and closing the window stops a run, because
  the window is the only control surface the run has.
- **The window's plain scan (and its auto-refresh) is now passive.** Opening the window is not a
  confirmation, and an auto-refreshing window would otherwise run an active scan every 30 s — which is
  exactly the "automatic" case the Context section forbids. The active path is reachable only through
  the confirmed button, so `BluetoothBatteryMonitor.StartTrackedDeepScanAsync` requires a caller-supplied
  token (no overload defaulting to the shutdown token) and has one production caller.
- Unchanged from the earlier read-side work: no GATT subscription is created during a deep scan, existing
  subscriptions are left untouched, and the automatic startup scan stays passive.

**Deliberately not done — §2's per-reader tuning.** The ADR says reader calls *may* use a slightly longer
per-device timeout (e.g. GATT 6 s → 8 s). That is not implemented: the 30 s budget is the binding limit,
and raising per-read timeouts makes it *more* likely that a run is cut off before it reaches every device,
which defeats the purpose of a diagnostic scan. Revisit only with a measurement showing per-device reads
are being truncated by the current timeouts.

**Deliberately not done — a separate modal progress panel.** §3 asks for a "modal progress panel". The run
is surfaced inline instead (status line with the running state and the cancel button; the action itself is
disabled while running), because the scan window already presents the results the summary reports and a
nested modal message loop adds nothing the inline surface does not already provide.

**Still unimplemented — §3's last bullet.** Presenting *suggested alias mappings* (ADR-015) and *devices
filtered by the category policy* (ADR-016) when a run finishes. The aggregation pipeline drops filtered
devices inside `BatteryReaderOrchestrator`, so the scan caller cannot see them; surfacing them needs new
plumbing and is tracked separately. Confirmed alias suggestions keep flowing through
`AliasSuggestionService` as before.

## Consequences

- Minimal, temporary increase in device radio activity during the scan.
- Improved troubleshooting UX; fewer support requests when users can run the diagnostic scan and confirm mappings.

## Related ADRs

- ADR-002 — Dual Bluetooth reader strategy
- ADR-003 — Polling-based monitoring over event-driven push
- ADR-015 — Device alias migration & heuristics
- ADR-016 — Device class / type filtering policy
