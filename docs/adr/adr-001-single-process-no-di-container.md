# ADR-001 — Single process, manual dependency wiring

**Status:** Accepted  
**Date:** 2026-05-08

## Context

The application is a single-user tray utility with a small, stable object graph: roughly a dozen collaborating classes. It has no HTTP endpoints, no plugin system, and no need to swap implementations at runtime beyond testability.

## Decision

All dependencies are constructed and wired manually in `Program.cs`. No dependency-injection container (e.g., `Microsoft.Extensions.DependencyInjection`) is used. Constructor injection is used exclusively; no service locator pattern.

## Rationale

- A DI container adds measurable startup overhead and a non-trivial dependency for a process that must start quickly and silently.
- The object graph is small enough that manual wiring is readable and explicit.
- Constructor injection alone enforces the same decoupling guarantees.
- Avoiding a container keeps the deployment artefact smaller (single self-contained `.exe`).

## Consequences

- Adding a new dependency requires editing `Program.cs`. Acceptable given the small graph.
- Unit testing requires constructing real dependencies or passing test doubles manually. Acceptable; the priority is integration correctness, not unit-test speed.
