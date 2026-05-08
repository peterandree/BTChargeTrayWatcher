# Testing Strategy

## Overview

This document defines the test boundary for BTChargeTrayWatcher and catalogues every class by testability tier. The goal is to prevent wasted effort attempting to unit-test classes whose dependencies are only resolvable at runtime on a physical Windows machine with Bluetooth hardware.

---

## Tier 1 — Unit-testable, no infrastructure

All dependencies are injectable via constructor parameters or delegate options. These classes are fully covered by the test project.

| Class | Test file |
|---|---|
| `PollingOrchestrator` | `PollingOrchestratorClassifyTests`, `PollingOrchestratorPollTests`, `PollingOrchestratorUpdateAlertStateTests` |
| `LaptopBatteryMonitor` | `LaptopBatteryMonitorClassifyTests` |
| `Scanner` | `ScannerTests` |
| `TaskTracker` | `TaskTrackerTests` |
| `ThresholdSettings` | `ThresholdSettingsTests`, `ThresholdSettingsSnapshotTests` |
| `NotificationDispatcher` | `NotificationDispatcherTests` |
| `NtfyNotificationChannel.BuildStatusBody` | `NtfyStatusBodyTests` |
| `NtfyNotificationChannel` (alert routing) | `NtfyAlertBodyTests` |
| `NtfyTopicGenerator` | `NtfyTopicGeneratorTests` |
| `NtfyIntegrationSettings` | `NtfyIntegrationSettingsTests` |
| `DeviceAggregationPipeline` | `DeviceAggregationPipelineTests` |

---

## Tier 2 — Unit-testable after minor refactor

Classes with testable logic that is currently entangled with an untestable concern. Tracked as issues.

| Class | Blocker | Tracking |
|---|---|---|
| `LaptopBatteryMonitor` (single constructor) | Nullable-field test constructor; `_settings is not null` guards | [#41](https://github.com/peterandree/BTChargeTrayWatcher/issues/41) |
| `GattBatteryProcessor` (reconnect path) | Stale `BluetoothLEDevice` returns `null`; no seam to inject a factory | [#42](https://github.com/peterandree/BTChargeTrayWatcher/issues/42) |
| `TaskTracker` (add-before-schedule) | Race condition between `HashSet.Add` and `Task.Run`; fixed in current code, concurrency stress test pending | [#43](https://github.com/peterandree/BTChargeTrayWatcher/issues/43) |
| `NtfyNotificationChannel` (alert body extraction) | `Fire` is private; `BuildAlertBody` not yet extracted as `internal static` | No issue yet — follow-up to `BuildStatusBody` extraction |

---

## Tier 3 — Integration test candidates (do not attempt to unit-test)

These classes have a hard dependency on WinRT APIs, Win32 message loops, or physical Bluetooth hardware. They cannot be unit-tested without those runtimes present. They are documented here so contributors do not attempt to mock or stub the underlying WinRT types — doing so requires COM interop shims that are not worth the maintenance cost.

The correct approach for each class is an **integration test** that runs on a real Windows machine with Bluetooth enabled, executed as a separate test project gated on a `[Trait("Category", "Integration")]` filter and excluded from CI by default.

### WinRT Bluetooth

| Class | Blocking API | Notes |
|---|---|---|
| `GattBatteryReader` | `Windows.Devices.Bluetooth.GenericAttributeProfile.*` | Requires a paired GATT device. Full read path including service/characteristic enumeration. |
| `ClassicBatteryReader` | `Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync` | Requires a paired Classic BT device with an exposed battery service. |
| `GattBatteryProcessor` | `BluetoothLEDevice.FromIdAsync`, `GetGattServicesAsync` | Requires a live GATT device. Cache-invalidation-on-reconnect path (tracked in #42) should be covered here once the seam exists. |
| `GattConnectionCache` | `BluetoothLEDevice` handle lifecycle | Device handle eviction and re-creation are only observable with real hardware. |

### WinForms / Win32

| Class | Blocking API | Notes |
|---|---|---|
| `WindowsLaptopBatteryReader` | `System.Windows.Forms.SystemInformation.PowerStatus` | Requires a WinForms message pump. Returns meaningful data only on a physical laptop with a battery. |
| `BluetoothBatteryMonitor` (timer/power-mode) | `Microsoft.Win32.SystemEvents.PowerModeChanged` | `PowerModeChanged` requires a Win32 message loop. Sleep/resume transitions cannot be simulated in-process. |
| `LaptopBatteryMonitor` (timer/power-mode) | Same as above | Timer tick and resume-wake paths require a real power-mode event. |

### WinRT Notifications

| Class | Blocking API | Notes |
|---|---|---|
| `WindowsToastNotificationChannel` | `Microsoft.Toolkit.Uwp.Notifications` | Requires the UWP notification runtime. Toast delivery is only verifiable on a running Windows desktop session. |

---

## Integration test project (future)

When integration tests are introduced, create a separate project:

```
tests.integration/
    BTChargeTrayWatcher.Integration.Tests.csproj
    GattBatteryReaderTests.cs
    ClassicBatteryReaderTests.cs
    WindowsLaptopBatteryReaderTests.cs
    WindowsToastNotificationChannelTests.cs
```

All tests in that project must be decorated with `[Trait("Category", "Integration")]`. The CI pipeline excludes them by default:

```bash
dotnet test --filter "Category!=Integration"
```

Manual execution on a developer machine with hardware:

```bash
dotnet test --filter "Category=Integration"
```

---

## What is intentionally not tested

- `TrayApp` — Win32 tray icon lifecycle, WinForms message loop, and system-event wiring are UI integration concerns.
- `SettingsPersistence` — disk I/O with `System.Text.Json`; covered sufficiently by the `ThresholdSettingsSnapshotTests` round-trip which validates the data contract without touching the file system.
- `StartupRegistration` — Windows Registry write; environment-specific, no value in a unit test.
