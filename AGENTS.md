# AGENTS.md

BTChargeTrayWatcher is a Windows-only system-tray application that monitors
Bluetooth-device and laptop battery levels and raises Windows notifications
when configured threshold states change.

## Required validation

- Run before completing a code change:

  dotnet test

- Also run `dotnet publish -c Release` when changing project properties,
  packaging, deployment, application startup, WinRT integration, native
  interop, or the publish/runtime configuration.
- Do not report validation as passed if a required command failed or was not run.

## Runtime and ownership

- Preserve the Windows-only, `win-x64`, self-contained application model.
  Do not introduce cross-platform runtime paths or change target/runtime
  configuration without explicit approval.
- Keep composition manual in `Program.cs`; do not introduce a DI container.
- `BatteryMonitorService` owns long-running monitoring. Do not create a second
  polling, scanning, reader, alert, or notification execution path.
- All WinForms control and `NotifyIcon` mutations must be posted through the
  established UI `SynchronizationContext`; never mutate UI from a worker,
  callback, or continuation thread.
- Every long-running or I/O-bound operation must accept and propagate a
  `CancellationToken`. Do not block the UI thread.

## Discovery and device data

- Preserve the established source precedence, aggregation, alias resolution,
  and device-category filtering pipeline. Do not bypass it from a reader,
  tray action, settings change, or notification path.
- Passive enumeration is lower precedence than established active readers.
  Do not make it wake, connect to, or otherwise actively probe devices.
- Background scans must remain within the established polling policy.
  Deep scans must be user-initiated, time-bounded, and confirmation-gated.
- All device discovery and aggregation diagnostics must use `DiscoveryLogger`
  and remain local-only.
- Do not use a display name as a device identifier or treat device metadata as
  complete, current, or unique.
- Treat Bluetooth, WinRT, WMI, SetupAPI, and native interop operations as
  fallible. Preserve distinct states for unsupported, unavailable, incomplete,
  stale, and failed reads; do not convert them into fabricated battery values.

## State, settings, and alerts

- Store polling timing and alert hysteresis only in `PollingDefaults`; never
  inline changed timing or threshold-transition constants in execution logic.
- Preserve atomic settings persistence: write a temporary file and atomically
  replace the target. Do not overwrite settings directly.
- Preserve the existing settings-change notification flow after persisted
  values change.
- Do not emit duplicate alerts or reset hysteresis state merely because a scan,
  reader, or UI operation repeats.

## Boundaries

- Never commit credentials, API keys, tokens, device identifiers, local logs,
  or personally identifying Bluetooth data.
- Never swallow exceptions or use empty catch blocks.
- Do not modify `manifests/` or `winget/`.
- Get explicit approval before adding a NuGet package, changing target/runtime
  configuration, changing polling or scan strategy, adding a reader source,
  changing device recognition/filtering policy, changing logging retention or
  destination, or modifying startup registration.
- Add an ADR before adding a package, introducing a layer, changing device
  recognition/filtering, changing reader precedence, changing polling/deep-scan
  strategy, changing local logging policy, or changing persistence format.