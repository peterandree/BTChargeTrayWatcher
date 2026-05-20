# ADR-018 — Centralized discovery logging & error classification

**Status:** Proposed
**Date:** 2026-05-20

## Context

Bluetooth discovery today is implemented across multiple readers and platform APIs (GATT, SetupAPI/WMI, enumeration). Investigation of intermittent failures (timeouts, permission errors, stale cached WinRT objects) is time-consuming because logs are ad-hoc and scattered across readers. A centralised, structured logging approach will speed diagnosis and provide consistent breadcrumbs for debugging.

Constraints:
- No telemetry or remote uploading of logs without explicit opt-in and documented privacy controls.
- Logging must be local-first (Debug.WriteLine / file) and low-overhead.

## Decision

1. Implement a `DiscoveryLogger` utility inside `Monitoring/Logging` that provides a small, structured log API used by all readers and the aggregation pipeline.

2. Log entries will be structured (JSON-friendly) and include the following fields where applicable:

   - `timestamp` (ISO8601)
   - `reader` (string, e.g., "GattBatteryReader")
   - `operation` (string, e.g., "ReadBattery", "GetDevice", "EvictCache")
   - `deviceId` (string|null)
   - `deviceName` (string|null)
   - `durationMs` (int|null)
   - `outcome` ("OK" | "WARN" | "ERROR")
   - `errorCode` (numeric, see below)
   - `message` (string)

3. Define a small, documented error-code namespace to make aggregated analysis easier (examples):

   - `1000` — GATT_TIMEOUT
   - `1001` — GATT_DISCONNECTED
   - `2000` — CLASSIC_SETUPAPI_FAILURE
   - `2001` — CLASSIC_PROPERTY_MISSING
   - `3000` — ENUMERATION_ACCESS_DENIED
   - `4000` — MAPPING_AMBIGUOUS

4. Default sink: `Debug.WriteLine` with a compact JSON payload. Provide an optional developer-only file sink (rotating, written to `%LOCALAPPDATA%\BTChargeTrayWatcher\logs`) enabled by a compile-time symbol or developer mode flag (not enabled for production installs by default).

5. All logs are local-only. If telemetry is considered in future, ADR-018 must be revisited and an explicit opt-in UX plus privacy policy must be introduced.

## Rationale

- Structured logs and small error codes make it easier to filter and search relevant discovery events during debugging or when collecting repros from users.
- Centralising logging reduces duplication and ensures consistent semantics across reader implementations.

## Implementation Notes

- Keep the `DiscoveryLogger` dependency minimal. Prefer static helpers (e.g., `DiscoveryLogger.Log(reader, operation, ...)`) to avoid polluting constructor signatures; readers may call the static helper directly.
- Where possible include `durationMs` and `deviceId` to make performance regressions and repeated failures visible.

## Consequences

- Slight increase in code surface to maintain, but large win for maintainability and debugging speed.
- No change to external behaviour when logging is at the default debug sink.

## Related ADRs

- ADR-002 — Dual Bluetooth reader strategy
- ADR-017 — Passive Windows.Devices.Enumeration reader
