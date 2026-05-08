# ADR-004 — Hysteresis for threshold alert state transitions

**Status:** Accepted  
**Date:** 2026-05-08

## Context

Battery level readings can fluctuate by ±1–2 percentage points between polls, especially near a threshold boundary. Without hysteresis, a device sitting at exactly the low threshold (e.g., 20 %) would trigger repeated notifications on consecutive polls.

## Decision

`PollingOrchestrator` maintains a `BatteryAlertState` per device (`Normal`, `Low`, `High`). State transitions are governed by hysteresis of `PollingDefaults.Hysteresis` (2 percentage points):

- Once in `Low` state: remains `Low` until battery rises above `low + 2`.
- Once in `High` state: remains `High` until battery falls below `high - 2`.
- A notification is fired only on a state *transition*, not on every poll.
- Alert states are reset (cleared) when the user changes thresholds, so the new thresholds are evaluated fresh.

## Rationale

- Eliminates notification spam at boundary values.
- The 2-point hysteresis is narrow enough to not delay a meaningful alert, yet wide enough to absorb read noise.
- Resetting on threshold change ensures the new configuration takes effect immediately without a stale state artifact.

## Consequences

- A device that crosses back and forth across a threshold within the hysteresis band generates at most one notification per crossing direction.
- `PollingDefaults.Hysteresis` is a single constant; adjust it there if the band proves too narrow or too wide.
