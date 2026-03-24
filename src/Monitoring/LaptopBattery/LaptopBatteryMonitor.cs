using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BTChargeTrayWatcher;

public sealed class LaptopBatteryMonitor : IAsyncDisposable
{
    private enum AlertState { Normal = 0, Low = 1, High = 2 }

    private const int Hysteresis = 2;

    private readonly ILaptopBatteryReader _reader;
    private readonly ThresholdSettings? _settings;
    private readonly NotificationService? _notifier;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly object _taskGate = new();
    private readonly HashSet<Task> _activeTasks = [];

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
    public LaptopBatteryInfo? LastKnownBattery => _lastKnown;

    // Used by Program.cs — full integration with settings and notifications
    public LaptopBatteryMonitor(ThresholdSettings settings, NotificationService notifier)
        : this(new WindowsLaptopBatteryReader(), settings, notifier) { }

    // Used for testing — reader injectable, no notifications
    public LaptopBatteryMonitor(ILaptopBatteryReader reader)
        : this(reader, null, null) { }

    private LaptopBatteryMonitor(
        ILaptopBatteryReader reader,
        ThresholdSettings? settings,
        NotificationService? notifier)
    {
        _reader = reader;
        _settings = settings;
        _notifier = notifier;

        if (_settings is not null)
            _settings.Changed += OnSettingsChanged;

        _timer = new System.Threading.Timer(
            _ => OnTimerTick(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(60));

        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
    }

    private void OnTimerTick()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested) return;
        StartTrackedTask(ct => SafeRefreshAsync(ct));
    }

    private void OnSettingsChanged()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested) return;
        // Reset alert state so threshold changes re-evaluate immediately on next poll
        _alertState = AlertState.Normal;
        StartTrackedTask(ct => SafeRefreshAsync(ct));
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposeStarted || _isDisposed) return;

        if (e.Mode == PowerModes.Suspend)
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        else if (e.Mode == PowerModes.Resume)
        {
            _timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
            StartTrackedTask(ct => SafeRefreshAsync(ct));
        }
    }

    private void ThrowIfDisposingOrDisposed()
    {
        if (_disposeStarted || _isDisposed)
            throw new ObjectDisposedException(nameof(LaptopBatteryMonitor));
    }

    private void StartTrackedTask(Func<CancellationToken, Task> work)
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested) return;

        Task task;
        try
        {
            task = Task.Run(() => work(_shutdownCts.Token), _shutdownCts.Token);
        }
        catch (OperationCanceledException) { return; }

        lock (_taskGate)
        {
            if (_disposeStarted || _isDisposed) return;
            _activeTasks.Add(task);
        }

        _ = task.ContinueWith(t =>
        {
            lock (_taskGate) { _activeTasks.Remove(t); }
            if (t.IsFaulted && t.Exception is not null)
                System.Diagnostics.Debug.WriteLine(
                    $"[BTChargeTrayWatcher] Laptop battery task fault: {t.Exception}");
        }, TaskScheduler.Default);
    }

    private Task[] SnapshotActiveTasks()
    {
        lock (_taskGate) { return [.. _activeTasks]; }
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

            if (_settings is not null && _notifier is not null)
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
        int low = _settings!.LaptopLow;
        int high = _settings.LaptopHigh;

        AlertState previous = _alertState;
        AlertState current = ClassifyAlertState(pct, low, high, previous, info);

        bool newHasAlert = current != AlertState.Normal;
        bool alertChanged = newHasAlert != _hasAlert;

        if (previous != current)
        {
            if (current == AlertState.Low)
                _notifier!.NotifyLaptopLow(pct);
            else if (current == AlertState.High)
                _notifier!.NotifyLaptopHigh(pct);
        }

        _alertState = current;

        if (alertChanged)
        {
            _hasAlert = newHasAlert;
            AlertStateChanged?.Invoke(_hasAlert);
        }
    }

    private static AlertState ClassifyAlertState(
        int pct, int low, int high, AlertState previous, LaptopBatteryInfo info)
    {
        // Low: only fire when not on AC power (plugging in silences the alert)
        if (!info.IsOnAcPower && pct <= low)
            return AlertState.Low;

        // High: only fire when actively charging (prompt to unplug for battery health)
        if (info.IsCharging && pct >= high)
            return AlertState.High;

        // Hysteresis — stay in current state near the boundary to prevent rapid toggling
        if (previous == AlertState.Low && !info.IsOnAcPower && pct <= low + Hysteresis)
            return AlertState.Low;

        if (previous == AlertState.High && info.IsCharging && pct >= high - Hysteresis)
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

        if (_settings is not null)
            _settings.Changed -= OnSettingsChanged;

        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;

        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _shutdownCts.Cancel();

        Task[] tasks = SnapshotActiveTasks();
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
