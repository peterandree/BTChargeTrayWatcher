using Microsoft.Win32;

namespace BTChargeTrayWatcher;

public sealed class LaptopBatteryMonitor : IAsyncDisposable
{
    internal enum AlertState { Normal = 0, Low = 1, High = 2 }

    /// <summary>
    /// Result of one threshold evaluation for the laptop battery: the new alert state plus
    /// which (if any) notification must fire. Extracted as a pure decision so the alert rules —
    /// including the <c>ExcludeLaptopFromMonitoring</c> short-circuit (#156) — are unit-testable
    /// without constructing the monitor (which owns a timer and system-event subscriptions).
    /// </summary>
    internal readonly record struct LaptopAlertDecision(
        AlertState State,
        bool NotifyLow,
        bool NotifyHigh);

    private readonly ILaptopBatteryReader _reader;
    private readonly ThresholdSettings _settings;
    private readonly INotificationService _notifier;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TaskTracker _taskTracker = new();

    private volatile bool _disposeStarted;
    private volatile bool _isDisposed;

    private LaptopBatteryInfo? _lastKnown;
    private AlertState _alertState = AlertState.Normal;
    private bool _hasAlert;

    private readonly TaskCompletionSource _disposalComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<LaptopBatteryInfo>? BatteryUpdated;
    public event Action<bool>? AlertStateChanged;

    public bool HasCachedResult => _lastKnown is not null;
    public bool IsInAlertState => _hasAlert;
    public LaptopBatteryInfo? LastKnownBattery => _lastKnown;

    /// <summary>
    /// Production constructor — uses the real Windows battery reader.
    /// </summary>
    public LaptopBatteryMonitor(ThresholdSettings settings, INotificationService notifier)
        : this(new WindowsLaptopBatteryReader(), settings, notifier) { }

    /// <summary>
    /// Full constructor — injectable reader for test and production use.
    /// All three parameters are non-nullable; no null-guards required in the body.
    /// </summary>
    public LaptopBatteryMonitor(
        ILaptopBatteryReader reader,
        ThresholdSettings settings,
        INotificationService notifier)
    {
        _reader   = reader;
        _settings = settings;
        _notifier = notifier;

        _settings.LaptopSettingsChanged += OnSettingsChanged;

        _timer = new System.Threading.Timer(
            _ => OnTimerTick(),
            null,
            PollingDefaults.StartupDelay,
            PollingDefaults.PollingInterval);

        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
    }

    private void OnTimerTick()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested) return;
        _taskTracker.Start(ct => SafeRefreshAsync(ct), _shutdownCts.Token);
    }

    private void OnSettingsChanged()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested) return;
        _alertState = AlertState.Normal;
        _taskTracker.Start(ct => SafeRefreshAsync(ct), _shutdownCts.Token);
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposeStarted || _isDisposed) return;

        if (e.Mode == PowerModes.Suspend)
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        else if (e.Mode == PowerModes.Resume)
        {
            _timer.Change(PollingDefaults.ResumeDelay, PollingDefaults.PollingInterval);
            _taskTracker.Start(ct => SafeRefreshAsync(ct), _shutdownCts.Token);
        }
    }

    private void ThrowIfDisposingOrDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposeStarted || _isDisposed, this);
    }

    private async Task SafeRefreshAsync(CancellationToken ct)
    {
        try { await RefreshAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposeStarted || _isDisposed) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BTChargeTrayWatcher] Laptop battery refresh fault: {ex}");
        }
    }

    public Task RefreshAsync() => RefreshAsync(_shutdownCts.Token);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            LaptopBatteryInfo info = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _lastKnown = info;
            BatteryUpdated?.Invoke(info);
            EvaluateThresholds(info);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void EvaluateThresholds(LaptopBatteryInfo info)
    {
        if (!info.HasBattery || info.BatteryPercent < 0) return;

        int pct = info.BatteryPercent;

        LaptopAlertDecision decision = Decide(
            pct,
            _settings.LaptopLow,
            _settings.LaptopHigh,
            _alertState,
            info,
            _settings.ExcludeLaptopFromMonitoring);

        bool newHasAlert = decision.State != AlertState.Normal;
        bool alertChanged = newHasAlert != _hasAlert;

        if (decision.NotifyLow)
            _notifier.NotifyLaptopLow(pct);
        else if (decision.NotifyHigh)
            _notifier.NotifyLaptopHigh(pct);

        _alertState = decision.State;

        if (alertChanged)
        {
            _hasAlert = newHasAlert;
            AlertStateChanged?.Invoke(_hasAlert);
        }
    }

    /// <summary>
    /// Pure threshold decision for one laptop battery reading.
    /// When <paramref name="excludeFromMonitoring"/> is true the laptop is treated as
    /// not monitored at all: state is reset to <see cref="AlertState.Normal"/> and no
    /// notification is ever produced (#156).
    /// </summary>
    internal static LaptopAlertDecision Decide(
        int pct,
        int low,
        int high,
        AlertState previous,
        LaptopBatteryInfo info,
        bool excludeFromMonitoring)
    {
        if (excludeFromMonitoring)
            return new LaptopAlertDecision(AlertState.Normal, false, false);

        AlertState current = ClassifyAlertState(pct, low, high, previous, info);

        return new LaptopAlertDecision(
            current,
            NotifyLow:  previous != current && current == AlertState.Low,
            NotifyHigh: previous != current && current == AlertState.High);
    }

    private static AlertState ClassifyAlertState(
        int pct, int low, int high, AlertState previous, LaptopBatteryInfo info)
    {
        if (!info.IsOnAcPower && pct <= low)
            return AlertState.Low;

        if (info.IsCharging && pct >= high)
            return AlertState.High;

        if (previous == AlertState.Low && !info.IsOnAcPower && pct <= low + PollingDefaults.Hysteresis)
            return AlertState.Low;

        if (previous == AlertState.High && info.IsCharging && pct >= high - PollingDefaults.Hysteresis)
            return AlertState.High;

        return AlertState.Normal;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        if (_disposeStarted)
        {
            await _disposalComplete.Task.ConfigureAwait(false);
            return;
        }

        _disposeStarted = true;

        _settings.LaptopSettingsChanged -= OnSettingsChanged;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;

        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _taskTracker.Stop();
        _shutdownCts.Cancel();

        Task[] tasks = _taskTracker.Snapshot();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BTChargeTrayWatcher] Laptop battery shutdown fault: {ex}");
            }
        }

        _timer.Dispose();
        _shutdownCts.Dispose();
        _refreshLock.Dispose();

        if (_reader is IDisposable d) d.Dispose();

        _isDisposed = true;
        GC.SuppressFinalize(this);
        _disposalComplete.TrySetResult();
    }
}
