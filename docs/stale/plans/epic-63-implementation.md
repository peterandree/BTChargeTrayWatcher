> **⚠️ STALE — archived to `docs/stale/` on 2026-09-13.**
> Epic #63 (Windows Cooperation refactor) shipped in 2026-05. This scaffold plan describes an early WIP state; the classes listed as "stubs" are long since fully implemented or, in some cases, since removed as dead code (#149). Preserved for historical reference only.

---

# Epic #63 — Move to Windows Cooperation for Battery Monitoring

This file is an implementation plan scaffold created by the agent to start work on epic #63 and its dependent issues (#64–#69).

High-level sequence:

1. Implement Windows-first device discovery (#64)
2. Implement GATT connection manager with hard timeouts and no object caching (#65)
3. Implement PnP watcher and BatteryReaderOrchestrator (#66)
4. Wire `PollingOrchestrator` and `ScanCoordinator` into the new pipeline (#67)
5. Remove Classic/WMI reader and cleanup (#68)
6. Run production checklist, hardware verification, and tests (#69)

Repository files added by this branch as initial scaffolding:

- `src/Monitoring/TaskExtensions.cs` — WaitAsync timeout helpers
- `src/Monitoring/BatteryReaderOrchestrator.cs` — orchestrator stub
- `src/Monitoring/DeviceCapabilityCache.cs` — small success-only cache
- `src/Monitoring/PhysicalDeviceIdentityResolver.cs` — identity resolver helper
- `src/Monitoring/Gatt/GattConnectionManager.cs` — GATT manager stub

Next steps (in this branch):

- Implement `BluetoothDeviceExtensions` and `PhysicalDeviceIdentityResolver` tests.
- Implement `GattConnectionManager.TryReadBatteryAsync` using WinRT with `WaitAsync` timeouts.
- Implement `BatteryReaderOrchestrator` to call orchestrated readers with a global semaphore (1 concurrent op default).
- Add unit tests for `DeviceCapabilityCache` and `TaskExtensions`.

See the linked issues in the repo for acceptance criteria and ADR references.