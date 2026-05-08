# ADR-007 — TaskTracker for cooperative async shutdown

**Status:** Accepted (amended 2026-05-08)  
**Date:** 2026-05-08

## Context

Background async tasks (poll loops, scan tasks) are started with fire-and-forget semantics (`TaskTracker.Start`). On application exit or `BluetoothBatteryMonitor.DisposeAsync`, these tasks must be awaited to completion to avoid orphaned handles, Bluetooth sessions left open, or exceptions on the finaliser thread.

A plain `List<Task>` would require locking around add/remove and would not handle tasks completing before the shutdown snapshot is taken.

## Decision

`TaskTracker` manages the lifecycle of background tasks. `Start` accepts a factory `Func<CancellationToken, Task>`, issues the token, and tracks the returned `Task`. `Stop` prevents new tasks from starting. `Snapshot()` returns the current live task set. `DisposeAsync` in `BluetoothBatteryMonitor` and `LaptopBatteryMonitor` calls `_shutdownCts.Cancel()`, then `await Task.WhenAll(tracker.Snapshot())`.

## Implementation constraint: add-before-schedule ordering

The task must be registered in the active set **before** it is scheduled for execution. The pattern:

```csharp
// WRONG — race window between Task.Run and _active.Add
task = Task.Run(...);
lock (_gate) { _active.Add(task); }  // task may already be complete and removed

// CORRECT — register first, schedule second
var tcs = new TaskCompletionSource(...);
lock (_gate) { _active.Add(tcs.Task); }
Task.Run(async () => { try { await work(ct); tcs.SetResult(); } catch ... });
```

Alternatively, use an `Interlocked` counter (increment on enter, decrement on exit) with a `TaskCompletionSource` drained to zero, which eliminates the `HashSet` and the lock entirely.

## Rationale

- `CancellationToken` propagation ensures tasks exit promptly on shutdown without `Thread.Abort`.
- `Task.WhenAll(snapshot)` provides a clean drain: all in-flight work completes (or cancels) before resources are released.
- `TaskTracker.Stop` prevents races where a new task is started after the snapshot is taken but before disposal completes.
- `OperationCanceledException` during drain is swallowed; it is the expected outcome of cancellation.

## Consequences

- Shutdown latency is bounded by the longest individual task's cancellation response time. GATT per-device timeout is 4 s; Classic connection-check timeout is 3 s. Worst-case shutdown drain is therefore approximately 4 seconds if a scan is in progress at exit.
- Any task that does not respect cancellation will block shutdown indefinitely. All async paths in the codebase must forward the `CancellationToken`.

## Resolution

The add-before-schedule race was fixed in `TaskTracker.Start` by registering a `TaskCompletionSource` placeholder in `_active` inside the lock **before** calling `Task.Run`. The running task mirrors its outcome into the TCS via `ContinueWith`, and the `finally` block removes `tcs.Task` from `_active` — so a task that completes before or concurrently with registration is always cleaned up correctly.

Verified by the `Start_race_condition_no_task_leaks_in_active_set` stress test (500 near-instant tasks) in `TaskTrackerTests.cs`. See issue **#43** — _TaskTracker.Start: fix add-before-schedule race condition_.
