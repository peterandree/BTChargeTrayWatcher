namespace BTChargeTrayWatcher;

/// <summary>
/// Deduplicates rapid-fire <see cref="AliasSuggestion"/> events from
/// <see cref="BatteryReaderOrchestrator"/> within a single poll cycle and
/// exposes a pending queue that the tray UI can consume.
/// </summary>
internal sealed class AliasSuggestionService
{
    private readonly HashSet<string> _seenThisCycle =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<AliasSuggestion> _pending = new();
    private readonly object _lock = new();

    /// <summary>Raised on the calling thread when a new (non-duplicate) suggestion is queued.</summary>
    internal event Action<AliasSuggestion>? SuggestionQueued;

    /// <summary>
    /// Call at the start of every poll cycle to reset the per-cycle dedup set.
    /// </summary>
    internal void BeginCycle() { lock (_lock) _seenThisCycle.Clear(); }

    /// <summary>
    /// Enqueues a suggestion if this DeviceId has not already been seen in the
    /// current poll cycle. Thread-safe.
    /// </summary>
    internal void OnAliasSuggested(AliasSuggestion suggestion)
    {
        AliasSuggestion? toRaise = null;
        lock (_lock)
        {
            if (_seenThisCycle.Add(suggestion.DeviceId))
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
            return _pending.Count > 0 ? _pending.Dequeue() : null;
    }

    internal bool HasPending { get { lock (_lock) return _pending.Count > 0; } }
}
