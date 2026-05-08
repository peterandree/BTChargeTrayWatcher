# ADR-008 — Options record for multi-parameter constructors

**Status:** Accepted  
**Date:** 2026-05-08

## Context

Several classes in this codebase require many collaborating dependencies at construction time. `PollingOrchestrator`, for example, needs eight distinct parameters: settings, notifier, shared state, task tracker, a read delegate, a shutdown token, and two event-callback delegates. A constructor with eight positional parameters is fragile: call sites are hard to read, parameter order is easy to confuse, and adding a ninth parameter requires updating every call site.

## Decision

When a constructor requires more than four parameters, group them into a `sealed record` named `<ClassName>Options` and accept that record as the single constructor parameter.

```csharp
internal sealed record PollingOrchestratorOptions(
    ThresholdSettings Settings,
    NotificationService Notifier,
    ConcurrentDictionary<string, DeviceBatteryInfo> LastKnown,
    TaskTracker Tracker,
    Func<CancellationToken, Task<List<DeviceBatteryInfo>>> ReadDevices,
    Action<string, int?> OnBatteryRead,
    Action<IReadOnlyList<DeviceBatteryInfo>> OnScanCompleted,
    CancellationToken ShutdownToken);
```

The record is defined in the same file as the class it configures.

## Rationale

- Named properties at the call site in `Program.cs` make it immediately clear what each argument is for.
- Adding a new dependency means adding one property to the record; `Program.cs` gets a compile error at the one construction site, nowhere else.
- Records are immutable by default, preventing accidental mutation of the configuration after construction.
- No DI container overhead; the pattern is pure C#.

## Consequences

- All construction of the class must go through `Program.cs` or a dedicated factory; there is no public multi-overload API.
- The options record is not an interface and cannot be mocked directly; pass a real (or test-double) instance.
- Apply consistently: `ScannerOptions` follows the same pattern. Any new class with more than four constructor parameters must use this pattern.
