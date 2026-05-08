# ADR-005 — Atomic JSON settings persistence

**Status:** Accepted  
**Date:** 2026-05-08

## Context

Settings are mutated from the UI thread (threshold sliders, ignore toggles) and read from background threads (poll loop). A partial write or a crash mid-write would corrupt the settings file and result in silent reset to defaults on next start.

## Decision

`ThresholdSettings.Save()` serialises the entire settings graph to a `.tmp` file in the same directory, then calls `File.Move(tmp, target, overwrite: true)`. This relies on the OS-level atomic rename guarantee on NTFS/ReFS.

All in-memory property access is protected by a `Lock` (`_thresholdLock`), ensuring reads and writes are consistent across threads.

## Rationale

- Atomic rename prevents partial writes: the target file is either the old version or the new version, never a mixture.
- A crash during the write leaves the `.tmp` file orphaned but the live settings file intact.
- `System.Text.Json` serialisation is simple, human-readable, and has no external dependencies.
- Storing the file in `%LOCALAPPDATA%` requires no elevated permissions and follows Windows per-user data conventions.

## Consequences

- On very high-frequency settings changes the `.tmp` file is written and renamed on every change. This is acceptable given that settings changes are user-initiated and infrequent.
- A corrupted or invalid settings file (e.g., from manual editing) resets to defaults (20/80) without crashing; a `Debug.WriteLine` is emitted.
