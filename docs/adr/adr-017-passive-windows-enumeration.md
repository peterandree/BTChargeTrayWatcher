# ADR-017 — Passive Windows.Devices.Enumeration reader

**Status:** Proposed
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

## Related ADRs

- ADR-002 — Dual Bluetooth reader strategy
- ADR-018 — Centralized discovery logging & error classification
