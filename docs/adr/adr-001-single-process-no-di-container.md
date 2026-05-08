# ADR-001 — Single process, manual dependency wiring

**Status:** Accepted (amended 2026-05-08)  
**Date:** 2026-05-08

## Context

The application is a single-user tray utility with a small, stable object graph: roughly a dozen collaborating classes. It has no HTTP endpoints, no plugin system, and no need to swap implementations at runtime beyond testability.

## Decision

All dependencies are constructed and wired manually in `Program.cs`. No dependency-injection container (e.g., `Microsoft.Extensions.DependencyInjection`) is used. Constructor injection is used exclusively; no service locator pattern.

All collaborators must have a **single public constructor**. Optional or nullable dependencies are never acceptable as constructor parameters in production code. When a collaborator is unavailable in a test context, inject a **null-object implementation** of the relevant interface instead.

## Rationale

- A DI container adds measurable startup overhead and a non-trivial dependency for a process that must start quickly and silently.
- The object graph is small enough that manual wiring is readable and explicit.
- Constructor injection alone enforces the same decoupling guarantees.
- Avoiding a container keeps the deployment artefact smaller (single self-contained `.exe`).
- Single-constructor discipline eliminates conditional `if (dependency is not null)` guards in production logic paths.

## Consequences

- Adding a new dependency requires editing `Program.cs`. Acceptable given the small graph.
- Testability is achieved via null-object implementations (`NullNotificationService`, etc.), not via nullable fields on production classes.

## Known violation requiring remediation

`LaptopBatteryMonitor` currently exposes a second "test" constructor that accepts `null` for both `ThresholdSettings` and `NotificationService`, storing them as nullable fields. This forces `if (_settings is not null && _notifier is not null)` guards inside `EvaluateThresholds` and `RefreshAsync`. See issue **#[TBD]** — _Refactor LaptopBatteryMonitor: replace nullable-field test constructor with null-object pattern_.
