
```markdown
# BTChargeTrayWatcher

A Windows system-tray application that monitors the battery levels of connected
Bluetooth devices and raises desktop notifications when a device crosses a
configurable low or high threshold.

---

## Prerequisites

| Requirement | Detail |
|---|---|
| Windows | Windows 10 2004 (build 19041) or later |
| .NET SDK | .NET 10 |
| Bluetooth | At least one Bluetooth adapter; devices must be paired |

> **Build tooling note:** The project generates a `.pri` resource file using the
> AppxPackage MSBuild targets that ship with Visual Studio. If the build fails
> with a missing `Microsoft.Build.Packaging.Pri.Tasks.dll` error, install the
> **Desktop development with C++** or **Universal Windows Platform development**
> workload in Visual Studio. On CI or machines without VS, set the
> `VSINSTALLDIR` environment variable or pass
> `/p:AppxMSBuildToolsPath=<path>` explicitly.

---

## Build

```powershell
git clone https://github.com/peterandree/BTChargeTrayWatcher.git
cd BTChargeTrayWatcher
dotnet build BTChargeTrayWatcher.sln
```


## Run

```powershell
dotnet run --project BTChargeTrayWatcher.csproj
```

Or publish a self-contained single-file executable:

```powershell
dotnet publish BTChargeTrayWatcher.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o ./publish
.\publish\BTChargeTrayWatcher.exe
```


---

## Configuration

Settings are persisted automatically. The following can be configured via the
tray context menu or by editing the settings file directly.

### Global thresholds

| Setting | Default | Description |
| :-- | :-- | :-- |
| Low threshold | 20 % | Notify when a Bluetooth device battery drops to or below this level |
| High threshold | 80 % | Notify when a Bluetooth device battery rises to or above this level |
| Laptop low threshold | 20 % | Same as above, applied to the built-in laptop battery |
| Laptop high threshold | 80 % | Same as above, applied to the built-in laptop battery |

A 2 % hysteresis band prevents repeated notifications at the boundary.

### Per-device overrides

Each Bluetooth device can have its own low/high thresholds, independent of the
global defaults.

### Ignored devices

Devices added to the ignore list are tracked but never trigger notifications.

### Tray icon overlay exclusions

The tray icon displays an alert overlay (⚠) when any monitored device or the
laptop battery is in an alert state. Individual devices and the laptop battery
can be excluded from this overlay so their alert state does not affect the tray
icon — useful for devices that are always near a threshold boundary.

### Tray context menu

The tray menu mirrors the Options dialog, so most settings can be changed
without opening it:

| Entry | What it does |
| :-- | :-- |
| `<device>  55 %` | One submenu per detected device |
| ↳ Low / High threshold | Per-device threshold override (falls back to the global value) |
| ↳ Exclude from tray icon alert | Same as the Options dialog's *Excluded* column for the overlay |
| ↳ Ignore device (no notifications) | Tracked but never alerts |
| Laptop battery | Laptop thresholds and exclusions (see below) |
| Low / High threshold | Global thresholds for every device without an override |

> The device list and the tick marks are re-read every time the menu opens, so
they stay in sync with the Options dialog.

### Excluding the laptop battery

| Setting | Effect |
| :-- | :-- |
| Exclude laptop from tray icon overlay | The laptop's alert state no longer influences the tray icon or the tooltip's `!` marker; notifications still fire |
| Exclude laptop from monitoring and alerts | The laptop battery is not evaluated at all: no notifications, no alert state, no overlay. The tooltip still shows the current charge, marked `(not monitored)` |

Both are available in the tray menu (right-click the icon → **Laptop battery**)
and in **Options → General**.

### Startup registration

The application can register itself to start with Windows via the tray menu.

---

## Architecture

```
Program.cs
└── BluetoothBatteryMonitor          (src/Monitoring/)
    ├── GattConnectionManager       (src/Monitoring/Gatt/)
    │   └── Reads battery via BLE GATT Battery Service (0x180F), no object caching
    ├── ClassicBatteryReader         (src/Monitoring/Classic/)
    │   └── Reads battery via Windows.Devices.Enumeration / SetupAPI
    ├── PollingOrchestrator          timer-driven 60 s poll cycle
    ├── Scanner                      on-demand full scan (GATT + Classic merged)
    └── TaskTracker                  tracks background tasks for clean shutdown
LaptopBatteryMonitor                 (src/Monitoring/LaptopBattery/)
    └── Monitors the built-in laptop battery; reacts only to laptop threshold changes
NotificationService                  (src/Notifications/)
    └── Raises Windows toast notifications for low/high events
ThresholdSettings                    (src/Settings/)
    └── Persists thresholds, ignored-device list, and overlay exclusions; fires
        Changed (all settings) and LaptopSettingsChanged (laptop thresholds only)
TrayIcon                             (src/Tray/)
    └── System-tray icon, context menu, manual scan trigger
```

**Reading pipeline:** On each poll tick both readers run in parallel. Results
are merged and deduplicated by stable `DeviceId`. The `BatteryAlertState`
machine (`Normal / Low / High`) determines whether a notification is warranted,
suppressing duplicate alerts for devices already in a given state.

**Shutdown:** `BluetoothBatteryMonitor` implements `IAsyncDisposable`. Disposal
cancels the shutdown `CancellationTokenSource`, waits for all tracked background
tasks, then releases all managed resources in order.

---

## Known Limitations

- GATT battery reads require the device to support the standard Battery Service
characteristic (UUID `0x180F`). Devices that expose battery level only via
proprietary means fall back to the Classic reader.
- The Classic reader relies on Windows device enumeration properties; some
devices report battery level only when actively connected.
- Multiple simultaneous Bluetooth adapters are not explicitly tested.
- Runs on Windows only; no cross-platform support is planned.

---

## License

MIT — see [LICENSE](LICENSE).

