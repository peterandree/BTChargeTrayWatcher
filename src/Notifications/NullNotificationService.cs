namespace BTChargeTrayWatcher;

/// <summary>
/// No-op implementation of <see cref="INotificationService"/>.
/// Used wherever notifications must be structurally present but should never fire
/// (e.g. unit tests, or injection sites that have no notification channel).
/// </summary>
internal sealed class NullNotificationService : INotificationService
{
    public static readonly NullNotificationService Instance = new();

    private NullNotificationService() { }

    public void NotifyLow(string deviceName, int battery) { }
    public void NotifyHigh(string deviceName, int battery) { }
    public void NotifyLaptopLow(int battery) { }
    public void NotifyLaptopHigh(int battery) { }

    /// <inheritdoc/>
    /// Never raised — null implementation has no notification delivery.
    public event Action? OnNotificationClicked { add { } remove { } }
}
