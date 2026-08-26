# Implementation Plan — August 2026 Deep Review of the Bluetooth Monitoring Stack

Date: 2026-08-26
Status: Draft for review
Owner: TBD

This plan is the outcome of a full review of the application: bugs, code smells, wrong
assumptions about the Windows Bluetooth stack, missing elements, and useful additions.
Every finding was verified against the production wiring in `src/Program.cs` before an
issue was created. Nothing was invented to fill the list; a few suspected issues were
**dropped** during review because they only affect dead code or could not be substantiated
(see [Appendix B](#appendix-b-findings-considered-and-dropped)).

New issues created by this review: **#144–#154**. They build on the existing backlog
(#64–#143), which this plan references where work overlaps.

---

## 1. Issue index

### 1.1 New issues from this review (144–154)

| # | Title | Labels | Priority | Phase |
|---|---|---|---|---|
| [#144](https://github.com/peterandree/BTChargeTrayWatcher/issues/144) | Laptop battery reported as 100 % when Windows does not know the charge level (`BatteryLifePercent == 255`) | bug, high, bluetooth | P0 | 1 |
| [#145](https://github.com/peterandree/BTChargeTrayWatcher/issues/145) | Clicking a toast notification does nothing — activation never bridged from `NotificationService` to `NotificationDispatcher` | bug, medium, ui | P1 | 2 |
| [#146](https://github.com/peterandree/BTChargeTrayWatcher/issues/146) | BLE devices never report charging state in production — high-threshold alerts fire while devices are charging | bug, high, bluetooth, bluetooth-stack | P0 | 1 |
| [#147](https://github.com/peterandree/BTChargeTrayWatcher/issues/147) | Classic BT reader performs active per-device connection checks every poll (ADR-017 violation) and drops devices with unknown battery | bug, bluetooth, bluetooth-stack, adr-deviation | P0 | 1 |
| [#148](https://github.com/peterandree/BTChargeTrayWatcher/issues/148) | ADR-016 device class filtering is a no-op in production — `DeviceBatteryInfo.Category` is never populated | bug, bluetooth, adr-deviation | P0 | 1 |
| [#149](https://github.com/peterandree/BTChargeTrayWatcher/issues/149) | Orphaned reader infrastructure not wired in production; unit tests cover the dead path, not the live one | refactor, technical debt, code quality, testing | P1 | 2 |
| [#150](https://github.com/peterandree/BTChargeTrayWatcher/issues/150) | `DeviceWatcherService.RefreshAsync` races with live watcher events; `DevicesChanged` raised from multiple threads | bug, concurrency, bluetooth-stack | P1 | 2 |
| [#151](https://github.com/peterandree/BTChargeTrayWatcher/issues/151) | `AliasSuggestionService` pending queue grows unbounded when the UI never consumes it | bug, performance, code quality | P1 | 2 |
| [#152](https://github.com/peterandree/BTChargeTrayWatcher/issues/152) | ntfy: topic is an unauthenticated shared secret — support access tokens and warn when unauthenticated | enhancement, security | P2 | 3 |
| [#153](https://github.com/peterandree/BTChargeTrayWatcher/issues/153) | Broaden battery coverage: Common Battery Service 0x182B, GATT battery notifications, classic headset battery | enhancement, bluetooth, bluetooth-stack | P2 | 3 |
| [#154](https://github.com/peterandree/BTChargeTrayWatcher/issues/154) | Laptop battery depth: charge/discharge rate, estimated time remaining, wear | enhancement | P2 | 3 |

Priority rationale:

- **P0** — wrong data or wrong behaviour on every install: #144 (phantom 100 %), #146 (alert
  storm on charging devices), #147 (radio abuse + devices disappearing), #148 (a documented
  feature that does nothing).
- **P1** — real defects with bounded blast radius: #145 (dead UI affordance), #149 (dead code
  + misleading tests), #150 (racy refresh), #151 (unbounded growth).
- **P2** — improvements with design decisions that should follow the P0/P1 stabilization.

### 1.2 Pre-existing issues that this plan depends on or extends

- #64–#69 (Windows-cooperation refactor epic), #78 (skip sleeping peripherals),
  #79 (scan dialog: show connected devices without battery service),
  #81–#85 (B1–B5 correctness bugs), #88/#91 (UI-thread/perf),
  #97–#99 (ADR-015/016/018 implementation), #100–#103 (pipeline + tests),
  #114 (alias suggestions UI), #123 (alias silent fallback), #125 (shutdown ordering),
  #127 (`Win32_Battery` staleness), #128 (ntfy hot path), #131–#134 (architecture cleanup).

---

## 2. Current architecture in one screen

Production wiring (`src/Program.cs`), verified 2026-08-26:

```
ThresholdSettings ──► SettingsPersistence (atomic JSON)
NotificationService ─► WindowsToastNotificationChannel ─┐
NtfyNotificationChannel ────────────────────────────────┴─► NotificationDispatcher
DeviceWatcherService (BLE + Classic AEP watchers, passive IsConnected)
GattConnectionManager (reads 0x180F/0x2A19, knowledge cache, no object caching)
ClassicBatteryReader (SetupAPI enumeration + active connection check + property reads)
BatteryReaderOrchestrator (per-device GATT-first, classic fallback, alias + category filter)
BluetoothBatteryMonitor (timer 60 s, PollingOrchestrator, TaskTracker, scanner)
LaptopBatteryMonitor (PowerStatus only)
TrayApp / ScanCoordinator / OptionsForm
```

Not wired (dead code): `GattBatteryReader`, `GattBatteryProcessor`, `GattConnectionCache`,
`DeviceProfileClassifier`, `PhysicalDeviceIdentityResolver`, `DeviceAggregationPipeline` → #149/#148.

---

## 3. Phase 1 — Correctness fixes (P0)

> Rule for this phase: behaviour-only changes, no new features, no re-architecture.
> Every fix ships with a unit test where the logic is testable without WinRT hardware.

### 3.1 #144 — Laptop battery 100 % when unknown

- File: `src/Monitoring/LaptopBattery/WindowsLaptopBatteryReader.cs`.
- Windows reports an unknown level as a byte `255`. The framework divides by 100 and clamps
  (`BatteryLifePercent / 100f` capped at 1.0, verified in the .NET reference source), so both
  a genuinely full battery (byte 100) and an unknown level (byte 255) surface as exactly `1.0f`.
  The current guard `rawPercent >= 0` accepts `1.0f` and clamps to 100 → phantom 100 %.
- Fix: treat `rawPercent >= 1.0f` (and out-of-range values) as unknown → `BatteryPercent = -1`
  (keeps the existing `-1` sentinel contract used by `LaptopBatteryInfo` and `BatteryDisplay`).
- **Known trade-off (documented in code):** a genuinely full battery also reads `1.0f` and will
  show as "unknown" — the float cannot distinguish the two. This is the safe direction for a
  threshold-alerting app; a precise fix needs WMI `Win32_Battery` (#127/#154).
- Test: parameterised unit test over 0.0/0.1/0.25/0.5/0.99/1.0/2.55/-0.1 in
  `WindowsLaptopBatteryReaderTests` (conversion extracted as `internal static` per the
  `NtfyStatusBodyTests` precedent; the reader itself stays Tier 3).
- **Status:** implemented on `refactor/149-remove-orphaned-reader-code` (commit pending push),
  pending local verification of the full-battery case on real hardware.

### 3.2 #146 — Charging state never reported for BLE

- Files: `src/Monitoring/Gatt/GattConnectionManager.cs`, `BatteryReaderOrchestrator.cs`.
- Root cause: production reads only 0x2A19 (level). Charging state exists in the BT spec as
  Battery Status 0x2BEA / Battery Power State 0x2A1B, but (a) most real devices do not expose
  them, and (b) the only charging-state code that exists lives in the **unwired**
  `GattBatteryProcessor`.
- Step 1 (immediate): **stop alerting on charge direction we cannot know.** In
  `BatteryReaderOrchestrator`/`PollingOrchestrator`, treat `IsCharging == null` as "unknown"
  and suppress the high-threshold alert while the *previous* reading for that device was
  rising/charging, or — simpler and safe — add a per-device hysteresis rule: only fire the
  high alert when the device is **not** charging and not on AC (for the laptop path this
  signal is reliable). The exact rule needs a short design note in #146 before coding.
- Step 2 (proper): implement 0x2BEA/0x2A1B reads in the **production** GATT path with a
  capability cache so we read them only for devices that have them (reuse
  `DeviceCapabilityCache` semantics), and surface `IsCharging` in the tray/scan UI.
- Test: orchestrator-level tests with injected outcomes (`IsCharging: null/false/true`) that
  assert alert suppression rules.
- Do **not** duplicate the `GattBatteryProcessor` charging code before #149 is resolved.

### 3.3 #147 — Classic reader active per-poll checks + drops unknown batteries

- Files: `src/Monitoring/Classic/ClassicBatteryReader.cs`,
  `ClassicBluetoothConnectionChecker.cs`.
- Step 1: in the background poll path, drop the per-candidate `FromBluetoothAddressAsync`
  check; use the passive `IsConnected` already collected by `DeviceWatcherService`
  (match Classic AEP ID ↔ `ClassicBluetoothCandidate.InstanceId`). Keep the explicit,
  timeboxed check only for manual deep scans (ADR-019).
- Step 2: change the final filter from `d.Battery is >= 0 and <= 100` to keep
  `Battery == null` entries (same contract as the GATT path — feeds #79).
- Test: fake connection-checker asserting it is not called on background reads; filter test
  for null/out-of-range values.

### 3.4 #148 — ADR-016 category filtering is a no-op

- Files: `BatteryReaderOrchestrator.cs`, `DeviceBatteryInfo.cs`, `DeviceProfileClassifier.cs`,
  `BluetoothDeviceExtensions.cs`.
- Step 1 (verify first): confirm the real value/type of
  `System.Devices.Aep.Bluetooth.Cod.Major` on 2–3 known devices. MS docs type it **UInt16**;
  `BluetoothDeviceExtensions.GetClassOfDevice` pattern-matches `value is uint`. If WinRT boxes
  it as `ushort`, the extractor always returns null. Log the raw `device.Properties` entries
  for a headset and a keyboard and assert the classifier output before wiring anything.
- Step 2: stamp `Category` on every `DeviceBatteryInfo` produced by the production readers
  (GATT via `WatchedDevice.IsBle` + classifier; Classic via SetupAPI CoD if available).
- Step 3: only then does `IsAllowedByFilter`/`CategoryFilterEnabled` become meaningful.
  Extend `DeviceProfileClassifier` beyond Audio/Peripheral (Phone/Computer/Wearable/Health)
  as part of #153.
- Test: classifier unit tests for documented major-class codes; orchestrator filter tests.

---

## 4. Phase 2 — Defects, debt, and concurrency (P1)

### 4.1 #145 — Toast activation dead end

- Files: `src/Notifications/NotificationService.cs`, `NotificationDispatcher.cs`,
  `TrayApp.cs` (subscribes `subscribeNotificationClicked` already — but nothing raises it).
- Work: implement `ToastNotification.Activated` handling in `NotificationService`, surface it
  through `WindowsToastNotificationChannel` → `NotificationDispatcher.RaiseNotificationClicked`,
  and have `TrayApp`'s handler show the scan window (per the existing `issue-64-tracking.md`
  convention). Follow IR-015 (`docs/ir/ir-015-missing-remote-notification-path.md`).
- Test: dispatcher-level activation test (already exists in
  `NotificationDispatcherTests`); manual toast click test.

### 4.2 #149 — Remove or wire orphaned infrastructure

- Decision (recommended): **delete** `GattBatteryReader`, `GattBatteryProcessor`,
  `GattConnectionCache`, `CachedGattEndpoint` (production GATT is `GattConnectionManager`;
  the old path re-introduces object caching, the exact anti-pattern #65 forbids).
  **Wire or delete** `DeviceProfileClassifier` (needed by #148 — wire it).
  **Delete** `PhysicalDeviceIdentityResolver` (unused; alias map covers identity) unless a
  concrete RPA problem appears in testing first.
- Re-home valuable tests (concurrency/timeout tests from `GattBatteryReaderTests`) onto
  `GattConnectionManager` and `BatteryReaderOrchestrator`.
- Update `docs/architecture.md`, `AGENTS.md`, `README.md` (architecture block) — they still
  describe the old graph and claim "no automated tests" although `tests/` exists.
- Add a CI/tooling guard that fails when `Program.cs` wiring drifts from the documented graph.

### 4.3 #150 — DeviceWatcherService refresh race

- Recommended fix: serialise `RefreshAsync` **through** the channel so all mutations and
  `DevicesChanged` publishing happen on the single channel-processing thread; return a `Task`
  for the refresh result instead of raising the event from the caller's thread. Document the
  threading contract on `DevicesChanged`.
- Test: interleave refresh with Added/Removed events; assert final set and single-threaded
  raise.

### 4.4 #151 — AliasSuggestionService unbounded queue

- Fix: coalesce by pending membership, not just per-cycle set; clear the cycle set only when
  the queue is drained (or mirror queued IDs in a `HashSet` and remove on dequeue).
- Test: same DeviceId across cycles → one pending entry; dequeue admits a later identical one.

### 4.5 Already backlogged items to schedule alongside Phase 2

- #123/#97 (alias pipeline hardening), #125 (shutdown ordering), #127 (WMI staleness),
  #128 (ntfy hot path), #81–#85, #100–#103, #131–#134. Sequence rule: fix #149 first — several
  of these issues touch the same files (e.g. #131/#132/#133) and will be easier to land once
  the dead paths are gone.

---

## 5. Phase 3 — Enhancements (P2)

### 5.1 #152 — ntfy authentication

- Add `AccessToken` to `NtfyIntegrationSettings` (persist via `SettingsDto`, follow AGENTS.md
  settings-property steps). Send `Authorization: Bearer <token>` in
  `NtfyNotificationChannel.PublishAsync`. Warn in the options UI and setup docs that the topic
  is a shared secret. Never log the token.
- Test: fake `HttpMessageHandler` asserting the header; settings round-trip; token never in
  Debug output.

### 5.2 #153 — Broader coverage (0x182B, notifications, classic headsets)

- Phase 1: query 0x180F **and** 0x182B in `GattConnectionManager`, prefer whichever exposes
  0x2A19. Validate on real hardware first (Windows Settings battery display is a good oracle).
- Phase 2: subscribe to Battery Level `ValueChanged` for connected devices that support
  Notify; keep polling as watchdog; unsubscribe on disconnect. **Update ADR-003** (hybrid
  push/poll) — this is a documented decision change, not a silent one.
- Phase 3: write up what Classic-only headsets can/cannot do through the public surface;
  decide with a new ADR before adding any vendor-specific reader.

### 5.3 #154 — Laptop battery depth

- Extend `LaptopBatteryInfo` with WMI-derived `DischargeRateWatts`, `EstimatedRunTimeMinutes`,
  `DesignCapacityMWh`, `FullChargeCapacityMWh`, `HealthPercent`; keep `PowerStatus` as the
  percentage source (do not regress #144). Bounded WMI query on the existing cadence, guarded
  and stale-safe per #127. New formatters in `BatteryDisplay`; surface in tooltip/scan dialog.

---

## 6. Cross-cutting engineering guidance

1. **Test placement:** `tests/BTChargeTrayWatcher.Tests/` is the real test project (xUnit,
   Microsoft.Testing.Platform runner per `global.json`). AGENTS.md's "no automated tests"
   claim is stale — update it in #149.
2. **WinRT testability:** production classes that touch WinRT directly
   (`GattConnectionManager`, `DeviceWatcherService`, `ClassicBluetoothConnectionChecker`)
   need seams (delegates/records) exactly like the legacy readers had, so logic is testable
   without hardware. #150/#147 depend on this.
3. **ADR compliance:** every behavioural decision in Phase 1–3 that touches polling cadence,
   device wakeups, filtering, or alias resolution needs an ADR note (repo convention).
   Specifically: #147 (passive-only classic), #153 (hybrid push/poll), #146 (charge-aware
   alert rule).
4. **Verification on real hardware:** this repo is Windows/.NET-10-only. CI cannot exercise
   Bluetooth. Each issue's "manual" checks are mandatory before closing: use Windows
   Settings' own battery display and `btmon`/radio counters as oracles.

---

## 7. Suggested execution order (small PRs, not one big branch)

1. #144 (tiny, isolated) → 2. #146 Step 1 + #147 Step 1/2 (same orchestrator files) →
3. #148 (verify CoD first) → 4. #149 (delete dead paths, re-home tests, fix docs) →
5. #150 → 6. #151 → 7. #145 → 8. #152 → 9. #153 → 10. #154.

Each step ends with: `dotnet build`, `dotnet test`, manual smoke on a Windows machine with
≥ 2 paired devices (one Classic, one BLE), and an ADR note where required.

---

## Appendix A — Reference: production wiring map (2026-08-26)

See the table in [section 2](#2-current-architecture-in-one-screen). Verified against
`src/Program.cs`; the orchestrator (`BatteryReaderOrchestrator`) is the single merge point
for GATT + Classic and the only place alias/category logic runs.

## Appendix B — Findings considered and dropped (with reasons)

| Candidate finding | Why it was dropped |
|---|---|
| `GattBatteryProcessor` caches WinRT objects, preventing peripheral sleep | True, but the class is **unwired** (dead code). Rolled into #149. |
| `ExtractIsConnected` returns false when the AEP property is absent (Classic) | Classic devices do not flow through the orchestrator's `IsConnected` gate (they use the Classic reader), so no production impact. Doc comment in `WatchedDevice` is stale — noted in #149's doc cleanup. |
| `PhysicalDeviceIdentityResolver` leaks stale MAC keys on RPA change | True, but the type is unwired. Rolled into #149 with a note to revisit if RPA handling is ever wired. |
| CoD `>> 8` shift is wrong for `Cod.Major` | Undetermined without hardware evidence; docs are ambiguous (UInt16 "major code"). Kept as a **verification step inside #148** instead of an asserted bug. |
| `FormStateManager.Save()` on every `ResizeEnd` | Overlaps existing #91 (synchronous, no debounce). Not duplicated. |
| Classic reader `Task.Run` around `ReadBatteryProperties` | Deliberate and correct (blocking SetupAPI off the pool). Not a defect. |

## Appendix C — Labels

Added during this review: `bluetooth-stack`, `ui`, `security`. All issues are tagged with the
existing `bug`/`enhancement`/`refactor`/`concurrency`/`performance`/`adr-deviation`/`bluetooth`
labels as appropriate.
