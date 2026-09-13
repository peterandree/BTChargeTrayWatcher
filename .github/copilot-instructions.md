# BTChargeTrayWatcher

Windows system-tray app (WinForms, .NET 10, win-x64) that monitors Bluetooth device battery levels and the laptop battery, shows charge percentages and alerts in the tray icon, and fires Windows toast and optional ntfy.sh mobile push notifications when thresholds are crossed. No UI window beyond the tray icon, an Options dialog, and a scan popup.

## Tech Stack

- C# / .NET 10, target `net10.0-windows10.0.19041.0`
- WinForms (`UseWindowsForms=true`) — tray icon, context menu, Options dialog, scan window
- `System.Management` 10.0.8 — WMI queries (Classic BT battery properties, laptop battery depth/health via `Win32_Battery`)
- `Svg` 3.4.7 — SVG rendering for the dynamic tray icon (`TrayIconRenderer.cs`)
- Windows Runtime APIs (WinRT) via `AllowUnsafeBlocks` — GATT BLE battery service, `DeviceWatcher`
- `OutputType=WinExe`, `RuntimeIdentifier=win-x64`, nullable enabled, implicit usings enabled
- Real test project: `tests/BTChargeTrayWatcher.Tests/` — xUnit v3 on the Microsoft Testing Platform runner (see `global.json`). ~29 test files. Run with `dotnet test`.
- Build: `dotnet build` / `dotnet publish`
- Packaging: WinGet manifest in `winget/` and `manifests/`, Inno Setup installer script, release workflow builds the real installer (not a bare single-file EXE)

## Project Structure

```
src/
  Program.cs                          — entry point: constructs the full cooperation stack, wires TrayApp
  Tray/
    TrayApp.cs                        — owns NotifyIcon, orchestrates tray icon/alert state, UI-thread marshaling
    TrayMenuBuilder.cs                — builds the right-click context menu (KNOWN GAP: BuildLaptopMenuItem/BuildLowMenu/BuildHighMenu/BuildDevicesMenu are dead stubs — see issue #156/#157; real per-device/laptop threshold controls live in OptionsForm now)
    TrayIconRenderer.cs               — renders SVG-based dynamic tray icon
    TrayIcons.cs                      — icon assets / cache
    ScanCoordinator.cs                — triggers manual and scheduled scans, marshals results to the UI thread
    ScanWindow.cs / .resx             — WinForms popup showing current scan results, supports manual "deep scan" (ADR-019)
    OptionsForm.cs / .resx            — tabbed Options dialog (Devices grid, Notifications/ntfy, General incl. laptop thresholds, autostart)
    OptionsFormManager.cs             — singleton show/focus management for OptionsForm
    FormStateManager.cs               — persists/restores window geometry and grid/list column state
    ViewModels/                       — DevicesViewModel, OptionsViewModel, ScanViewModel, TrayViewModel (SRP split from the Form/App classes)
  Monitoring/
    BluetoothBatteryMonitor.cs        — top-level BT monitor; wires PollingOrchestrator + Scanner around BatteryReaderOrchestrator
    BatteryReaderOrchestrator.cs      — single merge point for GATT + Classic reads; owns alias resolution, category filtering, DiscoveryLogger (ADR-018)
    PollingOrchestrator.cs            — timer-driven poll cycle, per-device alert-state FSM with hysteresis (ADR-004/ADR-011)
    Scanner.cs                        — manual/deep-scan path (consumes the orchestrator's merged output; ReadClassic slot is vestigial, see issue #157)
    DeviceWatcherService.cs           — passive BLE + Classic AEP device watching (ADR-017: no active radio queries on background poll)
    DeviceCapabilityCache.cs          — success-only capability cache
    DeviceProfileClassifier.cs        — device category classification (ADR-016)
    BluetoothDeviceExtensions.cs      — WinRT property extraction helpers (transport, class-of-device)
    AliasSuggestionService.cs / AliasSuggestion.cs — fuzzy device-name alias suggestion pipeline (ADR-015)
    TaskTracker.cs / TaskExtensions.cs — cooperative shutdown task tracking (ADR-007), timeout helpers
    WatchedDevice.cs, DeviceBatteryInfo.cs, DeviceIdentifier.cs, DeviceProfile.cs, Protocol.cs, PollingDefaults.cs, BluetoothMonitoringInfrastructure.cs
    Gatt/
      GattConnectionManager.cs        — production GATT reader: per-call connect + read, NO object caching (ADR-065), supports 0x180F and 0x182B (#153 phase 1)
    Classic/
      ClassicBatteryReader.cs         — production Classic BT reader (SetupAPI enumeration + property reads)
      ClassicBluetoothConnectionChecker.cs, ClassicBluetoothDeviceEnumerator.cs, ClassicBatteryPropertyReader.cs, SetupApiNative.cs
    LaptopBattery/
      LaptopBatteryMonitor.cs         — laptop battery poll/alert loop (PowerStatus-based; WMI enrichment for depth via #154)
      WindowsLaptopBatteryReader.cs   — reads System.Windows.Forms.PowerStatus (primary) + WMI Win32_Battery (discharge rate, health)
    Logging/
      DiscoveryLogger.cs              — structured, local-only discovery/aggregation logging (ADR-018)
  Notifications/
    INotificationChannel.cs, INotificationService.cs, NotificationDispatcher.cs — dispatch abstractions
    NotificationService.cs, WindowsToastNotificationChannel.cs — Windows Toast
    NtfyNotificationChannel.cs, NtfyIntegrationSettings.cs, NtfyTopicGenerator.cs — ntfy.sh mobile push (opt-in)
    NullNotificationService.cs        — null-object pattern for tests
  Settings/
    ThresholdSettings.cs              — domain model: global/per-device/laptop thresholds, ignore/overlay-exclude sets, ntfy settings; lock-protected, event-driven via SettingsEventBus
    SettingsEventBus.cs               — Changed / LaptopSettingsChanged events, raised outside the settings lock (ADR re: re-entrancy)
    SettingsPersistence.cs            — JSON load/save (atomic temp-file swap), owns SettingsDto — separate from domain (ADR-013)
    StartupRegistration.cs            — Windows autostart registry/Scheduled-Task management
    UiSettings.cs / IUiSettings.cs / UiWindowState.cs — window geometry/UI-state persistence (separate from ThresholdSettings)
  Utilities/
    BatteryDisplay.cs                 — shared battery percentage/duration/power/health formatters (handles the -1 "unknown" sentinel, see #144)
    BatteryTrendHelper.cs, DeviceNameNormalizer.cs, JaroWinkler.cs, NativeMethods.cs
tests/
  BTChargeTrayWatcher.Tests/          — real xUnit v3 test project, ~29 files, run via `dotnet test`
tools/                                — build/dev scripts and genicon (SVG→ICO generator), excluded from main compilation
winget/, manifests/                   — packaging manifests
docs/
  adr/                                — architecture decision records — READ RELEVANT ADRs BEFORE CHANGING POLLING, ALERTING, OR SETTINGS CODE
  architecture.md                     — current architecture narrative, kept in sync with Program.cs wiring
  stale/                              — archived tracking docs / superseded plans, historical reference only
GlobalSuppressions.cs, Directory.Build.props, Directory.Build.test.props
```

## Architecture Notes

- `Program.cs` is the single composition root (ADR-001: no DI container, no service locator, constructor injection only). Every production dependency is constructed and wired there — if you add a new component, wire it there and nowhere else.
- `BatteryReaderOrchestrator` is the **single** merge point for GATT + Classic Bluetooth reads (both the background poll and the manual "Scan devices…" path consume its output). Do not add a second independent merge path — see issue #157 for a known vestigial exception in `Scanner`/`ScannerOptions` being cleaned up.
- `PollingOrchestrator` is the single source of truth for alert state (ADR-011); it owns the per-device `BatteryAlertState` FSM with hysteresis. Tray icon overlay state is the OR of the Bluetooth alert flag and the laptop alert flag, computed in `TrayViewModel`/`TrayApp`.
- The laptop battery has **no equivalent of `IsIgnored`** — `ExcludeLaptopFromTrayIconOverlay` only suppresses the tray glyph, not notifications or polling. Tracked in issue #156; do not assume laptop exclusion is symmetric with BT device exclusion (`ThresholdSettings.IsIgnored`) until that issue is resolved.
- `ThresholdSettings` persists to `%AppData%\BTChargeTrayWatcher\settings.json` via `SettingsPersistence` (SRP split, ADR-013) — never write settings JSON directly from other classes, and never hardcode the AppData path.
- All WinRT async calls must bridge via `.AsTask(cancellationToken)`. `GattConnectionManager` deliberately does **no object caching** of `BluetoothLEDevice`/`GattDeviceService`/`GattCharacteristic` — this was a deliberate fix for a peripheral-sleep regression; do not reintroduce caching without reading the relevant ADR first.
- Background polls must not perform active radio queries — `DeviceWatcherService` provides passive `IsConnected` data (ADR-017). Active per-device connection checks are reserved for the user-initiated manual deep scan (ADR-019).
- `-1` is the sentinel for "battery level unknown" throughout the codebase (`BatteryDisplay`, `LaptopBatteryInfo`, ntfy status body) — Windows cannot distinguish a genuinely-full battery from an unknown level at the `PowerStatus` API level (#144); never treat `-1` as a real percentage.
- Known dead/vestigial code being tracked, not yet removed: `TrayMenuBuilder.BuildLowMenu/BuildHighMenu/BuildLaptopMenuItem/BuildDevicesMenu` (stubs, #156), `Scanner.ReadClassic`/`ScannerOptions.ReadClassic` (permanently empty, #157). Do not treat these as working examples to copy from.

## Commands

```bash
# Build debug
dotnet build

# Build release
dotnet publish -c Release

# Run tests
dotnet test

# Run locally
dotnet run
```

## Coding Conventions

- C# 14 (see `LangVersion` in project files), nullable enabled — no `#nullable disable`, no `!` null-forgiving operator without a comment explaining why
- `async`/`await` throughout — no `.Result` or `.Wait()` on Tasks (deadlock risk on the UI thread)
- All WMI queries stay in `Classic/` or `LaptopBattery/` — never inline WMI elsewhere
- All WinRT GATT/DeviceWatcher calls stay in `Gatt/`, `DeviceWatcherService.cs`, or `Classic/` — never inline WinRT elsewhere
- Tray/UI updates must happen on the UI thread — use the captured `SynchronizationContext`, never raw `Control.Invoke`/`BeginInvoke` outside `ScanCoordinator` (which has a documented, narrow exception for `ScanWindow`)
- Settings persistence only via `ThresholdSettings`/`SettingsPersistence` — never write to the registry or AppData directly from other classes
- New battery-reading logic goes through `BatteryReaderOrchestrator` — do not add ad-hoc readers outside the orchestrator's GATT/Classic delegate pattern
- Every settings property addition needs: domain field + property in `ThresholdSettings`, a line in `SettingsSnapshot`, a line in `SettingsDto`, load/save wiring in `SettingsPersistence`, and (if user-facing) a control in `OptionsForm.cs`

## Agent Boundaries

- ✅ Always: read the relevant ADR in `docs/adr/` before changing polling, alerting, settings-persistence, or threading code; check `BatteryReaderOrchestrator` before touching device-read merge logic; run `dotnet build` and `dotnet test` before marking done; check `docs/architecture.md` is still accurate if you change wiring in `Program.cs`
- ⚠️ Ask first: adding a NuGet package, changing the WinGet/MSIX/Inno Setup manifests, modifying `Directory.Build.props`, deleting anything under `docs/adr/`
- 🚫 Never: call `.Result`/`.Wait()` on Tasks, hardcode `%AppData%` paths, put WMI or WinRT calls outside their designated folders, write settings outside `ThresholdSettings`/`SettingsPersistence`, add object caching to `GattConnectionManager`, copy the dead `TrayMenuBuilder` stub methods as a pattern for new menu code
