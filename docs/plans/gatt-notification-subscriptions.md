# Design proposal — GATT Battery Level notifications (issue #153 phase 2)

**Status:** proposal, **not approved, not implemented**.
**Date:** 2026-09
**Requires:** amendments to **ADR-003** (polling over push) **and ADR-017** (passive enumeration / no active
GATT subscriptions) before any code is written. This document exists so that review can happen on the
design instead of on a pull request.

---

## 1. Problem

`GattConnectionManager` reads `0x2A19` with `BluetoothCacheMode.Uncached` on every poll (default 60 s,
`PollingDefaults.PollingInterval`) and then immediately drops every WinRT reference. Two consequences:

- Peripherals that support Battery Level **notifications** are woken by a client-initiated read every
  minute even though they would have told us about a change themselves.
- Changes are observed up to 60 s late.

Phase 1 of #153 (`0x182B`, shipped) widened which devices we can read; it did not change *how* we read.

## 2. What the OS offers

`GattCharacteristic.CharacteristicProperties` tells us whether `Notify` (or `Indicate`) is set.
To receive pushes a client must:

1. open the device (`BluetoothLEDevice.FromIdAsync`) and keep the object alive,
2. write the Client Characteristic Configuration Descriptor
   (`WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)`),
3. keep the subscription and a `ValueChanged` handler alive for as long as it wants pushes,
4. later write `None` to unsubscribe.

## 3. Why this is not a drop-in change

Three documented constraints collide with the proposal:

- **ADR-003** chose polling over push precisely because "subscribing to GATT notifications keeps radio
  connections open permanently, increasing power consumption".
- **ADR-017** states new readers "must not perform active GATT subscriptions or forced
  `BluetoothLEDevice.FromIdAsync` connections that are known to keep radios open".
- **#78** was a real regression where peripherals were kept awake by exactly this class of mistake. The
  rule that came out of it — hold no WinRT objects between reads — is the reason this reader has no cache.

So a hybrid design has to show that it *reduces* total peripheral wake-ups, not just moves them. That
claim cannot be verified without hardware measurements (see §6), which is why it needs sign-off first.

## 4. Proposed design (for discussion)

Subscription is opt-in per device and strictly bounded:

| Rule | Reason |
| :-- | :-- |
| Subscribe only if `0x2A19` reports `Notify` **and** the device is currently `Connected`. | Never force a connection; devices without the property keep the existing path. |
| Keep the existing periodic read as a **watchdog** (unchanged cadence) for subscribed devices, but read `BluetoothCacheMode.Cached` while a subscription is active. | A silent subscription must not freeze a stale value; a cached read costs no radio traffic. |
| Hard cap on simultaneous subscriptions (suggest 2, mirroring `PollingDefaults.GattMaxConcurrentReads`). | Bounds memory/radio cost; a 4-device machine must not create 4 permanent connections. |
| Unsubscribe immediately on `ConnectionStatusChanged != Connected`, on `PowerModes.Suspend`, on device absence (existing miss-counter), and in `DisposeAsync`. | Mirrors the evict-and-retry contract already documented for the removed connection cache. |
| A subscription that yields no notification within a settling window (suggest 10 min) is dropped and the device falls back to uncached reads permanently for that session. | Protects against peripherals that advertise `Notify` but never publish. |
| All subscription events are logged via `DiscoveryLogger` (ADR-018): subscribed, notified, dropped + reason. | The whole feature is unverifiable without this trail. |

Data flow: `ValueChanged` → update `_lastKnown[deviceId]` → feed the existing `PollingOrchestrator`
threshold state machine. No new alert path, no new alert state.

## 5. Testability

The decision logic must live behind a seam, following the existing `_testOverride` pattern in
`GattConnectionManager`:

- `IGattNotificationSubscription` (subscribe / unsubscribe / event) implemented by a WinRT-backed class and
  a fake.
- The **policy** (when to subscribe, when to drop, cap enforcement, suspend handling) is then plain logic
  and unit-testable in `GattConnectionManagerTests` with no hardware: "device without Notify is never
  subscribed", "third device over the cap is not subscribed", "disconnect drops the subscription",
  "suspend drops all subscriptions", "no notification within the window drops it".
- The untestable remainder is deliberately small: the actual CCCD write and the WinRT event delivery.

## 6. What must be measured before implementation is worth it

On a machine with ≥1 notifying BLE peripheral (most modern earbuds) and one non-notifying device:

1. Baseline: current build, radio/power counters over one hour.
2. Prototype: same hour with subscription enabled.
3. Compare peripheral-reported battery drain (device-side) and Windows radio activity.

If a subscription measurably increases peripheral drain, this proposal should be **rejected** and ADR-003
left as is — a 60 s polling latency is an acceptable price for the power behaviour we have now. That
outcome is a valid result and the reason the ADR amendment has to come first.

## 7. Decision requested

- [ ] Approve amending ADR-003 (hybrid push/poll) and ADR-017 (bounded, revocable subscriptions).
- [ ] Approve the cap (2) and the watchdog/settling windows in §4.
- [ ] Accept §6 as an exit criterion, including the possibility of a "no-go" result.
- [ ] Confirm who owns the hardware measurement.

Until all four are ticked, `GattConnectionManager` stays polling-only.

## 8. Related

- Issue #153 (phase 1 shipped, phase 3 documented in `docs/bluetooth/classic-battery-coverage.md`)
- ADR-003 — polling over push
- ADR-017 — passive enumeration, no active GATT subscriptions
- ADR-018 — discovery logging
- #78 — the original "peripherals kept awake" regression
