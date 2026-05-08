namespace BTChargeTrayWatcher;

/// <summary>
/// A single delivery channel for battery notifications.
/// Channels are composed by <see cref="NotificationDispatcher"/>.
/// </summary>
public interface INotificationChannel
{
    void NotifyLow(string deviceName, int battery);
    void NotifyHigh(string deviceName, int battery);
    void NotifyLaptopLow(int battery);
    void NotifyLaptopHigh(int battery);
}
