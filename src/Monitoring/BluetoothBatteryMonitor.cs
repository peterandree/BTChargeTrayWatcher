using System.Collections.Concurrent;
using Microsoft.Win32;
using System.Threading;

namespace BTChargeTrayWatcher;

public partial class BluetoothBatteryMonitor : IAsyncDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly NotificationService _notifier;
    private readonly IBatteryReader _gattReader;
    private readonly IBatteryReader _classicReader;

    // Keyed by stable DeviceId; value holds both display name and last battery level.
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    private readonly object _taskGate = new();
    private readonly HashSet<Task> _activeTasks = [];

    private volatile bool _isScanning;
    private volatile bool _disposeStarted;
    private volatile bool _isDisposed;
    private volatile int _thresholdsChanged;

    public event Action<string, int>? DeviceBatteryRead;
    public event Action<string, int>? DeviceFound;
    public event Action<IReadOnlyList<DeviceBatteryInfo>>? ScanCompleted;
    public event Action? ScanStarted;

    private readonly TaskCompletionSource _disposalComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsScanning => _isScanning;

    public IReadOnlyList<DeviceBatteryInfo> LastKnownDevices =>
        _lastKnown.Values.ToList();

    public bool HasCachedResults => !_lastKnown.IsEmpty;

    public BluetoothBatteryMonitor(ThresholdSettings settings, NotificationService notifier)
        : this(settings, notifier, new GattBatteryReader(), new ClassicBatteryReader())
    {
    }

    public BluetoothBatteryMonitor(
        ThresholdSettings settings,
        NotificationService notifier,
        IBatteryReader gattReader,
        IBatteryReader classicReader)
    {
        _settings = settings;
        _notifier = notifier;
        _gattReader = gattReader;
        _classicReader = classicReader;

        _timer = new System.Threading.Timer(
            _ => OnTimerTick(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(60));

        _settings.Changed += Settings_Changed;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposeStarted || _isDisposed) return;

        if (e.Mode == PowerModes.Suspend)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        }
    }

    private void Settings_Changed()
    {
        if (_disposeStarted || _isDisposed || _shutdownCts.IsCancellationRequested)
            return;

        Interlocked.Exchange(ref _thresholdsChanged, 1);
        StartTrackedTask(ct => SafePollAsync(ct));
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
        _shutdownCts.Cancel();

        Task[] tasks = SnapshotActiveTasks();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] Shutdown wait fault: {ex}");
            }
        }

        _timer.Dispose();
        _shutdownCts.Dispose();
        _pollLock.Dispose();
        _scanLock.Dispose();

        if (_gattReader is IDisposable gd) gd.Dispose();
        if (_classicReader is IDisposable cd) cd.Dispose();

        _isDisposed = true;
        GC.SuppressFinalize(this);
        _disposalComplete.TrySetResult();
    }
}
