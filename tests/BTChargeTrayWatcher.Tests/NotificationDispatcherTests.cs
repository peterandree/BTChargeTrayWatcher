using Xunit;

namespace BTChargeTrayWatcher.Tests;

public sealed class NotificationDispatcherTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private sealed class RecordingChannel : INotificationChannel
    {
        public List<string> Calls { get; } = [];

        public void NotifyLow(string deviceName, int battery)
            => Calls.Add($"Low:{deviceName}:{battery}");

        public void NotifyHigh(string deviceName, int battery)
            => Calls.Add($"High:{deviceName}:{battery}");

        public void NotifyLaptopLow(int battery)
            => Calls.Add($"LaptopLow:{battery}");

        public void NotifyLaptopHigh(int battery)
            => Calls.Add($"LaptopHigh:{battery}");
    }

    private sealed class ThrowingChannel : INotificationChannel
    {
        public void NotifyLow(string deviceName, int battery)   => throw new InvalidOperationException("channel fault");
        public void NotifyHigh(string deviceName, int battery)  => throw new InvalidOperationException("channel fault");
        public void NotifyLaptopLow(int battery)                => throw new InvalidOperationException("channel fault");
        public void NotifyLaptopHigh(int battery)               => throw new InvalidOperationException("channel fault");
    }

    // ── Fan-out ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NotifyLow_reaches_all_channels()
    {
        var a = new RecordingChannel();
        var b = new RecordingChannel();
        var dispatcher = new NotificationDispatcher([a, b]);

        dispatcher.NotifyLow("Headphones", 15);

        Assert.Contains("Low:Headphones:15", a.Calls);
        Assert.Contains("Low:Headphones:15", b.Calls);
    }

    [Fact]
    public void NotifyHigh_reaches_all_channels()
    {
        var a = new RecordingChannel();
        var b = new RecordingChannel();
        var dispatcher = new NotificationDispatcher([a, b]);

        dispatcher.NotifyHigh("Keyboard", 85);

        Assert.Contains("High:Keyboard:85", a.Calls);
        Assert.Contains("High:Keyboard:85", b.Calls);
    }

    [Fact]
    public void NotifyLaptopLow_reaches_all_channels()
    {
        var a = new RecordingChannel();
        var b = new RecordingChannel();
        var dispatcher = new NotificationDispatcher([a, b]);

        dispatcher.NotifyLaptopLow(10);

        Assert.Contains("LaptopLow:10", a.Calls);
        Assert.Contains("LaptopLow:10", b.Calls);
    }

    [Fact]
    public void NotifyLaptopHigh_reaches_all_channels()
    {
        var a = new RecordingChannel();
        var b = new RecordingChannel();
        var dispatcher = new NotificationDispatcher([a, b]);

        dispatcher.NotifyLaptopHigh(95);

        Assert.Contains("LaptopHigh:95", a.Calls);
        Assert.Contains("LaptopHigh:95", b.Calls);
    }

    // ── Fault isolation ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Throwing_channel_does_not_prevent_subsequent_channels_from_receiving()
    {
        var good = new RecordingChannel();
        var dispatcher = new NotificationDispatcher([new ThrowingChannel(), good]);

        // must not throw, and good channel must still receive
        dispatcher.NotifyLow("Mouse", 12);

        Assert.Contains("Low:Mouse:12", good.Calls);
    }

    [Fact]
    public void Dispatcher_with_no_channels_does_not_throw()
    {
        var dispatcher = new NotificationDispatcher([]);
        dispatcher.NotifyLow("Device", 5);   // no exception
        dispatcher.NotifyHigh("Device", 95);
        dispatcher.NotifyLaptopLow(8);
        dispatcher.NotifyLaptopHigh(98);
    }
}
