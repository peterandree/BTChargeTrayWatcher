# Feature Plan: Bluetooth Device Charging Indicator

## Goal

Display a visual charging indicator (⚡ or animated icon) next to a Bluetooth device in the scan window and tray tooltip whenever the device reports that it is actively charging. Optionally suppress the "battery high" alert while charging is in progress, because a device that is intentionally being charged to 100% should not trigger a warning.

---

## Background and Constraints

### What Bluetooth exposes

The GATT Battery Service (UUID `0x180F`) exposes a single characteristic: Battery Level (`0x2A19`), a single byte 0–100. It carries no charging-state bit.

Charging state is exposed by a **separate** optional GATT service: the **Battery Status service** (UUID `0x180F` is level-only; charging state lives in Battery Status `0x180A` / characteristic `0x2BEA` — `Battery Status` characteristic part of BT spec 1.0 Battery Service 2.0, or via the vendor-specific HID `0x2A1B` `Battery Power State` characteristic). In practice, only a minority of consumer BLE peripherals implement Battery Status `0x2BEA`. Most headsets, keyboards, and mice do **not** expose charging state over GATT at all.

For **Classic Bluetooth** (HID profile), Windows exposes the battery level through `IOCTL_BATTERY_QUERY_STATUS`, which includes a `BATTERY_CHARGING` flag. The existing `ClassicBatteryReader` reads via WMI `Win32_Battery`; `BatteryStatus = 2` in WMI maps to "Charging".

### Summary of data availability

| Transport | Battery Level | Charging State | Source |
|---|---|---|---|
| BLE GATT | ✅ `0x2A19` | ⚠️ `0x2BEA` — optional, rare | GATT Battery Status service |
| Classic BT (HID/WMI) | ✅ `Win32_Battery.EstimatedChargeRemaining` | ✅ `Win32_Battery.BatteryStatus == 2` | WMI |

Because GATT charging state is unreliable across devices, the implementation must treat `IsCharging` as a **best-effort nullable bool**: `null` means unknown, not false.

---

## Design Decisions That Govern This Feature

### ADR-001 — Single non-nullable constructor per class

`DeviceBatteryInfo` is a `record` with positional parameters (ADR-001 spirit: all fields set at construction, no post-construction mutation). Adding `IsCharging` means adding it as a constructor parameter with a default, not as a nullable property set later.

**Rule:** `bool? IsCharging = null` as the fourth positional parameter. Existing callers that omit it continue to compile without change. Do not add a setter or a separate `WithCharging(...)` method — use `with` expression syntax at the read site.

### ADR-002 — Dual Bluetooth reader strategy

GATT and Classic readers are independent implementations of `IBatteryReader`. Charging state must be added to **both** independently; the GATT reader returns `null` for devices that do not implement `0x2BEA`, the Classic reader populates it from WMI.

Do not add a shared charging-detection utility class that both readers call — that would couple the two strategies and violate the isolation ADR-002 requires.

### ADR-003 — Polling over push, cache-invalidation on reconnect

Charging state is polled on the same interval as battery level. It is **not** a subscription. If a device transitions from charging to discharging between two poll cycles, the stale cached state must be treated as expired on the next successful read (the existing `GattConnectionCache.RemoveDevice` eviction path already handles GATT; WMI is stateless so no caching concern exists for Classic).

### ADR-004 — Threshold hysteresis

The `PollingOrchestrator.ClassifyBatteryState` method currently fires a High alert when `battery >= high`. This must be suppressed when `IsCharging == true` — a device at 95% that is **actively charging** should not be alerted because the user intentionally put it on charge. The hysteresis logic remains; the additional guard is:

```
if (isCharging == true) return BatteryAlertState.Normal;
```

applied **before** the threshold comparisons, only for the High path. Low alerts are unaffected (a device cannot charge and be critically low simultaneously in normal operation, but the logic does not enforce this assumption).

### ADR-008 — Options record for constructor parameter groups

`PollingOrchestratorOptions` is a positional record. If the orchestrator needs to expose charging state to the tray, a new `Action<string, bool?> OnChargingStateChanged` callback is added to the options record following the same pattern as `OnAlertStateChanged` added in the previous session.

### ADR-010 — SynchronizationContext over Control.Invoke

All UI updates from charging-state changes must be dispatched through `ScanCoordinator`'s `PostToUi` pattern, same as `AlertStateChanged`. No direct `Control.Invoke` calls from the monitoring layer.

### ADR-011 — Single source of alert truth

The `PollingOrchestrator` remains the single authority on alert state. Charging suppression of High alerts is computed **inside** `ClassifyBatteryState`, not in `ScanCoordinator` or `TrayApp`. The tray icon's alert state continues to be driven solely by `OnAlertStateChanged`.

---

## Data Model Change

### `DeviceBatteryInfo`

```csharp
public sealed record DeviceBatteryInfo(
    string DeviceId,
    string Name,
    int? Battery,
    bool? IsCharging = null);   // <-- new, optional, default null
```

All existing construction sites continue to compile unchanged. Callers that know charging state pass it explicitly.

---

## Implementation Steps

### Step 1 — Extend `DeviceBatteryInfo`

Add `bool? IsCharging = null` as the fourth parameter. No other changes needed to the record itself.

**File:** `src/Monitoring/DeviceBatteryInfo.cs`

---

### Step 2 — GATT: attempt to read `Battery Power State` (`0x2A1B`) or `Battery Status` (`0x2BEA`)

In `GattBatteryProcessor.ProcessDeviceAsync`, after reading `0x2A19`, attempt a best-effort read of the charging characteristic:

```csharp
private static readonly Guid BatteryPowerStateUuid = new("00002a1b-0000-1000-8000-00805f9b34fb");
private static readonly Guid BatteryStatusUuid    = new("00002bea-0000-1000-8000-00805f9b34fb");
```

Logic:
1. Try `0x2BEA` first (BT spec Battery Status 2.0). If present, bit 0 of byte 0 = `ChargingState` field: `0x01` = charging, `0x02` = discharging, `0x05` = not charging, `0x0F` = full.
2. Fall back to `0x2A1B` (Battery Power State, older). Bits 6–7 of byte 0: `0b11` = charging, `0b10` = discharging.
3. If neither characteristic exists or the read fails, return `IsCharging = null`.

The attempt is wrapped in its own try/catch; failure must not affect the already-read battery level. The result is passed into `GattDeviceReadResult`:

```csharp
internal sealed record GattDeviceReadResult(string DeviceId, string Name, int? Battery, bool? IsCharging = null);
```

In `GattBatteryReader.ReadAllAsync`, the `DeviceBatteryInfo` construction becomes:

```csharp
results.Add(new DeviceBatteryInfo(r.DeviceId, r.Name, r.Battery, r.IsCharging));
```

**Files:** `src/Monitoring/Gatt/GattBatteryProcessor.cs`, `src/Monitoring/Gatt/GattBatteryReader.cs`

---

### Step 3 — Classic: read charging state from WMI

In `ClassicBatteryReader`, `Win32_Battery.BatteryStatus` returns:
- `1` = Discharging
- `2` = AC power (on AC, not necessarily charging — treat as `IsCharging = true` since Classic BT devices with a cable are charging)
- `3` = Fully Charged
- `4` = Low
- `5` = Critical
- `6` = Charging
- `7` = Charging and High
- `8` = Charging and Low
- `9` = Charging and Critical
- `11` = Partially Charged

Mapping: `IsCharging = batteryStatus is 2 or 6 or 7 or 8 or 9`.

Return `null` if the WMI property is not present or throws.

**File:** `src/Monitoring/Classic/ClassicBatteryReader.cs`

---

### Step 4 — Suppress High alert when charging

In `PollingOrchestrator.ClassifyBatteryState`, add an early return before the High threshold check:

```csharp
// Charging suppression (ADR-004 extension):
// A device known to be charging must never trigger a High alert.
// Unknown (null) does not suppress — we only suppress on confirmed true.
if (battery >= high && isCharging == true)
    return BatteryAlertState.Normal;
```

`DeviceBatteryInfo.IsCharging` must be threaded through `PollAsync` to `ClassifyBatteryState`. The signature becomes:

```csharp
internal BatteryAlertState ClassifyBatteryState(
    string name, int battery, BatteryAlertState previousState, bool? isCharging)
```

**File:** `src/Monitoring/PollingOrchestrator.cs`

---

### Step 5 — Expose charging state to the UI

Charging state is display-only information. It does not need its own event — `BackgroundRefreshCompleted` and `ManualScanCompleted` already carry the full `IReadOnlyList<DeviceBatteryInfo>`, which now includes `IsCharging`. No new events or callbacks are needed.

`ScanWindow` receives the list via `OnScanComplete` and `OnDeviceFound`. Both sites must be updated to pass `IsCharging` when constructing or updating rows.

**Files:** `src/Tray/ScanWindow.cs` (update list view columns or tooltip text)

---

### Step 6 — ScanWindow UI

Add a charging indicator to the device list. Two options:

| Option | Description | Recommended |
|---|---|---|
| A — Extra column | Add a "Charging" column with ⚡ / — / ? | No — wastes space, adds noise |
| B — Inline icon in Name or Battery column | Append ` ⚡` to the battery percentage cell when `IsCharging == true`, show `?` or nothing when `null` | **Yes** |

Option B: battery cell text:
- `IsCharging == true`: `"95% ⚡"`
- `IsCharging == false`: `"95%"`
- `IsCharging == null`: `"95%"` (no indicator — unknown is not shown)

The tray tooltip (context menu text showing battery levels) follows the same pattern.

**File:** `src/Tray/ScanWindow.cs`, `src/Tray/TrayApp.cs` (tooltip construction)

---

## Acceptance Criteria

- A BLE device that implements `0x2BEA` or `0x2A1B` and reports charging displays ⚡ next to its battery percentage in the scan window and tray tooltip.
- A Classic BT device with `BatteryStatus` in {2, 6, 7, 8, 9} displays ⚡ in the same locations.
- A device without charging-state data shows the battery percentage only, with no indicator and no error.
- A device at or above the High threshold that reports `IsCharging == true` does **not** trigger a High toast notification.
- A device at or above the High threshold with `IsCharging == null` or `IsCharging == false` continues to trigger the High toast as before.
- All existing unit tests continue to pass; `DeviceBatteryInfo` construction without `IsCharging` still compiles.
- No `Control.Invoke` calls are introduced; all UI updates use the existing `SynchronizationContext` dispatch path (ADR-010).
- `ClassifyBatteryState` is the only place that suppresses the High alert; no suppression logic exists in `ScanCoordinator` or `TrayApp` (ADR-011).

---

## Files Changed Summary

| File | Change |
|---|---|
| `src/Monitoring/DeviceBatteryInfo.cs` | Add `bool? IsCharging = null` parameter |
| `src/Monitoring/Gatt/GattBatteryProcessor.cs` | Try-read `0x2BEA` / `0x2A1B`; populate `GattDeviceReadResult.IsCharging` |
| `src/Monitoring/Gatt/GattBatteryReader.cs` | Pass `IsCharging` into `DeviceBatteryInfo` construction |
| `src/Monitoring/Classic/ClassicBatteryReader.cs` | Map `BatteryStatus` WMI field to `IsCharging` |
| `src/Monitoring/PollingOrchestrator.cs` | Thread `IsCharging` through `PollAsync`; suppress High alert when `isCharging == true` |
| `src/Tray/ScanWindow.cs` | Append ⚡ to battery cell when `IsCharging == true` |
| `src/Tray/TrayApp.cs` | Append ⚡ to tray tooltip device lines when `IsCharging == true` |
