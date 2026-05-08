# Architecture Decision Records

This directory captures significant design decisions made during the development of `BTChargeTrayWatcher`. Each record documents the context, the decision, and the rationale, so that future contributors (human or AI) can understand *why* the code is shaped the way it is.

| ID | Title | Status |
|---|---|---|
| [ADR-001](adr-001-single-process-no-di-container.md) | Single process, manual dependency wiring | Accepted |
| [ADR-002](adr-002-dual-bluetooth-reader-strategy.md) | Dual Bluetooth reader strategy (GATT + Classic) | Accepted |
| [ADR-003](adr-003-polling-over-push.md) | Polling-based monitoring over event-driven push | Accepted |
| [ADR-004](adr-004-threshold-hysteresis.md) | Hysteresis for threshold alert state transitions | Accepted |
| [ADR-005](adr-005-atomic-settings-persistence.md) | Atomic JSON settings persistence | Accepted |
| [ADR-006](adr-006-winrt-toast-notifications.md) | WinRT Toast Notifications via AUMID registration | Accepted |
| [ADR-007](adr-007-tasktracker-cooperative-shutdown.md) | TaskTracker for cooperative async shutdown | Accepted |
