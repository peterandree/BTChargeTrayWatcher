# ADR-003 — Polling-based monitoring over event-driven push

**Status:** Accepted (amended 2026-05-08; amended 2026-09-15 for bounded GATT notifications)  
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

## Cache invalidation requirement

Because the GATT connection cache (`GattConnectionCache`) retains `BluetoothLEDevice` instances across polls, the cache must be treated as **stale after a device reconnects**. When `GattBatteryProcessor` finds a cached device whose `ConnectionStatus` is `Disconnected`, it must evict that entry and attempt a fresh `BluetoothLEDevice.FromIdAsync` call in the same poll cycle before returning a null battery.

## Resolution

`GattBatteryProcessor.ProcessDeviceAsync` implements the evict-and-retry pattern:

1. If `device.ConnectionStatus != Connected`, call `_cache.RemoveDevice(deviceId)` — this evicts both the stale `BluetoothLEDevice` and any associated `CachedGattEndpoint`, and disposes the WinRT object.
2. Call `GetOrCreateDeviceAsync` again, which issues a fresh `BluetoothLEDevice.FromIdAsync`.
3. If the new instance is still null or still not connected, return a null battery result for this poll cycle — the device is genuinely unreachable.

`GattConnectionCache.RemoveDevice` is a public method; `GattBatteryProcessor` does not access `_devices` directly. See issue **#42** — _GattBatteryProcessor: re-create stale BluetoothLEDevice on reconnect instead of returning null_.

---

## Amendment (2026-09-15) — bounded hybrid push/poll for Battery Level notifications

**Trigger:** issue **#158** (phase 2 of #153), sign-off recorded in the issue; design proposal in
[`docs/plans/gatt-notification-subscriptions.md`](../plans/gatt-notification-subscriptions.md).

### What changes

The blanket rule "polling only, never push" is narrowed to a bounded exception:

- A BLE device may hold **one** GATT Battery Level (0x2A19) notification subscription when the
  characteristic advertises `CharacteristicProperties.Notify` **and** the device is currently
  `Connected`. Subscriptions are never created by forcing a connection, so a sleeping peripheral is
  still left asleep.
- The 60 s poll is **unchanged as the watchdog** for subscribed devices, but reads the value with
  `BluetoothCacheMode.Cached`; a pushed value is used when the OS cache has nothing. If neither is
  available, exactly one uncached read is performed so the capability cache and alert state machine
  see the same result as before this amendment.
- At most `GattSubscriptionDefaults.MaxConcurrentSubscriptions` (default **2**, mirroring
  `PollingDefaults.GattMaxConcurrentReads`) devices hold a subscription at once. Setting it to `0`
  restores the pre-amendment behaviour exactly; that is the documented baseline configuration for
  the hardware measurement below.
- A subscription that produces no notification within
  `GattSubscriptionDefaults.SettlingWindow` (default **10 min**) is dropped and that device is never
  re-subscribed in the same session.
- Every subscription is released on disconnect, on `PowerModes.Suspend`, on device eviction, and on
  manager disposal. The teardown paths are listed in ADR-017's amendment.

### Unchanged

- The 60 s poll cadence, `PollingDefaults`, hysteresis, and the alert state machine are untouched.
  No new alert path is introduced: a notification only feeds the value the next poll already reads.
- Charging state (`0x2BEA` / `0x2A1B`) is still read uncached on every poll, including for subscribed
  devices. Reason: the #146 "charging suppresses the High alert" contract must not depend on the
  contents of an OS cache the app does not control, and the peripheral is already connected by the
  subscription, so the marginal cost is small. If the measurement below shows these reads dominate
  the drain, they are the first candidate for a follow-up iteration.

### Exit criterion (fixed before measuring)

No more than **2 %/hour additional peripheral drain** over a 30-minute idle baseline, measured by
@peterandree on a peripheral confirmed to expose `Notify` on 0x2A19 (`powercfg /batteryreport`
deltas, or the peripheral's own reported percentage if no power meter is available). This threshold
is not renegotiated after the data comes in. If it is exceeded, subscription support is reverted to
`MaxConcurrentSubscriptions = 0` and the result is recorded here as the amendment outcome:

> **Measurement result:** _pending — to be recorded by @peterandree._

### Consequences

- WinRT objects live longer than one read for up to 2 devices. This is the deliberate, bounded
  deviation from the "hold nothing between reads" rule; see ADR-017's amendment for the teardown
  obligations and #78 for the regression that rule came from.
- Implementation: `GattSubscriptionCoordinator`, `GattSubscriptionPolicy`,
  `GattSubscriptionDefaults`, `WinRtGattNotificationSubscription` (all under `Monitoring/Gatt/`),
  wired in `GattConnectionManager` and `BluetoothBatteryMonitor`.
- Observability: every subscribe/notify/drop decision is logged under ADR-018 codes 1010–1012, which
  is the input for the measurement above.
