
```markdown
# BTChargeTrayWatcher

A Windows system-tray application that monitors the battery levels of connected
Bluetooth devices and raises desktop notifications when a device crosses a
configurable low or high threshold.

---

## Prerequisites

| Requirement | Detail |
|---|---|
| Windows | Windows 10 1903 or later (WinRT GATT APIs required) |
| .NET SDK | .NET 8 or later |
| Bluetooth | At least one Bluetooth adapter; devices must be paired |

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

### Threshold defaults

| Setting | Default | Description |
| :-- | :-- | :-- |
| Low threshold | 20 % | Notify when battery drops to or below this level |
| High threshold | 80 % | Notify when battery rises to or above this level |

A 2 % hysteresis band prevents repeated notifications at the boundary.

### Per-device overrides

Each device can have its own low/high thresholds, independent of the global
defaults.

### Ignored devices

Devices added to the ignore list are tracked but never trigger notifications.

### Startup registration

The application can register itself to start with Windows via the tray menu.

---

## Architecture

```
Program.cs
└── BluetoothBatteryMonitor          (src/Monitoring/)
    ├── GattBatteryReader            (src/Monitoring/Gatt/)
    │   └── Reads battery via BLE GATT Battery Service (0x180F)
    ├── ClassicBatteryReader         (src/Monitoring/Classic/)
    │   └── Reads battery via Windows.Devices.Enumeration / WMI
    ├── PollingOrchestrator          timer-driven 60 s poll cycle
    ├── Scanner                      on-demand full scan (GATT + Classic merged)
    └── TaskTracker                  tracks background tasks for clean shutdown
NotificationService                  (src/Notifications/)
    └── Raises Windows toast notifications for low/high events
ThresholdSettings                    (src/Settings/)
    └── Persists thresholds and ignored-device list; fires Changed event
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

