using BTChargeTrayWatcher.Monitoring.Logging;

namespace BTChargeTrayWatcher;

/// <summary>
/// Owns the bounded GATT Battery Level subscription set for one
/// <see cref="GattConnectionManager"/> (issues #160 and #161).
/// <para>
/// Responsibilities, in one place so the #78 failure mode ("a WinRT reference outlives its use and
/// keeps a peripheral awake") cannot reappear through a missed teardown path:
/// </para>
/// <list type="bullet">
///   <item>decide via <see cref="GattSubscriptionPolicy"/> whether a device may subscribe,</item>
///   <item>enforce <see cref="GattSubscriptionDefaults.MaxConcurrentSubscriptions"/>,</item>
///   <item>drop a subscription that never notifies within the settling window, permanently for the
///     session,</item>
///   <item>unsubscribe on disconnect, suspend, eviction and dispose,</item>
///   <item>log every subscribe / notify / drop decision through
///     <see cref="DiscoveryLogger"/> (ADR-018, codes 1010–1012).</item>
/// </list>
/// The WinRT side is behind <see cref="IGattNotificationSubscription"/>, so all of the above is
/// unit-testable without hardware.
/// </summary>
internal sealed class GattSubscriptionCoordinator : IDisposable
{
    private const string ReaderName = "GattSubscriptionCoordinator";

    private readonly IGattNotificationSubscription _subscription;
    private readonly int _maxConcurrentSubscriptions;
    private readonly TimeSpan _settlingWindow;
    private readonly Func<DateTime> _clock;
    private readonly Lock _lock = new();

    private readonly Dictionary<string, ActiveSubscription> _active =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Devices dropped by the settling rule. ADR-003/ADR-017 amendment: these are never
    /// re-subscribed in the same session, because the peripheral advertised a capability it does
    /// not honour.
    /// </summary>
    private readonly HashSet<string> _droppedForSession = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    private sealed class ActiveSubscription(DateTime subscribedAtUtc)
    {
        public DateTime SubscribedAtUtc { get; } = subscribedAtUtc;
        public bool HasNotified { get; set; }
        public int? LastNotificationBattery { get; set; }
    }

    internal GattSubscriptionCoordinator(
        IGattNotificationSubscription subscription,
        int maxConcurrentSubscriptions = GattSubscriptionDefaults.MaxConcurrentSubscriptions,
        TimeSpan? settlingWindow = null,
        Func<DateTime>? clock = null)
    {
        _subscription = subscription;
        _maxConcurrentSubscriptions = maxConcurrentSubscriptions;
        _settlingWindow = settlingWindow ?? GattSubscriptionDefaults.SettlingWindow;
        _clock = clock ?? (() => DateTime.UtcNow);

        _subscription.NotificationReceived += OnNotificationReceived;
        _subscription.SubscriptionLost += OnSubscriptionLost;
    }

    /// <summary>Number of live subscriptions — the value compared against the cap.</summary>
    internal int ActiveCount
    {
        get { lock (_lock) { return _active.Count; } }
    }

    /// <summary>Whether <paramref name="deviceId"/> currently holds a live subscription.</summary>
    internal bool IsSubscribed(string deviceId)
    {
        lock (_lock) { return _active.ContainsKey(deviceId); }
    }

    /// <summary>
    /// Whether <paramref name="deviceId"/> was dropped by the settling rule and must not be
    /// subscribed again until the app restarts.
    /// </summary>
    internal bool IsDroppedForSession(string deviceId)
    {
        lock (_lock) { return _droppedForSession.Contains(deviceId); }
    }

    /// <summary>
    /// The most recent battery value pushed by <paramref name="deviceId"/>, or <c>null</c> when no
    /// notification has arrived. Used as a fallback when a watchdog read returns nothing.
    /// </summary>
    internal int? TryGetLastNotificationBattery(string deviceId)
    {
        lock (_lock)
        {
            return _active.TryGetValue(deviceId, out var state)
                ? state.LastNotificationBattery
                : null;
        }
    }

    /// <summary>
    /// Attempts to subscribe <paramref name="deviceId"/>. Returns <c>true</c> only when a live
    /// subscription now exists. Returns <c>false</c> — without throwing — for a device that is not
    /// eligible, so the caller keeps using the uncached read path.
    /// </summary>
    internal async Task<bool> TrySubscribeAsync(
        string deviceId,
        string fallbackName,
        bool supportsNotify,
        bool isConnected,
        CancellationToken ct)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (_droppedForSession.Contains(deviceId)) return false;
            if (_active.ContainsKey(deviceId)) return true;
            if (!GattSubscriptionPolicy.ShouldSubscribe(
                    supportsNotify, isConnected, _active.Count, _maxConcurrentSubscriptions))
                return false;

            // Reserve the slot *before* awaiting the seam. Devices are read in parallel, so a
            // check-then-await would let three simultaneous attempts all pass a cap of two.
            _active[deviceId] = new ActiveSubscription(_clock());
        }

        bool subscribed;
        try
        {
            subscribed = await _subscription.SubscribeAsync(deviceId, fallbackName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReleaseReservation(deviceId);
            throw;
        }
        catch (Exception ex)
        {
            DebugLog($"Subscribe fault for '{deviceId}': {ex}");
            ReleaseReservation(deviceId);
            subscribed = false;
        }

        if (!subscribed)
        {
            // The slot reserved above must not stay occupied by a device that refused to
            // subscribe, or the cap would leak until the settling window expired.
            ReleaseReservation(deviceId);

            DiscoveryLogger.Log(
                reader:     ReaderName,
                operation:  "Subscribe",
                outcome:    "WARN",
                errorCode:  DiscoveryLogger.Codes.GattSubscribed,
                message:    "Subscription not established",
                deviceId:   deviceId,
                deviceName: fallbackName);
            return false;
        }

        bool disposeRace;
        lock (_lock)
        {
            disposeRace = _disposed;
        }

        if (disposeRace)
        {
            // Lost a race with dispose: release immediately rather than leaving a live
            // subscription on a shut-down manager.
            await UnsubscribeAndLogAsync(deviceId, null, GattSubscriptionDropReason.Disposed, ct)
                .ConfigureAwait(false);
            return false;
        }

        lock (_lock)
        {
            DiscoveryLogger.Log(
                reader:     ReaderName,
                operation:  "Subscribe",
                outcome:    "SUBSCRIBED",
                errorCode:  DiscoveryLogger.Codes.GattSubscribed,
                message:    $"active={_active.Count}, cap={_maxConcurrentSubscriptions}",
                deviceId:   deviceId,
                deviceName: fallbackName);
        }

        return true;
    }

    private void ReleaseReservation(string deviceId)
    {
        lock (_lock) { _active.Remove(deviceId); }
    }

    /// <summary>
    /// Drops any subscription that has produced no notification within the settling window.
    /// Lazily evaluated on the poll path so no additional timer is introduced (ADR-003).
    /// </summary>
    internal async Task PruneAsync(CancellationToken ct)
    {
        List<string> expired = [];
        DateTime now = _clock();

        lock (_lock)
        {
            if (_disposed) return;
            foreach (var kv in _active)
            {
                if (GattSubscriptionPolicy.HasFailedSettling(
                        kv.Value.HasNotified, kv.Value.SubscribedAtUtc, now, _settlingWindow))
                    expired.Add(kv.Key);
            }
        }

        foreach (string deviceId in expired)
            await DropAsync(deviceId, GattSubscriptionDropReason.SettlingTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unsubscribes <paramref name="deviceId"/> and releases the manager's bookkeeping.
    /// Idempotent: calling it for a device that is not subscribed is a no-op.
    /// </summary>
    internal Task DropAsync(string deviceId, GattSubscriptionDropReason reason, CancellationToken ct)
        => DropCoreAsync([deviceId], reason, ct);

    /// <summary>Unsubscribes every device (suspend, shutdown).</summary>
    internal Task DropAllAsync(GattSubscriptionDropReason reason, CancellationToken ct = default)
    {
        string[] deviceIds;
        lock (_lock)
        {
            if (_active.Count == 0) return Task.CompletedTask;
            deviceIds = [.. _active.Keys];
        }

        return DropCoreAsync(deviceIds, reason, ct);
    }

    /// <summary>
    /// Removes bookkeeping and unsubscribes. Removal happens under the lock *before* the async
    /// unsubscribe, so concurrent teardown triggers can never unsubscribe the same device twice.
    /// </summary>
    private async Task DropCoreAsync(
        IReadOnlyList<string> deviceIds,
        GattSubscriptionDropReason reason,
        CancellationToken ct)
    {
        List<(string DeviceId, int? Battery)> removed = [];

        lock (_lock)
        {
            foreach (string deviceId in deviceIds)
            {
                if (!_active.TryGetValue(deviceId, out var state)) continue;
                _active.Remove(deviceId);
                if (reason == GattSubscriptionDropReason.SettlingTimeout)
                    _droppedForSession.Add(deviceId);
                removed.Add((deviceId, state.LastNotificationBattery));
            }
        }

        foreach (var (deviceId, battery) in removed)
            await UnsubscribeAndLogAsync(deviceId, battery, reason, ct).ConfigureAwait(false);
    }

    private async Task UnsubscribeAndLogAsync(
        string deviceId,
        int? lastBattery,
        GattSubscriptionDropReason reason,
        CancellationToken ct)
    {
        try
        {
            await _subscription.UnsubscribeAsync(deviceId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown/suspend cancelled the round-trip. The implementation detaches its handlers
            // and drops its WinRT references before awaiting WinRT, so nothing outlives this call.
        }
        catch (Exception ex)
        {
            DebugLog($"Unsubscribe fault for '{deviceId}': {ex}");
        }

        DiscoveryLogger.Log(
            reader:     ReaderName,
            operation:  "Drop",
            outcome:    "WARN",
            errorCode:  DiscoveryLogger.Codes.GattSubscriptionDropped,
            message:    $"reason={reason}; lastBattery={(lastBattery?.ToString() ?? "none")}",
            deviceId:   deviceId);
    }

    private void OnNotificationReceived(object? sender, GattNotificationReceivedEventArgs e)
    {
        if (e.Battery < 0 || e.Battery > 100) return;

        lock (_lock)
        {
            if (!_active.TryGetValue(e.DeviceId, out var state)) return; // post-teardown: ignore
            state.HasNotified = true;
            state.LastNotificationBattery = e.Battery;

            DiscoveryLogger.Log(
                reader:     ReaderName,
                operation:  "Notify",
                outcome:    "NOTIFIED",
                errorCode:  DiscoveryLogger.Codes.GattNotificationReceived,
                message:    $"battery={e.Battery}",
                deviceId:   e.DeviceId);
        }
    }

    private void OnSubscriptionLost(object? sender, GattSubscriptionLostEventArgs e)
    {
        // The implementation already released its WinRT references, so only bookkeeping remains.
        lock (_lock)
        {
            if (!_active.Remove(e.DeviceId)) return;

            DiscoveryLogger.Log(
                reader:     ReaderName,
                operation:  "Drop",
                outcome:    "WARN",
                errorCode:  DiscoveryLogger.Codes.GattSubscriptionDropped,
                message:    $"reason={e.Reason}",
                deviceId:   e.DeviceId);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _active.Clear();
            _droppedForSession.Clear();
        }

        _subscription.NotificationReceived -= OnNotificationReceived;
        _subscription.SubscriptionLost -= OnSubscriptionLost;
    }

    private static void DebugLog(string message) =>
        System.Diagnostics.Debug.WriteLine($"[GattSubscriptionCoordinator] {message}");
}
