---
title: Refactor tuple-typed APIs and collection elements to named record types
labels: refactoring, tests
assignees: ''
---

## Summary

Several areas of the codebase use C# tuples as method return types, API parameters, or collection element types. While tuples are convenient for short-lived, local groupings, they reduce readability and extensibility when used across component boundaries (public/internal APIs, method returns, or collections). This PR proposes replacing those tuples with small, named record/record-struct types.

## Why

- Named types improve discoverability and documentation (IntelliSense).
- Easier to extend later (add fields or behaviours) without refactoring call sites.
- Typed deconstruction and property names avoid fragile positional access in tests and production code.

## Changes proposed

- Add `DeviceProfile` record-struct for device classification results (replaces `(DeviceTransport, DeviceCategory)`).
- Add `DeviceIdentifier` record-struct (Id + Name) used by GATT reader and scan UI (replaces `(string Id, string Name)`).
- Replace `IEnumerable<(string id, string name)>` and similar API boundaries with `IEnumerable<DeviceIdentifier>`.
- Replace test-local tuple types with small test records (e.g. `BatteryRead`, `ScannerBuildResult`, `PollingOrchestratorBuildResult`).

## Files touched

- src/Monitoring/DeviceProfile.cs (new)
- src/Monitoring/DeviceIdentifier.cs (new)
- src/Monitoring/DeviceProfileClassifier.cs (return type changed)
- src/Monitoring/Gatt/GattBatteryReader.cs (signature updated)
- src/Tray/ViewModels/ScanViewModel.cs (SetShownItems signature)
- src/Tray/ScanWindow.cs (call site updated)
- tests/* (updated test fixtures and assertions)

## Migration / Compatibility

All changes are internal to the assembly (InternalsVisibleTo is already configured for tests). The refactor preserves behaviour and provides `Deconstruct` semantics for callers, so most call-sites deconstructing the old tuple remain compatible.

## Testing

- Updated and ran unit tests locally; all tests pass.

## Notes

If you prefer a single commit with PR referencing this issue, I can create it; otherwise please review the changes and I'll open a PR.