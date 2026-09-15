# ADR-002 — Dual Bluetooth reader strategy (GATT + Classic)

**Status:** Accepted  
**Date:** 2026-05-08

## Context

Windows exposes Bluetooth device battery levels through two independent mechanisms:

1. **BLE GATT Battery Service (UUID 0x180F):** Supported by modern BLE peripherals (headphones, mice, keyboards). Accessed via `Windows.Devices.Bluetooth.GenericAttributeProfile`.
2. **Bluetooth Classic `DEVPKEY_Bluetooth_Battery` property:** Exposed through SetupAPI for older HID-over-RFCOMM devices (many headsets, gamepads). Accessed via P/Invoke into `SetupAPI.dll` and `System.Management` (WMI).

Neither mechanism covers all devices. A headset paired as Classic may not appear in the GATT enumeration, and a BLE mouse will not appear in the Classic path.

## Decision

Both readers (`GattBatteryReader`, `ClassicBatteryReader`) run concurrently on every scan via `DeviceAggregationPipeline.ReadMergedAsync`. Results are merged and deduplicated by `DeviceId`; GATT results take precedence on collision.

Both implement the same `IBatteryReader` interface so they are interchangeable in tests and future extension.

## Rationale

- The union of both sources covers the widest set of real-world Windows Bluetooth devices.
- Running them concurrently minimises total scan latency.
- Fault isolation: a failure in one reader is logged and treated as an empty result; the other reader's results are still used.

## Consequences

- A device that appears in both readers (rare but possible) is deduplicated. The GATT reading is preferred as it is typically more accurate.
- Two separate Windows API surface areas must be maintained.
- `AllowUnsafeBlocks` is required for the SetupAPI P/Invoke in the Classic path.

---

## Update (2026-09) — the merge moved, the two readers did not

**Status:** Accepted, amended.

The decision above still holds: there are two readers because Windows exposes battery data through two
mechanisms, and their union covers the most devices.

What changed is *where* the two results are merged. `DeviceAggregationPipeline.ReadMergedAsync` — the
original merge point named above — is no longer wired into production. The merge now happens exactly
once, inside `BatteryReaderOrchestrator.ReadAllAsync` (`src/Monitoring/BatteryReaderOrchestrator.cs`),
which runs `GattConnectionManager` and `ClassicBatteryReader` concurrently and applies the same rule
(GATT wins on `DeviceId` collision). Both the background poll and the manual scan obtain their device
list from that single method, which is what guarantees the two paths see identical data.

Consequences for contributors:

- **Do not add a second merge.** `Scanner` and `PollingOrchestrator` consume the orchestrator's output
  as a single already-merged list; neither of them merges anything, and the previous two-delegate
  (`ReadGatt` + `ReadClassic`) shape was vestigial because its second input was always empty
  (see issue #157).
- `ScannerOptions` carries one delegate **per read mode**: `ReadDevices` for the passive
  background/quiet read and `DeepReadDevices` for the user-initiated scan. They are deliberately
  separate rather than one delegate with a mode argument, because sharing one hardcoded mode is
  exactly how the manual scan silently ended up on the background path (issue #164). Both delegates
  produce the same shape and are wired to the same orchestrator method with different
  `BatteryReadMode` values — neither of them merges.
- A new reader still means a new `IBatteryReader` implementation under `Monitoring/`, and it still has to
  be merged in `BatteryReaderOrchestrator` — not in `Scanner`.
- Per-device read policy (for example skipping the active Classic connection check on background polls,
  ADR-017) is expressed by the `BatteryReadMode` the orchestrator exposes, not by swapping delegates at
  the call site.
