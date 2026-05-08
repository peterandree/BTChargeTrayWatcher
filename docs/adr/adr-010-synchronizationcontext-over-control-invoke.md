# ADR-010 — SynchronizationContext capture over Control.Invoke

**Status:** Accepted  
**Date:** 2026-05-08

## Context

`TrayApp` is a WinForms host class. Its `NotifyIcon`, context menu items, and `ScanWindow` must only be mutated on the UI (STA) thread. Multiple background events arrive from thread-pool threads: `BluetoothBatteryMonitor.BackgroundRefreshCompleted`, `LaptopBatteryMonitor.BatteryUpdated`, `ScanCoordinator.ScanCompleted`, and others.

The conventional WinForms approach to marshal to the UI thread is `control.Invoke(...)` or `control.BeginInvoke(...)`. A `NotifyIcon` does not have a visible window handle, making `Invoke` unavailable until a form with a valid handle exists.

## Decision

`TrayApp` captures `SynchronizationContext.Current` at construction time:

```csharp
_uiContext = SynchronizationContext.Current
    ?? throw new InvalidOperationException("TrayApp must be created on the UI thread.");
```

All UI mutations from background threads are posted via `_uiContext.Post(...)`. `TrayApp` enforces construction on the UI thread by throwing immediately if the context is null.

## Rationale

- `SynchronizationContext` is available from the moment `Application.Run()` sets up the Windows message pump, which occurs before any background events can fire.
- It does not depend on any particular `Control` instance having a valid HWND.
- It is framework-agnostic: the same pattern works if the UI layer is ever changed (e.g., migrating to WPF or WinUI 3 in the future).
- The explicit null-check at construction time makes threading errors fail fast at startup rather than intermittently at runtime.

## Consequences

- `TrayApp` **must** be constructed on the UI thread after `Application.SetHighDpiMode` and `Application.EnableVisualStyles` but before `Application.Run`. This is enforced by the null-check; violating it causes an immediate `InvalidOperationException`.
- **Never** introduce `Control.Invoke` or `Control.BeginInvoke` into `TrayApp` or any class that calls back into it. All thread transitions go through `_uiContext.Post`.
- `ScanCoordinator` uses `BeginInvoke` on `ScanWindow` directly because `ScanWindow` is a `Form` with a valid HWND; this is a local exception and does not affect `TrayApp`.
