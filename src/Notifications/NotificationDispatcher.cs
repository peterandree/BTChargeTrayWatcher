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
    /// Raised when the user clicks/activates a delivered notification.
    /// Subscribe from the call site (e.g. TrayApp) after construction
    /// by wiring up the concrete channel that supports activation.
    public event Action? OnNotificationClicked;

    public NotificationDispatcher(IReadOnlyList<INotificationChannel> channels)
    {
        _channels = channels;
    }

    /// <summary>
    /// Raises <see cref="OnNotificationClicked"/>. Call this from the
    /// concrete notification channel (e.g. ToastNotificationChannel) when
    /// the user activates a toast.
    /// </summary>
    public void RaiseNotificationClicked() => OnNotificationClicked?.Invoke();

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
