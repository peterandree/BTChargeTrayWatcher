# BTChargeTrayWatcher

Windows system-tray app (WinForms, .NET 10, win-x64) that monitors Bluetooth device battery levels and the laptop battery, shows charge percentages in the tray icon, and fires Windows toast notifications when thresholds are crossed. No UI window beyond the tray icon and a scan popup.

## Tech Stack

- C# / .NET 10, target `net10.0-windows10.0.19041.0`
- WinForms (`UseWindowsForms=true`) — tray icon, context menu, scan window
- `System.Management` 9.0.0 — WMI queries for Classic BT battery and laptop battery
- `Svg` 3.4.7 — SVG rendering for dynamic tray icons
- Windows Runtime APIs (WinRT) via `AllowUnsafeBlocks` — GATT BLE battery service
- `OutputType=WinExe`, `RuntimeIdentifier=win-x64`, nullable enabled, implicit usings enabled
- No test project currently
- Build: `dotnet build` / `dotnet publish`
- Packaging: WinGet manifest in `winget/`, MSIX manifest in `manifests/`

## Project Structure

```
src/
  Program.cs                          — entry point: Application.Run(new TrayApp())
  Tray/
    TrayApp.cs                        — ApplicationContext subclass; owns NotifyIcon, orchestrates everything
    TrayMenuBuilder.cs                — builds the right-click context menu from device data
    TrayIconRenderer.cs               — renders SVG-based dynamic tray icon
    TrayIcons.cs                      — icon assets / cache
    ScanCoordinator.cs                — triggers manual and scheduled scans
    ScanWindow.cs / .resx             — WinForms popup showing current scan results
  Monitoring/
    BluetoothBatteryMonitor.cs        — top-level monitor; aggregates GATT + Classic readers
    IBatteryReader.cs                 — interface: Task<IEnumerable<DeviceBatteryInfo>> ReadAsync()
    DeviceBatteryInfo.cs              — record: DeviceName, BatteryPercent, Source
    DeviceAggregationPipeline.cs      — deduplicates results from multiple readers
    Scanner.cs                        — enumerates paired BT devices
    PollingOrchestrator.cs            — timer-based periodic polling, raises DevicesUpdated event
    PollingDefaults.cs                — polling interval constants
    TaskTracker.cs                    — tracks in-flight async tasks to avoid overlapping scans
    Gatt/                             — WinRT GATT Battery Service reader (BLE devices)
    Classic/                          — WMI-based reader for Classic Bluetooth devices
    LaptopBattery/                    — WMI SystemPowerStatus reader for laptop battery
  Notifications/                      — Windows toast notification helpers
  Settings/
    ThresholdSettings.cs              — per-device and global charge thresholds; persisted to JSON in AppData
    StartupRegistration.cs            — Windows startup registry key management
  Utilities/                          — shared helpers
tools/                                — build/dev scripts (excluded from compilation via csproj)
winget/                               — WinGet package manifest
manifests/                            — MSIX/packaging manifests
GlobalSuppressions.cs                 — project-wide analyzer suppressions
Directory.Build.props                 — shared MSBuild properties
```

## Architecture Notes

- `TrayApp` is the root owner of all long-lived objects. It creates `PollingOrchestrator`, subscribes to `DevicesUpdated`, and delegates rendering/menu-building to the Tray sub-classes.
- Battery reading is split by protocol: `Gatt/` uses WinRT `GattDeviceService` (BLE Battery Service UUID `0x180F`); `Classic/` uses WMI `Win32_PnPEntity` + registry; `LaptopBattery/` uses WMI `Win32_Battery`.
- `DeviceAggregationPipeline` merges results from all readers and deduplicates by device address/name to avoid double-counting devices that appear in both GATT and Classic.
- `ThresholdSettings` persists to `%AppData%\BTChargeTrayWatcher\settings.json`. Never hardcode paths — always use `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)`.
- All WinRT async calls must be marshalled correctly — use `.AsTask()` when bridging WinRT `IAsyncOperation` to .NET `Task`.

## Commands

```bash
# Build debug
dotnet build

# Build release (single-file, no debug symbols per csproj)
dotnet publish -c Release

# Run locally
dotnet run
```

## Coding Conventions

- C# 12, nullable enabled — no `#nullable disable`, no `!` null-forgiving operators without a comment explaining why
- `async`/`await` throughout — no `.Result` or `.Wait()` on Tasks (deadlock risk on UI thread)
- All WMI queries go in `Classic/` or `LaptopBattery/` — never inline WMI elsewhere
- All WinRT GATT calls go in `Gatt/` — never inline WinRT elsewhere
- Tray icon updates must happen on the UI thread — use `SynchronizationContext` or `Control.Invoke` when updating from background polling
- New battery source types implement `IBatteryReader` and are registered in `BluetoothBatteryMonitor` — no ad-hoc readers outside this pattern
- Settings persistence only via `ThresholdSettings` — never write to registry or AppData directly from other classes

## Agent Boundaries

- ✅ Always: read the relevant `IBatteryReader` impl before adding a new one; check `DeviceAggregationPipeline` before changing deduplication logic; run `dotnet build` before marking done
- ⚠️ Ask first: adding a NuGet package, changing the WinGet/MSIX manifest, modifying `Directory.Build.props`
- 🚫 Never: call `.Result`/`.Wait()` on Tasks, hardcode `%AppData%` paths, put WMI or WinRT calls outside their designated folders, write settings outside `ThresholdSettings`
