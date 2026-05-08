namespace BTChargeTrayWatcher;

/// <summary>
/// Wraps the existing <see cref="NotificationService"/> as an
/// <see cref="INotificationChannel"/> so it can participate in
/// <see cref="NotificationDispatcher"/> fan-out without any changes
/// to its own implementation.
/// </summary>
public sealed class WindowsToastNotificationChannel : INotificationChannel
{
    private readonly NotificationService _inner;

    public WindowsToastNotificationChannel(NotificationService inner)
    {
        _inner = inner;
    }

    public void NotifyLow(string deviceName, int battery)
        => _inner.NotifyLow(deviceName, battery);

    public void NotifyHigh(string deviceName, int battery)
        => _inner.NotifyHigh(deviceName, battery);

    public void NotifyLaptopLow(int battery)
        => _inner.NotifyLaptopLow(battery);

    public void NotifyLaptopHigh(int battery)
        => _inner.NotifyLaptopHigh(battery);
}
