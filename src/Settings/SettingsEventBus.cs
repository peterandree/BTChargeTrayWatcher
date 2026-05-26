namespace BTChargeTrayWatcher;

/// <summary>
/// Owns the two public change-notification events that were previously declared
/// directly on <see cref="ThresholdSettings"/>.
///
/// Motivation (closes #130, closes #134):
/// <list type="bullet">
///   <item>Eliminates the ADR-009/013 violation where <c>ThresholdSettings</c> mixed
///     domain model responsibilities with event-dispatch responsibilities.</item>
///   <item>Makes the raise-after-lock pattern explicit and testable: callers capture
///     a <see cref="SettingsEventBus.PendingRaise"/> value inside the lock, then call
///     <see cref="Raise"/> after releasing it.  Re-entrant deadlocks from
///     subscribers calling back into settings are impossible because the lock is
///     never held during notification.</item>
/// </list>
/// </summary>
internal sealed class SettingsEventBus
{
    public event Action? Changed;
    public event Action? LaptopSettingsChanged;

    /// <summary>
    /// Flags passed back to the mutation site so it knows which events to raise
    /// after the lock is released.
    /// </summary>
    [Flags]
    internal enum PendingRaise
    {
        None                = 0,
        Changed             = 1,
        LaptopSettingsChanged = 2,
    }

    /// <summary>
    /// Raises the flagged events.  Must be called outside any settings lock.
    /// </summary>
    internal void Raise(PendingRaise pending)
    {
        if ((pending & PendingRaise.Changed) != 0)             Changed?.Invoke();
        if ((pending & PendingRaise.LaptopSettingsChanged) != 0) LaptopSettingsChanged?.Invoke();
    }
}
