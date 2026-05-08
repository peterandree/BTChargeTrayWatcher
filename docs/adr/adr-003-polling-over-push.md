# ADR-003 — Polling-based monitoring over event-driven push

**Status:** Accepted (amended 2026-05-08)  
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
