using System.Collections.Concurrent;

namespace BTChargeTrayWatcher;

public partial class BluetoothBatteryMonitor : IDisposable, IAsyncDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly NotificationService _notifier;
    private readonly GattBatteryReader _gattReader = new();
    private readonly ClassicBatteryReader _classicReader = new();
    private readonly ConcurrentDictionary<string, int> _lastKnown = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    private readonly object _taskGate = new();
    private readonly HashSet<Task> _activeTasks = [];

    private volatile bool _isScanning;
    private volatile bool _disposeStarted;
    private volatile bool _isDisposed;

    public event Action<string, int>? DeviceBatteryRead;
    public event Action<string, int>? DeviceFound;
    public event Action<IReadOnlyList<(string, int)>>? ScanCompleted;
    public event Action? ScanStarted;

    public bool IsScanning => _isScanning;

    public IReadOnlyList<(string Name, int Battery)> LastKnownDevices =>
        _lastKnown.Select(kv => (kv.Key, kv.Value)).ToList();

    public bool HasCachedResults => !_lastKnown.IsEmpty;

    private enum BatteryAlertState
    {
        Normal = 0,
        Low = 1,
        High = 2
    }

    private readonly ConcurrentDictionary<string, BatteryAlertState> _alertStates =
    new(StringComparer.OrdinalIgnoreCase);

    public BluetoothBatteryMonitor(ThresholdSettings settings, NotificationService notifier)
    {
        _settings = settings;
        _notifier = notifier;

        _timer = new System.Threading.Timer(
            _ => OnTimerTick(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(60));
    }

    private void OnTimerTick()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested)
            return;

        StartTrackedTask(ct => SafePollAsync(ct));
    }

    private void StartTrackedTask(Func<CancellationToken, Task> work)
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested)
            return;

        Task task;
        try
        {
            task = Task.Run(() => work(_shutdownCts.Token), _shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_taskGate)
        {
            if (_disposeStarted || _isDisposed)
                return;

            _activeTasks.Add(task);
        }

        _ = task.ContinueWith(t =>
        {
            lock (_taskGate)
            {
                _activeTasks.Remove(t);
            }

            if (t.IsFaulted && t.Exception is not null)
                System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] Background task fault: {t.Exception}");
        }, TaskScheduler.Default);
    }

    private async Task SafePollAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PollAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposeStarted || _isDisposed)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] PollAsync fault: {ex}");
        }
    }

    public Task<List<(string Name, int Battery)>> ScanNowAsync() =>
       ScanNowAsync(_shutdownCts.Token);

    public async Task<List<(string Name, int Battery)>> ScanNowAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        _isScanning = true;
        ScanStarted?.Invoke();

        List<(string, int)> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var (name, battery) in await _gattReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(name)) continue;
                DeviceFound?.Invoke(name, battery);
                results.Add((name, battery));
            }

            foreach (var (name, battery) in await _classicReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(name)) continue;
                DeviceFound?.Invoke(name, battery);
                results.Add((name, battery));
            }
        }
        finally
        {
            _isScanning = false;
            ScanCompleted?.Invoke(results);
        }

        return results;
    }

    public Task<List<(string Name, int Battery)>> StartTrackedScanAsync() =>
    StartTrackedScanAsync(_shutdownCts.Token);

    public Task<List<(string Name, int Battery)>> StartTrackedScanAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        CancellationToken token = linkedCts.Token;

        Task<List<(string Name, int Battery)>> task = ScanNowAsync(token);

        lock (_taskGate)
        {
            if (_disposeStarted || _isDisposed)
            {
                linkedCts.Dispose();
                throw new ObjectDisposedException(nameof(BluetoothBatteryMonitor));
            }

            _activeTasks.Add(task);
        }

        _ = task.ContinueWith(t =>
        {
            lock (_taskGate)
            {
                _activeTasks.Remove(t);
            }

            linkedCts.Dispose();

            if (t.IsFaulted && t.Exception is not null)
                System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] Scan task fault: {t.Exception}");
        }, TaskScheduler.Default);

        return task;
    }

    private async Task<List<(string Name, int Battery)>> QuietReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<(string, int)> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, battery) in await _gattReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(name)) continue;
            results.Add((name, battery));
        }

        foreach (var (name, battery) in await _classicReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(name)) continue;
            results.Add((name, battery));
        }

        return results;
    }

    public Task PollAsync() => PollAsync(_shutdownCts.Token);

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _pollLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = new Dictionary<string, int>(_lastKnown, StringComparer.OrdinalIgnoreCase);
            var devices = await QuietReadAsync(cancellationToken).ConfigureAwait(false);

            foreach (var (name, battery) in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (battery < 0) continue;

                snapshot.TryGetValue(name, out int prev);
                bool isNew = !snapshot.ContainsKey(name);

                _lastKnown[name] = battery;
                DeviceBatteryRead?.Invoke(name, battery);

                if (isNew)
                {
                    _alertStates[name] = ClassifyBatteryState(battery);
                    continue;
                }

                if (prev == battery)
                    continue;

                BatteryAlertState previousState = _alertStates.TryGetValue(name, out var existingState)
                    ? existingState
                    : ClassifyBatteryState(prev);

                BatteryAlertState currentState = ClassifyBatteryState(battery);

                if (previousState != currentState)
                {
                    if (currentState == BatteryAlertState.Low)
                        _notifier.NotifyLow(name, battery);
                    else if (currentState == BatteryAlertState.High)
                        _notifier.NotifyHigh(name, battery);
                }

                _alertStates[name] = currentState;

            }
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private BatteryAlertState ClassifyBatteryState(int battery)
    {
        if (battery < 0)
            return BatteryAlertState.Normal;

        if (battery <= _settings.Low)
            return BatteryAlertState.Low;

        if (battery >= _settings.High)
            return BatteryAlertState.High;

        return BatteryAlertState.Normal;
    }


    public static string BatteryBar(int pct)
    {
        int clamped = Math.Clamp(pct, 0, 100);
        int filled = (int)Math.Round(clamped / 10.0, MidpointRounding.AwayFromZero);
        return "[" + new string('\u2588', filled) + new string('\u2591', 10 - filled) + "]";
    }

    private void ThrowIfDisposingOrDisposed()
    {
        if (_disposeStarted || _isDisposed)
            throw new ObjectDisposedException(nameof(BluetoothBatteryMonitor));
    }

    private Task[] SnapshotActiveTasks()
    {
        lock (_taskGate)
        {
            return _activeTasks.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        if (_disposeStarted) return;

        _disposeStarted = true;

        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _shutdownCts.Cancel();

        Task[] tasks = SnapshotActiveTasks();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] Shutdown wait fault: {ex}");
            }
        }

        _timer.Dispose();
        _shutdownCts.Dispose();
        _pollLock.Dispose();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
