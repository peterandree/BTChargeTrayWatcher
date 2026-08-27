namespace BTChargeTrayWatcher;

/// <summary>
/// Deduplicates rapid-fire <see cref="AliasSuggestion"/> events from
/// <see cref="BatteryReaderOrchestrator"/> within a single poll cycle and
/// exposes a pending queue that the tray UI can consume.
/// </summary>
internal sealed class AliasSuggestionService
{
    private readonly HashSet<string> _pendingDeviceIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<AliasSuggestion> _pending = new();
    private readonly Lock _lock = new();

    /// <summary>Raised on the calling thread when a new (non-duplicate) suggestion is queued.</summary>
    internal event Action<AliasSuggestion>? SuggestionQueued;

    /// <summary>No-op — retained for API compatibility. Per-cycle dedup is replaced
    /// by cross-cycle <c>_pendingDeviceIds</c> tracking (#151).</summary>
    internal void BeginCycle() { }

    /// <summary>
    /// Enqueues a suggestion if this DeviceId does not already have a pending
    /// (unconsumed) suggestion. The <c>_pendingDeviceIds</c> set persists across
    /// poll cycles so the same device is never re-enqueued while its earlier
    /// suggestion is still in the queue. Thread-safe.
    /// </summary>
    internal void OnAliasSuggested(AliasSuggestion suggestion)
    {
        AliasSuggestion? toRaise = null;
        lock (_lock)
        {
            if (_pendingDeviceIds.Add(suggestion.DeviceId))
            {
                _pending.Enqueue(suggestion);
                toRaise = suggestion;
            }
        }
        if (toRaise is not null)
            SuggestionQueued?.Invoke(toRaise);
    }

    /// <summary>Dequeues the next pending suggestion, or returns null if none.</summary>
    internal AliasSuggestion? TryDequeue()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return null;
            var suggestion = _pending.Dequeue();
            _pendingDeviceIds.Remove(suggestion.DeviceId);
            return suggestion;
        }
    }

    internal bool HasPending { get { lock (_lock) return _pending.Count > 0; } }
}
