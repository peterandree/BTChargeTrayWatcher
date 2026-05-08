using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Exercises LaptopBatteryMonitor.ClassifyAlertState (private static) via the public
/// RefreshAsync entry point.  All hardware dependencies are replaced by StubLaptopBatteryReader.
/// </summary>
public sealed class LaptopBatteryMonitorClassifyTests : IAsyncDisposable
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class StubLaptopBatteryReader : ILaptopBatteryReader
    {
        public LaptopBatteryInfo Next { get; set; } =
            new LaptopBatteryInfo(HasBattery: true, BatteryPercent: 50,
                IsOnAcPower: false, IsCharging: false);

        public Task<LaptopBatteryInfo> ReadAsync(CancellationToken ct) =>
            Task.FromResult(Next);
    }

    private sealed class NotificationSpy : INotificationService
    {
        public int LaptopLowCount;  public int LastLaptopLowPct;
        public int LaptopHighCount; public int LastLaptopHighPct;
        public int LowCount;        public int HighCount;

        public void NotifyLow(string d, int b)       { LowCount++; }
        public void NotifyHigh(string d, int b)      { HighCount++; }
        public void NotifyLaptopLow(int b)           { LaptopLowCount++;  LastLaptopLowPct  = b; }
        public void NotifyLaptopHigh(int b)          { LaptopHighCount++; LastLaptopHighPct = b; }
        public void NotifyStatusReport(string body)  { }
    }

    private static (LaptopBatteryMonitor monitor, StubLaptopBatteryReader reader, NotificationSpy spy)
        Build()
    {
        var reader = new StubLaptopBatteryReader();
        var spy    = new NotificationSpy();
        var settings = new ThresholdSettings();   // low=20, high=80
        // Use the injectable-reader constructor (test path in LaptopBatteryMonitor).
        // We need settings+notifier: use the internal three-arg route via the two-arg
        // public constructor that accepts ILaptopBatteryReader + inject settings manually.
        // Since the test constructor deliberately omits settings, we instead call the
        // two-arg production constructor with a real ThresholdSettings and a real spy,
        // but swap out the reader by routing through the three-arg private constructor
        // via reflection — which is the right thing to do until #41 is resolved.
        var monitor = CreateWithReader(reader, settings, spy);
        return (monitor, reader, spy);
    }

    /// <summary>
    /// Calls the private canonical constructor via reflection so tests can supply all
    /// three dependencies without waiting for #41 to land.
    /// </summary>
    private static LaptopBatteryMonitor CreateWithReader(
        ILaptopBatteryReader reader,
        ThresholdSettings settings,
        INotificationService notifier)
    {
        var ctor = typeof(LaptopBatteryMonitor).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [typeof(ILaptopBatteryReader), typeof(ThresholdSettings), typeof(INotificationService)]);

        Assert.NotNull(ctor);
        return (LaptopBatteryMonitor)ctor!.Invoke([reader, settings, notifier]);
    }

    private static LaptopBatteryInfo Bat(int pct, bool onAc, bool charging) =>
        new(HasBattery: true, BatteryPercent: pct, IsOnAcPower: onAc, IsCharging: charging);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private readonly List<(LaptopBatteryMonitor m, NotificationSpy s)> _created = [];

    private (LaptopBatteryMonitor m, StubLaptopBatteryReader r, NotificationSpy s) Create()
    {
        var (m, r, s) = Build();
        _created.Add((m, s));
        return (m, r, s);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (m, _) in _created)
            await m.DisposeAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Low threshold
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Low_fires_when_discharging_at_threshold()
    {
        var (m, r, s) = Create();
        r.Next = Bat(20, onAc: false, charging: false);
        await m.RefreshAsync();
        Assert.Equal(1, s.LaptopLowCount);
    }

    [Fact]
    public async Task Low_fires_when_discharging_below_threshold()
    {
        var (m, r, s) = Create();
        r.Next = Bat(5, onAc: false, charging: false);
        await m.RefreshAsync();
        Assert.Equal(1, s.LaptopLowCount);
    }

    [Fact]
    public async Task Low_suppressed_when_on_ac_power()
    {
        var (m, r, s) = Create();
        r.Next = Bat(5, onAc: true, charging: false);
        await m.RefreshAsync();
        Assert.Equal(0, s.LaptopLowCount);
    }

    [Fact]
    public async Task Low_suppressed_when_charging()
    {
        var (m, r, s) = Create();
        r.Next = Bat(5, onAc: true, charging: true);
        await m.RefreshAsync();
        Assert.Equal(0, s.LaptopLowCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // High threshold
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task High_fires_when_charging_at_threshold()
    {
        var (m, r, s) = Create();
        r.Next = Bat(80, onAc: true, charging: true);
        await m.RefreshAsync();
        Assert.Equal(1, s.LaptopHighCount);
    }

    [Fact]
    public async Task High_fires_when_charging_above_threshold()
    {
        var (m, r, s) = Create();
        r.Next = Bat(95, onAc: true, charging: true);
        await m.RefreshAsync();
        Assert.Equal(1, s.LaptopHighCount);
    }

    [Fact]
    public async Task High_suppressed_when_not_charging_even_above_threshold()
    {
        var (m, r, s) = Create();
        r.Next = Bat(95, onAc: false, charging: false);
        await m.RefreshAsync();
        Assert.Equal(0, s.LaptopHighCount);
    }

    [Fact]
    public async Task High_suppressed_when_plugged_in_but_not_actively_charging()
    {
        var (m, r, s) = Create();
        r.Next = Bat(95, onAc: true, charging: false);
        await m.RefreshAsync();
        Assert.Equal(0, s.LaptopHighCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Hysteresis
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Hysteresis_Low_stays_low_within_band_while_discharging()
    {
        var (m, r, s) = Create();
        // First poll: enter Low
        r.Next = Bat(18, onAc: false, charging: false);
        await m.RefreshAsync();
        Assert.Equal(1, s.LaptopLowCount);

        // Second poll: still within hysteresis band (20 + 5 = 25)
        r.Next = Bat(22, onAc: false, charging: false);
        await m.RefreshAsync();
        // Should NOT fire again — still Low, no state transition
        Assert.Equal(1, s.LaptopLowCount);
        Assert.True(m.IsInAlertState);
    }

    [Fact]
    public async Task Hysteresis_Low_exits_when_above_band()
    {
        var (m, r, s) = Create();
        r.Next = Bat(18, onAc: false, charging: false);
        await m.RefreshAsync();

        r.Next = Bat(30, onAc: false, charging: false);   // above 20+5
        await m.RefreshAsync();
        Assert.False(m.IsInAlertState);
    }

    [Fact]
    public async Task Hysteresis_Low_exits_immediately_when_ac_plugged_in()
    {
        var (m, r, s) = Create();
        r.Next = Bat(18, onAc: false, charging: false);
        await m.RefreshAsync();

        // Same pct, but now on AC
        r.Next = Bat(18, onAc: true, charging: false);
        await m.RefreshAsync();
        Assert.False(m.IsInAlertState);
    }

    [Fact]
    public async Task Hysteresis_High_stays_high_within_band_while_charging()
    {
        var (m, r, s) = Create();
        r.Next = Bat(85, onAc: true, charging: true);
        await m.RefreshAsync();
        Assert.Equal(1, s.LaptopHighCount);

        // Within hysteresis band (80 - 5 = 75)
        r.Next = Bat(78, onAc: true, charging: true);
        await m.RefreshAsync();
        Assert.Equal(1, s.LaptopHighCount);   // no new fire
        Assert.True(m.IsInAlertState);
    }

    [Fact]
    public async Task Hysteresis_High_exits_when_below_band()
    {
        var (m, r, s) = Create();
        r.Next = Bat(85, onAc: true, charging: true);
        await m.RefreshAsync();

        r.Next = Bat(70, onAc: true, charging: true);  // below 80-5
        await m.RefreshAsync();
        Assert.False(m.IsInAlertState);
    }

    [Fact]
    public async Task Hysteresis_High_exits_when_charging_stops()
    {
        var (m, r, s) = Create();
        r.Next = Bat(85, onAc: true, charging: true);
        await m.RefreshAsync();

        r.Next = Bat(84, onAc: true, charging: false);  // still in band but charging stopped
        await m.RefreshAsync();
        Assert.False(m.IsInAlertState);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Guards
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HasBattery_false_produces_no_alert()
    {
        var (m, r, s) = Create();
        r.Next = new LaptopBatteryInfo(HasBattery: false, BatteryPercent: 0,
            IsOnAcPower: false, IsCharging: false);
        await m.RefreshAsync();
        Assert.Equal(0, s.LaptopLowCount + s.LaptopHighCount);
        Assert.False(m.IsInAlertState);
    }

    [Fact]
    public async Task Negative_pct_produces_no_alert()
    {
        var (m, r, s) = Create();
        r.Next = Bat(-1, onAc: false, charging: false);
        await m.RefreshAsync();
        Assert.Equal(0, s.LaptopLowCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Events
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BatteryUpdated_fires_on_every_refresh()
    {
        var (m, r, _) = Create();
        int callCount = 0;
        m.BatteryUpdated += _ => callCount++;
        r.Next = Bat(50, onAc: false, charging: false);
        await m.RefreshAsync();
        await m.RefreshAsync();
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task AlertStateChanged_fires_when_hasAlert_changes()
    {
        var (m, r, _) = Create();
        var changes = new List<bool>();
        m.AlertStateChanged += v => changes.Add(v);

        r.Next = Bat(50, onAc: false, charging: false);  // Normal → no change
        await m.RefreshAsync();

        r.Next = Bat(10, onAc: false, charging: false);  // → Low  (change: true)
        await m.RefreshAsync();

        r.Next = Bat(10, onAc: false, charging: false);  // stays Low (no change)
        await m.RefreshAsync();

        r.Next = Bat(50, onAc: false, charging: false);  // → Normal (change: false)
        await m.RefreshAsync();

        Assert.Equal([true, false], changes);
    }

    [Fact]
    public async Task No_double_fire_on_consecutive_low_polls()
    {
        var (m, r, s) = Create();
        r.Next = Bat(10, onAc: false, charging: false);
        await m.RefreshAsync();
        await m.RefreshAsync();
        Assert.Equal(1, s.LaptopLowCount);
    }

    [Fact]
    public async Task No_double_fire_on_consecutive_high_polls()
    {
        var (m, r, s) = Create();
        r.Next = Bat(90, onAc: true, charging: true);
        await m.RefreshAsync();
        await m.RefreshAsync();
        Assert.Equal(1, s.LaptopHighCount);
    }
}
