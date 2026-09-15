# Design proposal — GATT Battery Level notifications (issue #153 phase 2)

**Status:** **approved and implemented** (2026-09-15).
**Date:** 2026-09
**Sign-off:** issue #158 (all four decision boxes ticked by @peterandree).
**Amendments merged with the implementation:** ADR-003 (bounded hybrid watchdog read) and ADR-017
(bounded, revocable subscriptions, teardown obligations).
**Implemented by:** `GattSubscriptionDefaults`, `GattSubscriptionPolicy`,
`GattSubscriptionCoordinator`, `WinRtGattNotificationSubscription`, `GattBatteryCharacteristicLocator`
(all under `src/Monitoring/Gatt/`), wired in `GattConnectionManager` and `BluetoothBatteryMonitor`.
**Still open:** the hardware measurement below (§6). Until its result is recorded in ADR-003, the
feature stays enabled by default with `MaxConcurrentSubscriptions = 2`.

## How to run the measurement

The policy is tunable without touching logic, exactly as the sign-off required:

| Configuration | Change | Effect |
| :-- | :-- | :-- |
| Baseline (pre-feature behaviour) | `GattSubscriptionDefaults.MaxConcurrentSubscriptions = 0` | The policy refuses every subscribe attempt; the read path is byte-for-byte the old one. |
| Prototype | `GattSubscriptionDefaults.MaxConcurrentSubscriptions = 2` (default) | Up to two devices hold a notification subscription. |

`DiscoveryLogger` (codes 1010–1012) records which devices were subscribed, how many notifications
each produced, and why each subscription ended — that trail is the input for the write-up, and it is
what makes the before/after comparison verifiable after the fact.

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

- [x] Approve amending ADR-003 (hybrid push/poll) and ADR-017 (bounded, revocable subscriptions).
- [x] Approve the cap (2) and the watchdog/settling windows in §4.
- [x] Accept §6 as an exit criterion, including the possibility of a "no-go" result.
- [x] Confirm who owns the hardware measurement — @peterandree.

All four were ticked in issue #158 on 2026-09-15. The exit-criterion threshold was fixed **before**
measuring (≤ 2 %/hour additional peripheral drain over a 30-minute idle baseline); it is not
renegotiated after the data arrives. The result is recorded in the amendment note in ADR-003.

## 8. Related

- Issue #153 (phase 1 shipped, phase 3 documented in `docs/bluetooth/classic-battery-coverage.md`)
- Issues #160 (policy), #161 (teardown), #162 (logging) — the junior-sized sub-issues
- ADR-003 — polling over push (amended)
- ADR-017 — passive enumeration, no active GATT subscriptions (amended)
- ADR-018 — discovery logging (codes 1010–1012)
- #78 — the original "peripherals kept awake" regression

## 9. Implementation deviations from this proposal

Two details were decided during implementation and are recorded here and in the ADR amendments:

1. **Charging state stays uncached.** §4 says subscribed devices are read with
   `BluetoothCacheMode.Cached`; that applies to the Battery Level read. The charging-state
   characteristics (`0x2BEA` / `0x2A1B`) are still read uncached on every poll, because the #146
   "charging suppresses the High alert" contract must not depend on an OS cache the app does not
   control and the peripheral is already connected. If the measurement shows these reads dominate
   the drain, they are the first follow-up candidate.
2. **Latency is unchanged by design.** The watchdog poll cadence is untouched and no new alert path
   was introduced, so the benefit measured here is reduced client-initiated radio traffic, not faster
   alerts. A notification feeds exactly the value the next poll reads.
