using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BTChargeTrayWatcher;

public partial class BluetoothBatteryMonitor : IDisposable, IAsyncDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly NotificationService _notifier;
    private readonly IBatteryReader _gattReader;
    private readonly IBatteryReader _classicReader;
    private readonly ConcurrentDictionary<string, int> _lastKnown = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    private readonly object _taskGate = new();
    private readonly HashSet<Task> _activeTasks = [];

    private volatile bool _isScanning;
    private volatile bool _disposeStarted;
    private volatile bool _isDisposed;
    private volatile bool _thresholdsChanged;

    public event Action<string, int>? DeviceBatteryRead;
    public event Action<string, int>? DeviceFound;
    public event Action<IReadOnlyList<(string, int)>>? ScanCompleted;
    public event Action? ScanStarted;

    public bool IsScanning => _isScanning;

    public IReadOnlyList<(string Name, int Battery)> LastKnownDevices =>
        _lastKnown.Select(kv => (kv.Key, kv.Value)).ToList();

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

        // Any setting change flags a wipe of alert memory
        _thresholdsChanged = true;
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
        if (_disposeStarted) return;

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

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        if (_disposeStarted) return;

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
                Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
            }
            catch (AggregateException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] Shutdown wait fault: {ex}");
            }
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

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
