# ADR-013: Settings classes must not mix domain state with persistence or OS-registration concerns

## Status
Accepted

## Context
`ThresholdSettings` accumulated three independent responsibilities over time:

1. **Domain model** — threshold values, per-device overrides, ignored/excluded device sets, lock coordination, change events.
2. **Persistence** — JSON serialisation via a nested `SettingsDto`, atomic tmp-file write, file-path construction under `%LOCALAPPDATA%`.
3. **OS-registration** — `RunOnStartup` delegating to `StartupRegistration.IsEnabled` (registry write), with no connection to threshold data.

The constructor created a real file path and called `Load()`, making any test that touched the domain object dependent on the filesystem. There was no injection point for persistence.

## Decision

Split into two runtime objects:

### `ThresholdSettings` — domain only
- Owns all fields, properties, overrides, lock, and events.
- Constructor sets defaults (`Low=20`, `High=80`); does **not** touch the filesystem.
- Exposes `internal Snapshot()` and `internal ApplySnapshot(SettingsSnapshot)` for `SettingsPersistence` to use without exposing mutable internals publicly.
- `RunOnStartup` is removed. Callers reference `StartupRegistration.IsEnabled` directly.

### `SettingsPersistence` — I/O only
- Constructor takes a `ThresholdSettings` and the file path (derived internally from `%LOCALAPPDATA%`).
- Subscribes to `settings.Changed` and auto-saves on every mutation.
- Exposes a single `Load()` method called once from `Program.cs` after construction.
- Owns `SettingsDto` as a private nested record; the DTO is invisible outside this class.
- Temporarily unsubscribes from `Changed` during `Load()` to prevent a save-on-load round-trip.

`SettingsSnapshot` is an `internal sealed record` in the `ThresholdSettings.cs` file; it is the only coupling surface between the two classes.

## Consequences

**Positive**
- `ThresholdSettings` can be instantiated in tests without touching disk.
- `SettingsPersistence` can be replaced (e.g. with an in-memory stub) without touching domain logic.
- `SettingsDto` is encapsulated inside the persistence layer; no external code can accidentally depend on the serialisation shape.
- `RunOnStartup` is no longer falsely attributed to the threshold domain.

**Negative / trade-offs**
- Slight indirection: `Program.cs` now holds both `settings` and `persistence` references.
- `Snapshot()` / `ApplySnapshot()` must be kept in sync with any new fields added to `ThresholdSettings`. Convention: new field → update both methods.

## Known violation requiring remediation
None after this refactor. Previous state (single-class mix) is resolved in branch `refactor/threshold-settings-srp`.
