# ADR-017 — Passive Windows.Devices.Enumeration reader

**Status:** Proposed (amended 2026-09-15 for bounded, revocable GATT notification subscriptions)
**Date:** 2026-05-20

## Context

The existing readers cover GATT (BLE) and Classic (SetupAPI/WMI) sources (ADR-002). Windows also provides a passive enumeration API (`Windows.Devices.Enumeration`) that can expose device metadata without actively opening GATT sessions or performing SetupAPI enumerations. A passive enumeration reader can increase coverage (detect devices that do not appear in the other readers) while avoiding active connections that may wake or poll devices.

Constraints:
- Any new reader must implement `IBatteryReader` and live under `Monitoring/`.
- Must not perform active GATT subscriptions or forced `BluetoothLEDevice.FromIdAsync` connections that are known to keep radios open.

## Decision

1. Introduce `EnumerationBatteryReader` (or `PassiveEnumerationReader`) implementing `IBatteryReader` located in `Monitoring/Enumeration/`.

2. The reader will:

   - Use `DeviceInformation.FindAllAsync` (WinRT) with selective property requests to read available metadata such as `System.ItemNameDisplay`, `System.Devices.Aep.IsConnected`, and `System.Devices.Aep.DeviceAddress`.
   - Not open GATT sessions or otherwise connect to devices; it is strictly a passive read of platform-reported properties.
   - Return an empty or partial `DeviceBatteryInfo` when no battery information is available; the aggregation pipeline decides how to merge partial results.

3. The `DeviceAggregationPipeline` will treat enumeration results as *lower precedence* than GATT or Classic results. In a merge collision, GATT > Classic > Enumeration.

## Rationale

- Provides a broader device surface area without introducing additional radio activity.
- Works especially well for devices whose metadata is surfaced by the OS but are not reachable via the current reader implementations.

## Implementation Notes

- The reader should use `AsTask()` to bridge WinRT calls to `Task`-based async flows and must accept a `CancellationToken` per the project's async conventions.
- Placement: `Monitoring/Enumeration/EnumerationBatteryReader.cs`.
- Requests for suspicious or unexpected enumeration-derived results should be logged via `DiscoveryLogger` (ADR-018) for field debugging.

## Consequences

- Enumeration results may be stale or incomplete. Treat them as advisory data rather than authoritative battery values.
- Additional maintenance surface (new reader) but no increase in active device wakeups.

---

## Amendment (2026-09-15) — bounded, revocable Battery Level subscriptions

**Trigger:** issue **#158**, approved in the issue thread; see ADR-003's amendment for the policy.

### What changes

The constraint "must not perform active GATT subscriptions" is narrowed for one specific case: the
existing GATT reader may hold at most `GattSubscriptionDefaults.MaxConcurrentSubscriptions` (default
2) **Battery Level (0x2A19) notification subscriptions**, and only for devices that advertise
`Notify` and are already `Connected`. No new reader is added by this amendment, and the passive
enumeration reader described above is unaffected — it stays strictly passive.

### Teardown obligations (the reason this is allowed at all)

A subscription holds a `BluetoothLEDevice` and a `GattCharacteristic` open, which is exactly what
#78 showed can keep a peripheral's radio awake. Every one of these paths must release the
subscription, and each is covered by a unit test against the fake seam:

| Trigger | Entry point |
| :-- | :-- |
| Peripheral disconnected (`ConnectionStatus != Connected`) | `WinRtGattNotificationSubscription.OnConnectionStatusChanged` self-teardown, bookkeeping removed by `GattSubscriptionCoordinator.OnSubscriptionLost` |
| Machine suspends | `BluetoothBatteryMonitor.SystemEvents_PowerModeChanged` → `GattConnectionManager.SuspendSubscriptionsAsync` (tracked task, ADR-007) |
| Device evicted after `PollingDefaults.MissCountThreshold` misses | `PollingOrchestrator` → `PollingOrchestratorCallbacks.OnDeviceEvicted` → `GattConnectionManager.DeviceEvictedAsync`, called *before* the cache entry is removed |
| Manager disposed | `GattConnectionManager.DisposeAsync` (preferred) or `Dispose` |
| No notification within the settling window | `GattSubscriptionCoordinator.PruneAsync`, evaluated once per poll cycle from `BatteryReaderOrchestrator.ReadAllAsync` |

Release means: detach the `ValueChanged`/`ConnectionStatusChanged` handlers first, drop the
references, then attempt a best-effort CCCD write-off to `None`. A failed, cancelled or impossible
descriptor write never leaves a reference behind.

### What stays forbidden

- No forced `BluetoothLEDevice.FromIdAsync` connection for a device that is not already connected.
- No subscription for a device without the `Notify` property.
- No subscription when the cap is reached, and no re-subscription in the same session after a
  settling-window drop.
- No unbounded or background-initiated WinRT object retention: the only long-lived objects are the
  bounded subscription set described here.

### Verification

Unit tests cover cap enforcement, settling-window drop and every teardown trigger through the
`IGattNotificationSubscription` fake. The hardware check from #78 (a sleeping peripheral is not kept
awake) remains a release check and is recorded in the PR description; the power measurement and its
fixed threshold live in ADR-003's amendment.

## Related ADRs

- ADR-002 — Dual Bluetooth reader strategy
- ADR-003 — Polling over push (amended for the hybrid watchdog read)
- ADR-007 — TaskTracker cooperative shutdown
- ADR-018 — Centralized discovery logging & error classification
