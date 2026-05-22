using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

internal sealed class BluetoothBatteryMonitor : IAsyncDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly INotificationService _notifier;
    private readonly DeviceWatcherService _deviceWatcher;
    private readonly BatteryReaderOrchestrator _orchestrator;
    private readonly GattConnectionManager _gattConnectionManager;
    private readonly DeviceCapabilityCache _capabilityCache;
    private readonly AliasSuggestionService _aliasSuggestionService;
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _lastKnown = new();
    private readonly Scanner _scanner;
    private readonly PollingOrchestrator _poller;
    private readonly TaskTracker _tracker = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _disposed;

    public event Action<string, string, int?>? DeviceFound;
    public event Action<string, int?>? BatteryRead;
    public event Action? ScanStarted;
    public event Action<IReadOnlyList<DeviceBatteryInfo>>? BackgroundRefreshCompleted;
    public event Action<IReadOnlyList<DeviceBatteryInfo>>? ManualScanCompleted;
    public event Action<bool>? AlertStateChanged;

    public IReadOnlyList<DeviceBatteryInfo> LastKnownDevices => _lastKnown.Values.OrderBy(d => d.Name).ToList();

    internal BluetoothBatteryMonitor(
        ThresholdSettings settings,
        INotificationService notifier,
        BluetoothMonitoringInfrastructure infrastructure)
    {
        _settings = settings;
        _notifier = notifier;
        _deviceWatcher = infrastructure.DeviceWatcher;
        _orchestrator = infrastructure.Orchestrator;
        _gattConnectionManager = infrastructure.GattConnectionManager;
        _capabilityCache = infrastructure.CapabilityCache;
        _aliasSuggestionService = infrastructure.AliasSuggestionService;

        _poller = new PollingOrchestrator(new PollingOrchestratorOptions(
            Settings: _settings,
            Notifier: _notifier,
            LastKnown: _lastKnown,
            Tracker: _tracker,
            ReadDevices: ct => ReadDevicesCoreAsync(ct),
            Callbacks: new PollingOrchestratorCallbacks(
                OnBatteryRead: (name, battery) => BatteryRead?.Invoke(name, battery),
                OnScanCompleted: devices => BackgroundRefreshCompleted?.Invoke(devices),
                OnAlertStateChanged: hasAlert => AlertStateChanged?.Invoke(hasAlert)),
            ShutdownToken: _shutdownCts.Token));

        _scanner = new Scanner(new ScannerOptions(
            GattReader: new GattBatteryReader(_deviceWatcher, _orchestrator),
            ClassicReader: new ClassicBatteryReader(_deviceWatcher),
            LastKnown: _lastKnown,
            Poller: _poller,
            Tracker: _tracker,
            Callbacks: new ScannerCallbacks(
                OnDeviceFound: (deviceId, name, battery) => DeviceFound?.Invoke(deviceId, name, battery),
                OnBatteryRead: (name, battery) => BatteryRead?.Invoke(name, battery),
                OnScanStarted: () => ScanStarted?.Invoke(),
                OnScanCompleted: devices => ManualScanCompleted?.Invoke(devices)),
            ShutdownToken: _shutdownCts.Token));
    }

    public void StartBackgroundMonitoring()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _poller.StartBackgroundPolling();
    }

    public Task<List<DeviceBatteryInfo>> ScanNowAsync(CancellationToken ct) =>
        _scanner.ScanNowAsync(ct);

    internal async Task<List<DeviceBatteryInfo>> ReadDevicesCoreAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var results = await _scanner.QuietReadAsync(ct);

        foreach (var device in results)
        {
            if (device.Battery is not null)
                _lastKnown[device.DeviceId] = device;

            TryQueueAliasSuggestion(device);
        }

        return results;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _shutdownCts.Cancel();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BluetoothBatteryMonitor] Cancel fault: {ex}");
        }

        _scanner.Dispose();
        _poller.Dispose();
        _tracker.Dispose();

        try
        {
            _shutdownCts.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BluetoothBatteryMonitor] CTS dispose fault: {ex}");
        }

        try
        {
            await _gattConnectionManager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BluetoothBatteryMonitor] GATT dispose fault: {ex}");
        }
    }

    private void TryQueueAliasSuggestion(DeviceBatteryInfo device)
    {
        if (device.Battery is null) return;
        if (_settings.TryGetAlias(device.Name, out _)) return;
        if (_settings.IsAliasSuggestionSuppressed(device.DeviceId)) return;

        _ = _aliasSuggestionService.TrySuggestAsync(device.DeviceId, device.Name, _settings);
    }
}
