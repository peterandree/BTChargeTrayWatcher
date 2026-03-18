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

    private void ThrowIfDisposingOrDisposed()
    {
        if (_disposeStarted || _isDisposed)
            throw new ObjectDisposedException(nameof(BluetoothBatteryMonitor));
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
