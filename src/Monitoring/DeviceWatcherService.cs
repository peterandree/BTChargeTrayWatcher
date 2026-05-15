using System.Diagnostics;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BTChargeTrayWatcher;

/// <summary>
/// Monitors paired Bluetooth devices via WinRT <see cref="DeviceWatcher"/>.
/// Uses two watchers (BLE paired + Classic BT paired) and serialises all
/// events through a <see cref="Channel{T}"/> to avoid async void.
/// The BLE watcher requests <c>System.Devices.Aep.IsConnected</c> so we can
/// skip sleeping peripherals without touching the radio (#78).
/// </summary>
internal sealed class DeviceWatcherService : IAsyncDisposable
{
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";

    private readonly Channel<DeviceWatcherEvent> _channel =
        Channel.CreateUnbounded<DeviceWatcherEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly Dictionary<string, WatchedDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processor;

    private DeviceWatcher? _bleWatcher;
    private DeviceWatcher? _classicWatcher;
    private volatile bool _disposed;

    /// <summary>Raised (on the channel processing thread) when devices are added, removed, or connection state changes.</summary>
    internal event Action? DevicesChanged;

    internal DeviceWatcherService()
    {
        _processor = ProcessEventsAsync(_cts.Token);
    }

    /// <summary>Returns a snapshot of all currently tracked devices.</summary>
    internal IReadOnlyList<WatchedDevice> CurrentDevices
    {
        get
        {
            lock (_lock) { return [.. _devices.Values]; }
        }
    }

    /// <summary>Starts the device watchers. Must be called once after construction.</summary>
    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Watcher 1: BLE paired devices — request IsConnected so we can skip sleeping peripherals.
        string bleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        _bleWatcher = DeviceInformation.CreateWatcher(
            bleSelector,
            [IsConnectedProperty],
            DeviceInformationKind.AssociationEndpoint);
        WireWatcher(_bleWatcher, isBle: true);
        _bleWatcher.Start();

        // Watcher 2: Classic Bluetooth paired devices — also request IsConnected.
        string classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        _classicWatcher = DeviceInformation.CreateWatcher(classicSelector, [IsConnectedProperty]);
        WireWatcher(_classicWatcher, isBle: false);
        _classicWatcher.Start();
    }

    /// <summary>Performs a full re-enumeration, replacing the tracked device list.</summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);

        var bleDevicesTask = DeviceInformation.FindAllAsync(
            bleSelector, [IsConnectedProperty], DeviceInformationKind.AssociationEndpoint).AsTask(ct);
        var classicDevicesTask = DeviceInformation.FindAllAsync(
            classicSelector, [IsConnectedProperty]).AsTask(ct);

        await Task.WhenAll(bleDevicesTask, classicDevicesTask).ConfigureAwait(false);

        Debug.WriteLine($"[DeviceWatcherService] Refresh: {bleDevicesTask.Result.Count} BLE, {classicDevicesTask.Result.Count} Classic devices");

        lock (_lock)
        {
            _devices.Clear();

            foreach (var d in bleDevicesTask.Result)
            {
                string name = !string.IsNullOrWhiteSpace(d.Name) ? d.Name : d.Id;
                bool connected = ExtractIsConnected(d.Properties);
                _devices[d.Id] = new WatchedDevice(d.Id, name, IsBle: true, IsConnected: connected);
                Debug.WriteLine($"[DeviceWatcherService]   BLE: '{name}' connected={connected} id={d.Id}");
            }

            foreach (var d in classicDevicesTask.Result)
            {
                string name = !string.IsNullOrWhiteSpace(d.Name) ? d.Name : d.Id;
                bool connected = ExtractIsConnected(d.Properties);
                _devices.TryAdd(d.Id, new WatchedDevice(d.Id, name, IsBle: false, IsConnected: connected));
                Debug.WriteLine($"[DeviceWatcherService]   Classic: '{name}' connected={connected} id={d.Id}");
            }
        }

        DevicesChanged?.Invoke();
    }

    private void WireWatcher(DeviceWatcher watcher, bool isBle)
    {
        watcher.Added += (_, d) =>
            _channel.Writer.TryWrite(new DeviceWatcherEvent.Added(
                d.Id, d.Name, isBle, ExtractIsConnected(d.Properties)));
        watcher.Removed += (_, u) =>
            _channel.Writer.TryWrite(new DeviceWatcherEvent.Removed(u.Id));
        watcher.Updated += (_, u) =>
            _channel.Writer.TryWrite(new DeviceWatcherEvent.Updated(
                u.Id, isBle, ExtractIsConnected(u.Properties)));
        watcher.EnumerationCompleted += (_, _) =>
            Debug.WriteLine($"[DeviceWatcherService] Enumeration completed (BLE={isBle})");
        watcher.Stopped += (_, _) =>
            Debug.WriteLine($"[DeviceWatcherService] Watcher stopped (BLE={isBle})");
    }

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    switch (evt)
                    {
                        case DeviceWatcherEvent.Added a:
                            string name = !string.IsNullOrWhiteSpace(a.Name) ? a.Name : a.DeviceId;
                            Debug.WriteLine(
                                $"[DeviceWatcherService] Added: '{name}' BLE={a.IsBle} connected={a.IsConnected} id={a.DeviceId}");
                            lock (_lock)
                            {
                                _devices[a.DeviceId] = new WatchedDevice(
                                    a.DeviceId, name, a.IsBle, a.IsConnected);
                            }
                            DevicesChanged?.Invoke();
                            break;

                        case DeviceWatcherEvent.Removed r:
                            bool removed;
                            lock (_lock) { removed = _devices.Remove(r.DeviceId); }
                            if (removed) DevicesChanged?.Invoke();
                            break;

                        case DeviceWatcherEvent.Updated u:
                            bool changed = false;
                            lock (_lock)
                            {
                                if (_devices.TryGetValue(u.DeviceId, out var existing))
                                {
                                    bool newConnected = u.IsConnected ?? existing.IsConnected;
                                    if (newConnected != existing.IsConnected)
                                    {
                                        _devices[u.DeviceId] = existing with { IsConnected = newConnected };
                                        changed = true;
                                        Debug.WriteLine(
                                            $"[DeviceWatcherService] '{existing.Name}' IsConnected: {existing.IsConnected} → {newConnected}");
                                    }
                                }
                            }
                            if (changed) DevicesChanged?.Invoke();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DeviceWatcherService] Event processing fault: {ex}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Extracts <c>System.Devices.Aep.IsConnected</c> from a property set.
    /// Returns <c>false</c> if the property is absent (safe default for BLE devices).
    /// </summary>
    private static bool ExtractIsConnected(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(IsConnectedProperty, out var value) && value is true;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _channel.Writer.TryComplete();

        try { _bleWatcher?.Stop(); } catch { /* DeviceWatcher.Stop may throw if not started */ }
        try { _classicWatcher?.Stop(); } catch { /* DeviceWatcher.Stop may throw if not started */ }

        try { await _processor.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _cts.Dispose();
    }

    // ── Event discriminated union ─────────────────────────────────────────────────

    private abstract record DeviceWatcherEvent
    {
        internal sealed record Added(string DeviceId, string Name, bool IsBle, bool IsConnected) : DeviceWatcherEvent;
        internal sealed record Removed(string DeviceId) : DeviceWatcherEvent;
        internal sealed record Updated(string DeviceId, bool IsBle, bool? IsConnected) : DeviceWatcherEvent;
    }
}
