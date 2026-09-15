namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Fake <see cref="IGattNotificationSubscription"/> for the #160/#161/#162 unit tests.
/// Records every subscribe attempt and unsubscribe so teardown behaviour can be asserted without
/// touching WinRT or Bluetooth hardware.
/// </summary>
internal sealed class FakeGattNotificationSubscription : IGattNotificationSubscription
{
    private readonly Lock _lock = new();

    /// <summary>Device ids the fake accepted as subscribed.</summary>
    public List<string> Subscribed { get; } = [];

    /// <summary>Every device id passed to <see cref="SubscribeAsync"/>, accepted or not.</summary>
    public List<string> SubscribeAttempts { get; } = [];

    /// <summary>Every device id passed to <see cref="UnsubscribeAsync"/>.</summary>
    public List<string> Unsubscribed { get; } = [];

    /// <summary>Result returned by the next <see cref="SubscribeAsync"/> call.</summary>
    public bool SubscribeResult { get; set; } = true;

    /// <summary>When set, <see cref="SubscribeAsync"/> throws instead of returning.</summary>
    public bool ThrowOnSubscribe { get; set; }

    /// <summary>Artificial latency, used to force overlapping subscribe attempts.</summary>
    public TimeSpan SubscribeDelay { get; set; } = TimeSpan.Zero;

    public event EventHandler<GattNotificationReceivedEventArgs>? NotificationReceived;

    public event EventHandler<GattSubscriptionLostEventArgs>? SubscriptionLost;

    public async Task<bool> SubscribeAsync(string deviceId, string fallbackName, CancellationToken ct)
    {
        lock (_lock) { SubscribeAttempts.Add(deviceId); }

        if (SubscribeDelay > TimeSpan.Zero)
            await Task.Delay(SubscribeDelay, ct).ConfigureAwait(false);

        if (ThrowOnSubscribe) throw new InvalidOperationException("simulated WinRT fault");

        lock (_lock)
        {
            if (SubscribeResult) Subscribed.Add(deviceId);
        }

        return SubscribeResult;
    }

    public Task UnsubscribeAsync(string deviceId, CancellationToken ct)
    {
        lock (_lock)
        {
            Unsubscribed.Add(deviceId);
            Subscribed.RemoveAll(id => string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase));
        }

        return Task.CompletedTask;
    }

    public bool IsSubscribed(string deviceId)
    {
        lock (_lock)
        {
            return Subscribed.Contains(deviceId, StringComparer.OrdinalIgnoreCase);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Simulates a pushed Battery Level value.</summary>
    public void RaiseNotification(string deviceId, int battery) =>
        NotificationReceived?.Invoke(this, new GattNotificationReceivedEventArgs(deviceId, battery));

    /// <summary>Simulates the peripheral dropping the link, which the seam detects itself.</summary>
    public void RaiseLost(string deviceId, GattSubscriptionDropReason reason) =>
        SubscriptionLost?.Invoke(this, new GattSubscriptionLostEventArgs(deviceId, reason));
}
