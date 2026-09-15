# Classic Bluetooth battery coverage — what the public Windows surface can and cannot do

**Scope:** issue #153, phase 3. Research/decision input, no behaviour change.
**Date:** 2026-09
**Method:** read the shipped readers, the WinRT/SetupAPI surface they use, and cross-check against what
Windows itself displays in *Settings → Bluetooth & devices*. Claims are marked **[code]** when they are
verifiable in this repository and **[platform]** when they are a statement about the OS/BT spec that a
developer should re-confirm on real hardware before relying on it.

---

## 1. What the app reads today

| Path | Mechanism | Files |
| :-- | :-- | :-- |
| BLE GATT | Battery Level `0x2A19` from Battery Service `0x180F`, falling back to Common Battery Service `0x182B`; charging state from `0x2BEA` / `0x2A1B`. Uncached read per poll. **[code]** | `src/Monitoring/Gatt/GattConnectionManager.cs` |
| Classic | SetupAPI device property store: enumerate `BTHENUM` device instances, keep only instance IDs containing `_HCIBYPASS_`, then read property GUID `104EA319-6EE2-4701-BD47-8DDBF425BBE5` pid 2 (level) and pid 3 (status) from the property store. **[code]** | `src/Monitoring/Classic/ClassicBluetoothDeviceEnumerator.cs`, `ClassicBatteryPropertyReader.cs` |
| Passive enumeration | `System.Devices.Aep.IsConnected` and `System.Devices.Aep.Bluetooth.Cod.Major` only — no battery property is requested or exists in the public AEP property set. **[code]** | `src/Monitoring/DeviceWatcherService.cs` |

There is **no** `EnumerationBatteryReader` in the tree. ADR-017 describes one as a future addition; it was
never implemented. If it ever is, it can improve *discovery* coverage but cannot produce battery levels,
because the AEP property set exposes no battery value. **[code]**

---

## 2. Why a Classic-only headset shows no battery

Five independent reasons, in the order they bite:

1. **Candidate filter.** `ClassicBluetoothDeviceEnumerator` keeps only instances whose ID contains
   `_HCIBYPASS_`, i.e. the Hands-Free/Headset audio endpoints the Bluetooth audio stack publishes.
   A classic device that does not publish that interface (HID, serial, older gamepad) never becomes a
   candidate at all. **[code]**
2. **The property store is provider-populated.** pid 2 / pid 3 only contain a value if the Windows
   Bluetooth stack, the audio driver, or a vendor extension INF wrote one for that device instance.
   Absence is the normal case for headsets that only implement the HFP battery report. **[platform]**
3. **Windows shows the same data.** The battery that *Settings → Bluetooth & devices* renders comes from
   the same device property store. So a device that shows no battery in Settings will show none in this
   app either — this is a useful **oracle** when triaging: if Settings shows one and the app does not,
   that is our bug; if Settings shows none, there is nothing to read through this surface. **[platform]**
4. **HFP battery reports cannot be read without owning the audio path.** Headsets do announce battery in
   the Hands-Free Profile (HF indicators / vendor AT extensions such as the Apple/Android accessory
   reports). Consuming them requires acting as the Audio Gateway / holding the audio device
   client — a background tray monitor doing that would interfere with the user's audio sessions (and
   would be visible in the volume mixer as an active stream owner). This is why the app deliberately does
   not attempt it. **[platform]**
5. **Vendor RFCOMM/SPP and vendor GATT are per-vendor.** A classic headset may expose a proprietary SPP
   service with a battery command, and a dual-mode device may expose a vendor GATT characteristic. Neither
   has a public specification; supporting them means one implementation and one test device per vendor.
   **[platform]**

Point 5 also explains the asymmetric success rate of the BLE path: many "classic" headsets are dual-mode
and expose the standard battery service on the LE side, which the GATT reader already covers (plus `0x182B`
since phase 1 of #153). Those devices report battery through the GATT path, not through the Classic one,
so they are *not* evidence that the Classic path works. **[code + platform]**

---

## 3. Verdict table

| Capability | Feasible through the public surface? | Notes |
| :-- | :-- | :-- |
| Battery for classic devices whose vendor driver populates the property store | **Yes — already implemented** | This is the only generic source. |
| Battery for classic devices whose driver does not populate it | **No** | The data does not exist in any public API. |
| Wider Classic candidate set (drop the `_HCIBYPASS_` filter) | **Partly** | More instances would be enumerated, but classic HID/serial devices rarely carry pid 2. Worth measuring (§4.1) so we can stop guessing. |
| HFP battery via AT commands | **No** | Requires owning the audio endpoint; unacceptable side effects for a tray utility. |
| Vendor SPP / RFCOMM battery protocols | **Per vendor only** | Needs vendor documentation, a device matrix, and a new ADR before any code. |
| Vendor GATT characteristic on dual-mode devices | **Per vendor only** | Same as above; the standard services are already covered. |
| Battery from `Windows.Devices.Enumeration` (AEP) | **No** | No battery property in the public AEP property set. |
| Reading it "the way Settings does" | **Already done** | Same property store. |

**Conclusion:** for Classic-only headsets there is no generic fix to implement. The honest product answer
is to make "this device reports no battery over any standard service" legible in the UI and to keep
investing in the BLE path, which is where real coverage gains still exist.

---

## 4. Follow-ups worth doing (ordered by value per effort)

### 4.1 Measure the candidate filter (cheap, no behaviour change)
Add a one-off diagnostic (behind the existing ADR-018 `DiscoveryLogger`, Debug-only) that enumerates
**all** `BTHENUM` instances and reports, per instance: whether it matched `_HCIBYPASS_`, and whether pid 2
/ pid 3 exist (without claiming they are battery values for non-candidates). Run it on a machine with a
classic mouse/keyboard/headset and a dual-mode headset, then decide from data whether widening the filter
is worth it. Without this measurement, "we miss classic HID batteries" is speculation.

### 4.2 Log which property is missing
`ClassicBatteryPropertyReader` currently collapses three distinct outcomes into "no value": the device is
not a candidate, the property does not exist, or the value is out of range (`NormalizeBatteryPercent`
returns -1). Logging the distinction (ADR-018) would end the "why is this device N/A?" support loop.

### 4.3 Make coverage legible in the UI
The scan window and tray already show `N/A`. A short suffix (for example `N/A (no battery service)`) plus
`BatterySource` would tell the user whether the app failed or the device simply does not report a level.

### 4.4 Do not write a vendor reader yet
If a specific device becomes a real user complaint, the sequence is: capture its instance ID, property
dump and (if applicable) its SPP/GATT characteristics → write an ADR with the device matrix → implement
behind a capability check. Adding vendor code without that is guaranteed maintenance cost for one device.

---

## 5. How to reproduce the measurements on a Windows machine

```powershell
# 1. Classic Bluetooth devices Windows knows about
Get-PnpDevice -Class Bluetooth | Format-Table -AutoSize Status, FriendlyName, InstanceId

# 2. Instances that publish the hands-free audio endpoint (what the app enumerates today)
Get-PnpDevice | Where-Object InstanceId -like '*_HCIBYPASS_*' | Select-Object FriendlyName, InstanceId

# 3. Compare with what Windows itself reports: Settings > Bluetooth & devices > <device> > battery
```

If step 3 shows a battery for a device that this app reports as `N/A`, that is a bug worth filing with the
`InstanceId` from step 2 — not an invitation to write a vendor parser.

---

## 6. Related

- ADR-002 — dual Bluetooth reader strategy (amended 2026-09: the merge lives only in `BatteryReaderOrchestrator`)
- ADR-017 — passive enumeration (reader not implemented; battery is out of scope for it)
- ADR-019 — manual deep scan (why the active connection check exists only for user-initiated scans)
- Issue #153 phase 1 — Common Battery Service `0x182B` (shipped)
- Issue #153 phase 2 — GATT notifications: design in `docs/plans/gatt-notification-subscriptions.md`
