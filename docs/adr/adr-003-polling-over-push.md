# ADR-003 — Polling-based monitoring over event-driven push

**Status:** Accepted  
**Date:** 2026-05-08

## Context

Windows does not provide a reliable push notification for Bluetooth battery level changes across both GATT and Classic devices. GATT characteristic notifications (indications) are available for some devices, but:

- Classic devices have no push mechanism at all.
- Not all BLE peripherals implement Battery Level notifications even when they implement the Battery Service.
- Subscribing to GATT notifications keeps radio connections open permanently, increasing power consumption.

## Decision

A `System.Threading.Timer` fires every 60 seconds (`PollingDefaults.PollingInterval`). On each tick `PollingOrchestrator` issues a full read of all devices via `DeviceAggregationPipeline` and evaluates threshold state. The timer is suspended on `PowerModes.Suspend` and resumes with a 10-second delay after `PowerModes.Resume`.

## Rationale

- A 60-second interval is more than sufficient for battery level monitoring (levels change slowly).
- A uniform polling approach works identically for both BT technologies without special-casing.
- Suspending the timer on sleep prevents spurious reads against an unavailable Bluetooth radio.
- The 10-second resume delay allows the radio and device connections to re-establish before reading.

## Consequences

- Maximum notification latency is 60 seconds after a battery crosses a threshold.
- Battery reads impose a small periodic cost on the Bluetooth radio. At 60-second intervals this is negligible.
- `PollingDefaults.PollingInterval` can be reduced if faster notifications are needed.
