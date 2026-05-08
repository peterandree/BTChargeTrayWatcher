# ADR-006 — WinRT Toast Notifications via AUMID registration

**Status:** Accepted  
**Date:** 2026-05-08

## Context

The application must surface battery alerts as Windows Action Center notifications. Options considered:

1. `NotifyIcon.ShowBalloonTip` — deprecated; on Windows 10+, balloons are silently suppressed by the OS in many configurations.
2. WinRT `ToastNotificationManager` — the modern, supported path; appears in the Action Center, supports click callbacks, respects Focus Assist / Do Not Disturb.
3. Third-party notification library — unnecessary dependency.

WinRT Toast requires the calling process to be associated with an Application User Model ID (AUMID). Packaged MSIX apps get this automatically; an unpackaged `.exe` must register the AUMID manually.

## Decision

Use `Windows.UI.Notifications.ToastNotificationManager` directly. On startup, `NotificationService` writes the AUMID registration to `HKCU\Software\Classes\AppUserModelId\BTChargeTrayWatcher` with `DisplayName` and `IconUri` values pointing to the running executable.

## Rationale

- WinRT Toasts are the only reliable notification mechanism for unpackaged Win32 apps on Windows 10/11.
- AUMID registration under `HKCU` requires no elevation.
- Click callbacks (`toast.Activated`) allow the notification to re-open the scan window, improving usability.
- `_toastsSupported` is checked at startup via `ToastNotificationManager.History`; if the runtime is unavailable (unusual OS configurations) the service degrades silently.

## Consequences

- One registry key is written to `HKCU` on every startup. This is idempotent and leaves no persistent side-effects beyond the key itself.
- The `TargetFramework` must include the WinRT projection (`net10.0-windows10.0.19041.0`). The minimum Windows build is 19041 (Windows 10 2004).
