using System.Collections.Generic;
using System.Diagnostics;

namespace BTChargeTrayWatcher;

/// <summary>
/// Fans out every battery notification event to all registered
/// <see cref="INotificationChannel"/> implementations.
/// Implements <see cref="INotificationService"/> so existing consumers
/// (monitors, tests) remain unchanged.
/// </summary>
public sealed class NotificationDispatcher : INotificationService
{
    private readonly IReadOnlyList<INotificationChannel> _channels;

    /// <inheritdoc/>
    /// Aggregated from all channels that expose a compatible clicked event.
    /// Channels that do not support activation never raise this event.
    public event Action? OnNotificationClicked;

    public NotificationDispatcher(IReadOnlyList<INotificationChannel> channels)
    {
        _channels = channels;

        foreach (var channel in _channels)
        {
            if (channel is INotificationClickSource clickSource)
                clickSource.NotificationClicked += () => OnNotificationClicked?.Invoke();
        }
    }

    public void NotifyLow(string deviceName, int battery)
        => Dispatch(c => c.NotifyLow(deviceName, battery));

    public void NotifyHigh(string deviceName, int battery)
        => Dispatch(c => c.NotifyHigh(deviceName, battery));

    public void NotifyLaptopLow(int battery)
        => Dispatch(c => c.NotifyLaptopLow(battery));

    public void NotifyLaptopHigh(int battery)
        => Dispatch(c => c.NotifyLaptopHigh(battery));

    private void Dispatch(Action<INotificationChannel> action)
    {
        foreach (var channel in _channels)
        {
            try
            {
                action(channel);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotificationDispatcher] Channel {channel.GetType().Name} faulted: {ex}");
            }
        }
    }
}
