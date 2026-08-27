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
    private const string ClassOfDeviceProperty = "System.Devices.Aep.Bluetooth.Cod.Major";

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
            [IsConnectedProperty, ClassOfDeviceProperty],
            DeviceInformationKind.AssociationEndpoint);
        WireWatcher(_bleWatcher, isBle: true);
        _bleWatcher.Start();

        // Watcher 2: Classic Bluetooth paired devices — also request IsConnected.
        string classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        _classicWatcher = DeviceInformation.CreateWatcher(classicSelector, [IsConnectedProperty, ClassOfDeviceProperty]);
        WireWatcher(_classicWatcher, isBle: false);
        _classicWatcher.Start();
    }

    /// <summary>
    /// Performs a full re-enumeration, replacing the tracked device list.
    /// The refresh is routed through the channel so all <c>_devices</c> mutations
    /// and <c>DevicesChanged</c> invocations are serialised on the single
    /// channel-processing thread, eliminating the race with live watcher events.
    /// WinRT <c>FindAllAsync</c> calls run on the caller's thread (no lock held).
    /// </summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Phase 1: WinRT enumeration on caller's thread (no lock).
        var bleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);

        var bleDevicesTask = DeviceInformation.FindAllAsync(
            bleSelector, [IsConnectedProperty, ClassOfDeviceProperty],
            DeviceInformationKind.AssociationEndpoint).AsTask(ct);
        var classicDevicesTask = DeviceInformation.FindAllAsync(
            classicSelector, [IsConnectedProperty, ClassOfDeviceProperty]).AsTask(ct);

        await Task.WhenAll(bleDevicesTask, classicDevicesTask).ConfigureAwait(false);

        Debug.WriteLine($"[DeviceWatcherService] Refresh: {bleDevicesTask.Result.Count} BLE, {classicDevicesTask.Result.Count} Classic devices");

        // Phase 2: Build snapshot on caller's thread.
        var snapshot = new Dictionary<string, WatchedDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in bleDevicesTask.Result)
        {
            string name = !string.IsNullOrWhiteSpace(d.Name) ? d.Name : d.Id;
            bool connected = ExtractIsConnected(d.Properties);
            uint? cod = ExtractClassOfDevice(d.Properties);
            snapshot[d.Id] = new WatchedDevice(d.Id, name, IsBle: true, IsConnected: connected, cod);
            Debug.WriteLine($"[DeviceWatcherService]   BLE: '{name}' connected={connected} id={d.Id}");
        }

        foreach (var d in classicDevicesTask.Result)
        {
            string name = !string.IsNullOrWhiteSpace(d.Name) ? d.Name : d.Id;
            bool connected = ExtractIsConnected(d.Properties);
            uint? cod = ExtractClassOfDevice(d.Properties);
            snapshot.TryAdd(d.Id, new WatchedDevice(d.Id, name, IsBle: false, IsConnected: connected, cod));
            Debug.WriteLine($"[DeviceWatcherService]   Classic: '{name}' connected={connected} id={d.Id}");
        }

        // Phase 3: Post through channel — the channel thread will replace _devices
        // and raise DevicesChanged, serialising with live watcher events.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new DeviceWatcherEvent.RefreshRequest(snapshot, tcs)))
        {
            tcs.SetCanceled();
            return;
        }

        await tcs.Task.ConfigureAwait(false);
    }

    private void WireWatcher(DeviceWatcher watcher, bool isBle)
    {
        watcher.Added += (_, d) =>
            _channel.Writer.TryWrite(new DeviceWatcherEvent.Added(
                d.Id, d.Name, isBle, ExtractIsConnected(d.Properties), ExtractClassOfDevice(d.Properties)));
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
                                    a.DeviceId, name, a.IsBle, a.IsConnected, a.ClassOfDevice);
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

                        case DeviceWatcherEvent.RefreshRequest refresh:
                            lock (_lock)
                            {
                                _devices.Clear();
                                foreach (var kv in refresh.Snapshot)
                                    _devices[kv.Key] = kv.Value;
                            }
                            DevicesChanged?.Invoke();
                            refresh.Tcs.TrySetResult();
                            Debug.WriteLine($"[DeviceWatcherService] Refresh applied: {_devices.Count} devices");
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

    /// <summary>
    /// Extracts the Bluetooth Class of Device from <c>System.Devices.Aep.Bluetooth.Cod.Major</c>.
    /// Returns <c>null</c> if the property is absent or not a numeric type.
    /// Handles both <c>uint</c> and <c>ushort</c> boxing (WinRT may return either).
    /// </summary>
    private static uint? ExtractClassOfDevice(IReadOnlyDictionary<string, object> properties)
    {
        if (!properties.TryGetValue(ClassOfDeviceProperty, out var value)) return null;
        return value switch
        {
            uint u  => u,
            int i   => i >= 0 ? (uint)i : null,
            ushort us => us,
            short s  => s >= 0 ? (uint)s : null,
            _ => null
        };
    }

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
        internal sealed record Added(string DeviceId, string Name, bool IsBle, bool IsConnected, uint? ClassOfDevice = null) : DeviceWatcherEvent;
        internal sealed record Removed(string DeviceId) : DeviceWatcherEvent;
        internal sealed record Updated(string DeviceId, bool IsBle, bool? IsConnected) : DeviceWatcherEvent;
        internal sealed record RefreshRequest(
            Dictionary<string, WatchedDevice> Snapshot,
            TaskCompletionSource Tcs) : DeviceWatcherEvent;
    }
}
