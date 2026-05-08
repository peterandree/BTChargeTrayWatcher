# ADR-007 — TaskTracker for cooperative async shutdown

**Status:** Accepted  
**Date:** 2026-05-08

## Context

Background async tasks (poll loops, scan tasks) are started with fire-and-forget semantics (`TaskTracker.Start`). On application exit or `BluetoothBatteryMonitor.DisposeAsync`, these tasks must be awaited to completion to avoid orphaned handles, Bluetooth sessions left open, or exceptions on the finaliser thread.

A plain `List<Task>` would require locking around add/remove and would not handle tasks completing before the shutdown snapshot is taken.

## Decision

`TaskTracker` wraps a `ConcurrentBag<Task>` (effectively). `Start` accepts a factory `Func<CancellationToken, Task>`, issues the token, and registers the returned `Task`. `Stop` prevents new tasks from starting. `Snapshot()` returns the current live task list. `DisposeAsync` in `BluetoothBatteryMonitor` calls `_shutdownCts.Cancel()`, then `await Task.WhenAll(tracker.Snapshot())`.

## Rationale

- `CancellationToken` propagation ensures tasks exit promptly on shutdown without `Thread.Abort`.
- `Task.WhenAll(snapshot)` provides a clean drain: all in-flight work completes (or cancels) before resources are released.
- `TaskTracker.Stop` prevents races where a new task is started after the snapshot is taken but before disposal completes.
- `OperationCanceledException` during drain is swallowed; it is the expected outcome of cancellation.

## Consequences

- Shutdown latency is bounded by the longest individual task's cancellation response time. GATT per-device timeout is 4 s; Classic connection-check timeout is 3 s. Worst-case shutdown drain is therefore approximately 4 seconds if a scan is in progress at exit.
- Any task that does not respect cancellation will block shutdown indefinitely. All async paths in the codebase must forward the `CancellationToken`.
