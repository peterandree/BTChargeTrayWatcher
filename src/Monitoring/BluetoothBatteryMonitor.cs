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

    public event Action<string, int>? DeviceBatteryRead;
    public event Action<string, int>? DeviceFound;
    public event Action<IReadOnlyList<DeviceBatteryInfo>>? ScanCompleted;
    public event Action? ScanStarted;

    public bool IsScanning => _scanner.IsScanning;

    public IReadOnlyList<DeviceBatteryInfo> LastKnownDevices =>
        _lastKnown.Values.ToList();

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

        _poller = new PollingOrchestrator(
            settings: settings,
            notifier: notifier,
            lastKnown: _lastKnown,
            tracker: _taskTracker,
            readDevices: ct => _scanner!.QuietReadAsync(ct),
            shutdownToken: _shutdownCts.Token,
            onBatteryRead: (name, lvl) => DeviceBatteryRead?.Invoke(name, lvl),
            onScanCompleted: list => ScanCompleted?.Invoke(list));

        _scanner = new Scanner(
            gattReader: gattReader,
            classicReader: classicReader,
            lastKnown: _lastKnown,
            poller: _poller,
            tracker: _taskTracker,
            shutdownToken: _shutdownCts.Token,
            onDeviceFound: (name, lvl) => DeviceFound?.Invoke(name, lvl),
            onBatteryRead: (name, lvl) => DeviceBatteryRead?.Invoke(name, lvl),
            onScanStarted: () => ScanStarted?.Invoke(),
            onScanCompleted: list => ScanCompleted?.Invoke(list));

        _timer = new System.Threading.Timer(
            _ => OnTimerTick(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(60));

        _settings.Changed += Settings_Changed;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
    }

    // Public API — all external-token paths are linked to shutdown and tracked
    public Task PollAsync() => StartTrackedPollAsync(_shutdownCts.Token);
    public Task PollAsync(CancellationToken ct) => StartTrackedPollAsync(ct);

    public Task<List<DeviceBatteryInfo>> ScanNowAsync() =>
        _scanner.StartTrackedScanAsync(_shutdownCts.Token);
    public Task<List<DeviceBatteryInfo>> ScanNowAsync(CancellationToken ct) =>
        _scanner.StartTrackedScanAsync(ct);

    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync() =>
        _scanner.StartTrackedScanAsync(_shutdownCts.Token);
    public Task<List<DeviceBatteryInfo>> StartTrackedScanAsync(CancellationToken ct) =>
        _scanner.StartTrackedScanAsync(ct);

    private Task StartTrackedPollAsync(CancellationToken ct)
    {
        ThrowIfDisposingOrDisposed();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, ct);
        CancellationToken token = linkedCts.Token;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _taskTracker.Start(_ =>
        {
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
        _poller.OnTimerTick();
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposeStarted || _isDisposed) return;

        if (e.Mode == PowerModes.Suspend)
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        else if (e.Mode == PowerModes.Resume)
            _timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
    }

    private void Settings_Changed()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested) return;
        _poller.SignalThresholdsChanged();
    }

    private void ThrowIfDisposingOrDisposed()
    {
        if (_disposeStarted || _isDisposed)
            throw new ObjectDisposedException(nameof(BluetoothBatteryMonitor));
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
