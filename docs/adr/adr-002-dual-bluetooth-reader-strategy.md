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
