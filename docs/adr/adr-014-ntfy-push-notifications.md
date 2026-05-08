# ADR-014: Mobile Push Notifications via ntfy.sh

**Status:** Accepted  
**Date:** 2026-05-08

---

## Context

The app currently delivers battery threshold alerts exclusively as Windows Toast notifications. Users want to receive the same alerts on their mobile device (iOS / Android) without running any additional server or subscribing to a paid push service.

---

## Decision

Use [ntfy.sh](https://ntfy.sh) as the push notification relay. The tray app sends a plain HTTP POST to `https://ntfy.sh/{topic}` when a threshold is crossed. The user installs the free ntfy app and subscribes to the same topic.

---

## Rationale

| Option | Pros | Cons |
|---|---|---|
| **ntfy.sh (public)** | Zero infra, free, open-source client, single HTTP call | Topics are publicly readable by default |
| SignalR / WebSocket hub | Reliable delivery | Requires a hosted server |
| Email (SMTP) | Universal | Latency, requires SMTP credentials |
| Pushover / Pushbullet | Good UX | Paid API key, vendor lock-in |

ntfy.sh wins on the hard constraint: no additional infrastructure.

---

## Consequences

### Positive

- No NuGet package added — `System.Net.Http.HttpClient` is sufficient.
- Feature is fully opt-in; all code paths are guarded behind `NtfySettings.IsEnabled`.
- `NtfyNotificationService` implements the existing `INotificationService` interface, keeping the call site in `TrayApp` unchanged.
- Settings round-trip cleanly through the existing `SettingsPersistence` + `SettingsDto` mechanism (ADR-005).
- `NtfySettings` is an immutable record injected via the single non-nullable constructor of `NtfyNotificationService` (ADR-001).

### Negative / Accepted risks

- HTTP POST can fail silently (network unavailable, server down). Failures are logged to `Debug.WriteLine` only — consistent with how `NotificationService` handles toast failures.
- Public topics are world-readable. Mitigated by generating a random UUID-based default topic name in the wizard and documenting the risk.

---

## Compliance with existing ADRs

| ADR | Requirement | How this plan satisfies it |
|---|---|---|
| ADR-001 | Single constructor, no nullable fields | `NtfyNotificationService(ThresholdSettings settings)` — one constructor, all fields non-nullable |
| ADR-005 | Atomic settings persistence | `NtfySettings` fields added to `SettingsDto`; `SettingsPersistence` round-trips them via the existing tmp-swap pattern |
| ADR-006 | Notifications through `INotificationService` | `NtfyNotificationService` implements the interface; `TrayApp` calls both services |
| ADR-010 | No `Control.Invoke` — use `SynchronizationContext` | Wizard dialog is shown from the UI thread via the menu click handler; no cross-thread marshal required |
| ADR-013 | Settings domain vs. persistence SRP | `NtfySettings` lives in the domain (`ThresholdSettings.Ntfy`); serialisation lives in `SettingsPersistence` |
