using System.Collections.Concurrent;
using Microsoft.Win32;

namespace BTChargeTrayWatcher;

public sealed class BluetoothBatteryMonitor : IAsyncDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly IBatteryReader _gattReader;
    private readonly IBatteryReader _classicReader;

    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly System.Threading.Timer _timer;

    private readonly TaskTracker _taskTracker;
    private readonly PollingOrchestrator _poller;
    private readonly Scanner _scanner;

    private volatile bool _disposeStarted;
    private volatile bool _isDisposed;

    private readonly TaskCompletionSource _disposalComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<string, int?>? DeviceBatteryRead;
    public event Action<string, string, int?>? DeviceFound;
    public event Action<IReadOnlyList<DeviceBatteryInfo>>? ManualScanCompleted;
    public event Action<IReadOnlyList<DeviceBatteryInfo>>? BackgroundRefreshCompleted;
    public event Action? ScanStarted;

    public bool IsScanning => _scanner.IsScanning;

    public IReadOnlyList<DeviceBatteryInfo> LastKnownDevices =>
        [.. _lastKnown.Values];

    public bool HasCachedResults => !_lastKnown.IsEmpty;

    public BluetoothBatteryMonitor(ThresholdSettings settings, NotificationService notifier)
        : this(settings, notifier, new GattBatteryReader(), new ClassicBatteryReader()) { }

    public BluetoothBatteryMonitor(
        ThresholdSettings settings,
        NotificationService notifier,
        IBatteryReader gattReader,
        IBatteryReader classicReader)
    {
        _settings = settings;
        _gattReader = gattReader;
        _classicReader = classicReader;

        _taskTracker = new TaskTracker();

        _poller = new PollingOrchestrator(new PollingOrchestratorOptions(
            Settings: settings,
            Notifier: notifier,
            LastKnown: _lastKnown,
            Tracker: _taskTracker,
            ReadDevices: ct => _scanner!.QuietReadAsync(ct),
            ShutdownToken: _shutdownCts.Token,
            OnBatteryRead: (name, lvl) => DeviceBatteryRead?.Invoke(name, lvl),
            OnScanCompleted: list => BackgroundRefreshCompleted?.Invoke(list)));

        _scanner = new Scanner(new ScannerOptions(
            GattReader: gattReader,
            ClassicReader: classicReader,
            LastKnown: _lastKnown,
            Poller: _poller,
            Tracker: _taskTracker,
            ShutdownToken: _shutdownCts.Token,
            OnDeviceFound: (id, name, lvl) => DeviceFound?.Invoke(id, name, lvl),
            OnBatteryRead: (name, lvl) => DeviceBatteryRead?.Invoke(name, lvl),
            OnScanStarted: () => ScanStarted?.Invoke(),
            OnScanCompleted: list => ManualScanCompleted?.Invoke(list)));

        _timer = new System.Threading.Timer(
            _ => OnTimerTick(),
            null,
            PollingDefaults.StartupDelay,
            PollingDefaults.PollingInterval);

        _settings.Changed += Settings_Changed;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
    }

    public Task PollAsync() => StartTrackedPollAsync(_shutdownCts.Token);
    public Task PollAsync(CancellationToken ct) => StartTrackedPollAsync(ct);

    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync() =>
        _scanner.StartTrackedScanAsync(_shutdownCts.Token);
    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync(CancellationToken ct) =>
        _scanner.StartTrackedScanAsync(ct);

    private Task StartTrackedPollAsync(CancellationToken ct)
    {
        ThrowIfDisposingOrDisposed();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _taskTracker.Start(_ =>
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, ct);
            CancellationToken token = linkedCts.Token;

            return _poller.PollAsync(token).ContinueWith(t =>
            {
                linkedCts.Dispose();
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine(
                        $"[BTChargeTrayWatcher] PollAsync fault: {t.Exception}");
                if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
                else if (t.IsCanceled) tcs.TrySetCanceled(token);
                else tcs.TrySetResult();
            }, TaskScheduler.Default);
        }, _shutdownCts.Token);

        return tcs.Task;
    }

    private void OnTimerTick()
    {
        if (_disposeStarted || _isDisposed) return;
        try { _poller.OnTimerTick(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BTChargeTrayWatcher] Timer tick fault: {ex}");
        }
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposeStarted || _isDisposed) return;

        if (e.Mode == PowerModes.Suspend)
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        else if (e.Mode == PowerModes.Resume)
            _timer.Change(PollingDefaults.ResumeDelay, PollingDefaults.PollingInterval);
    }

    private void Settings_Changed()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested) return;
        _poller.SignalThresholdsChanged();
    }

    private void ThrowIfDisposingOrDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposeStarted || _isDisposed, this);
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

        _settings.Changed -= Settings_Changed;
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
                    $"[BTChargeTrayWatcher] Shutdown wait fault: {ex}");
            }
        }

        _timer.Dispose();
        _shutdownCts.Dispose();
        _poller.Dispose();
        _scanner.Dispose();

        if (_gattReader is IDisposable gd) gd.Dispose();
        if (_classicReader is IDisposable cd) cd.Dispose();

        _isDisposed = true;
        GC.SuppressFinalize(this);
        _disposalComplete.TrySetResult();
    }
}
