namespace BTChargeTrayWatcher;

/// <summary>
/// Abstraction over toast/notification delivery.
/// Exists so that <see cref="LaptopBatteryMonitor"/> and other consumers
/// can be constructed without a real notification channel (tests, headless scenarios).
/// </summary>
public interface INotificationService
{
    void NotifyLow(string deviceName, int battery);
    void NotifyHigh(string deviceName, int battery);
    void NotifyLaptopLow(int battery);
    void NotifyLaptopHigh(int battery);

    /// <summary>
    /// Raised when the user clicks/activates a delivered notification.
    /// Implementations that do not support activation may leave this event unsubscribed.
    /// </summary>
    event Action? OnNotificationClicked;
}
