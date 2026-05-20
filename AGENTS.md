# AGENTS.md

Guidance for AI coding agents (Copilot, Claude, Cursor, etc.) working in this repository.

## Repository overview

`BTChargeTrayWatcher` is a single-project, Windows-only .NET 10 WinForms system-tray application that monitors the battery levels of connected Bluetooth devices and the host laptop, then fires Windows Toast notifications when configurable low / high thresholds are crossed.


See [`docs/architecture.md`](docs/architecture.md) for the full structural picture.

---

## Recent ADRs and agent boundaries

### New ADRs (015–019)
- **ADR-015:** Device alias migration & heuristics — agents must not bypass alias resolution or propose settings changes that ignore the alias pipeline or UI confirmation requirements.
- **ADR-016:** Device class/type filtering — agents must respect the default filtering policy and only propose code/UI changes that allow user override as described.
- **ADR-017:** Passive enumeration reader — if present, agents must treat it as a lower-precedence source and not introduce active device wakeups.
- **ADR-018:** Centralized discovery logging — all new device discovery or aggregation code must use `DiscoveryLogger` for structured, local-only logs.
- **ADR-019:** Manual deep scan — agents must not increase background scan frequency or bypass user confirmation for deep scans; deep scan logic must remain user-initiated and timeboxed.

### Agent boundaries for new features
- Do not bypass or remove alias, filtering, or logging logic.
- Do not introduce new background polling or device wakeups outside the documented scan intervals and deep scan UX.
- When adding new readers or device sources, follow the IBatteryReader pattern and update the aggregation pipeline and UI as per ADR-002, ADR-015, ADR-016, and ADR-017.
- All changes to device recognition, filtering, or logging must be justified with a new ADR or explicit documentation.

---

## Technology constraints

| Constraint | Value |
|---|---|
| Target framework | `net10.0-windows10.0.19041.0` |
| Runtime | `win-x64` (self-contained) |
| UI framework | Windows Forms (`UseWindowsForms`) |
| Language | C# 13, nullable enabled, implicit usings enabled |
| Key packages | `Svg` 3.4.7 (tray icon rendering), `System.Management` 9.0 (WMI for Classic BT) |

Do **not** introduce cross-platform abstractions, Linux or macOS code paths, or any runtime that is not `win-x64`.

---


## Source layout (see also new folders for ADR-017/ADR-018)

```
src/
  Program.cs                    – entry point, single-instance mutex, object graph wiring
  Monitoring/
    BluetoothBatteryMonitor.cs  – public facade for BT polling
    Scanner.cs                  – full device scan coordinator
    DeviceAggregationPipeline.cs– merges GATT + Classic results
    PollingOrchestrator.cs      – background poll loop, threshold alert logic
    PollingDefaults.cs          – all timing and hysteresis constants
    TaskTracker.cs              – fire-and-forget task lifecycle management
    DeviceBatteryInfo.cs        – value type: device id, name, battery %
    IBatteryReader.cs           – reader abstraction (ReadAllAsync)
    Classic/                    – Bluetooth Classic reader (SetupAPI + WMI P/Invoke)
    Gatt/                       – BLE GATT Battery Service (0x180F) reader
    LaptopBattery/              – laptop battery via System.Windows.Forms.PowerStatus / WMI
  Notifications/
    NotificationService.cs      – Windows Toast via WinRT ToastNotificationManager
  Settings/
    ThresholdSettings.cs        – JSON persistence in %LOCALAPPDATA%\BTChargeTrayWatcher\
    StartupRegistration.cs      – HKCU run-key for autostart
  Tray/
    TrayApp.cs                  – NotifyIcon host, event wiring
    ScanCoordinator.cs          – UI-thread scan orchestration, alert evaluation
    ScanWindow.cs               – scan results WinForms dialog
    TrayMenuBuilder.cs          – context-menu construction
    TrayIconRenderer.cs         – SVG → icon rasterization (normal / alert states)
    TrayIcons.cs                – embedded SVG string constants
  Utilities/
    BatteryDisplay.cs           – percentage → display string helper
    NativeMethods.cs            – P/Invoke declarations
tools/                          – excluded from build; maintenance / code-gen helpers
manifests/                      – WinGet package manifests
winget/                         – WinGet submission artefacts
```

---

## Coding conventions

- **No dependency injection container.** All dependencies are wired manually in `Program.cs`.
- **Interfaces are narrow.** `IBatteryReader` is the only public abstraction for battery readers; keep it that way unless there is a clear reason to add another.
- **Thread safety via `SynchronizationContext`.** UI mutations must be posted via `_uiContext.Post(...)`. Never call WinForms controls from a thread-pool thread.
- **Async / await throughout.** Use `ConfigureAwait(false)` on all non-UI awaits. UI continuations use `ConfigureAwait(true)` or post explicitly.
- **`CancellationToken` everywhere.** Every async method must accept a `CancellationToken` and forward it.
- **Settings persistence is atomic.** `ThresholdSettings` writes to a `.tmp` file and renames it over the target; do not change this pattern.
- **Hysteresis and polling timing** are centralised in `PollingDefaults`. Change constants there, not inline in logic.
- **`sealed` by default.** Mark classes `sealed` unless inheritance is explicitly required.
- **Records for options.** Pass constructor parameter groups as `sealed record` option types (see `ScannerOptions`, `PollingOrchestratorOptions`).

---

## Adding a new battery reader

1. Implement `IBatteryReader` (`ReadAllAsync(CancellationToken)`  →  `Task<List<DeviceBatteryInfo>>`).
2. Add it to `DeviceAggregationPipeline` (both parallel tasks in `ReadMergedAsync`).
3. Pass the new instance through `BluetoothBatteryMonitor`'s constructor chain into `ScannerOptions`.
4. Dispose it in `BluetoothBatteryMonitor.DisposeAsync` if it implements `IDisposable`.

## Adding a new settings property

1. Add the backing field and thread-safe property to `ThresholdSettings`.
2. Add the corresponding property to the private `SettingsDto` record.
3. Populate the field in `Load()` and include it in `Save()`.
4. Raise `Changed` (and `LaptopSettingsChanged` if laptop-specific) after writing.

## Modifying the tray menu

All menu construction lives in `TrayMenuBuilder`. Add new items there and wire click handlers in `TrayApp`.

---

## Build

```powershell
dotnet build
dotnet publish -c Release
```

The project targets `win-x64` and will not build on non-Windows hosts without the Windows SDK.

## Tests

There are no automated tests at this time. Unit tests for `PollingOrchestrator` (threshold/hysteresis logic) and `DeviceAggregationPipeline` (merge/dedup logic) are the highest-value additions.

---

## Out of scope for agents

- Do not modify `manifests/` or `winget/` — these are controlled by the WinGet submission pipeline.
- Do not add NuGet packages without explicit instruction.
- Do not change the `RuntimeIdentifier` or `TargetFramework`.
