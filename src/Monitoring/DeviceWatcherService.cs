using System.Diagnostics;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace BTChargeTrayWatcher;

/// <summary>
/// Monitors paired Bluetooth devices via WinRT <see cref="DeviceWatcher"/>.
/// Uses two watchers (BLE GATT Battery Service + Classic BT paired) and
/// serialises all events through a <see cref="Channel{T}"/> to avoid async void.
/// Fires <see cref="DevicesChanged"/> when the tracked device list changes.
/// </summary>
internal sealed class DeviceWatcherService : IAsyncDisposable
{
    private static readonly Guid BatterySvcUuid = new("0000180f-0000-1000-8000-00805f9b34fb");

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

    /// <summary>Raised (on the channel processing thread) when devices are added or removed.</summary>
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

        // Watcher 1: BLE devices exposing GATT Battery Service (0x180F)
        string bleSelector = GattDeviceService.GetDeviceSelectorFromUuid(BatterySvcUuid);
        _bleWatcher = DeviceInformation.CreateWatcher(bleSelector);
        WireWatcher(_bleWatcher, isBle: true);
        _bleWatcher.Start();

        // Watcher 2: Classic Bluetooth paired devices
        string classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        _classicWatcher = DeviceInformation.CreateWatcher(classicSelector);
        WireWatcher(_classicWatcher, isBle: false);
        _classicWatcher.Start();
    }

    /// <summary>Performs a full re-enumeration, replacing the tracked device list.</summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bleSelector = GattDeviceService.GetDeviceSelectorFromUuid(BatterySvcUuid);
        var classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);

        var bleDevicesTask = DeviceInformation.FindAllAsync(bleSelector).AsTask(ct);
        var classicDevicesTask = DeviceInformation.FindAllAsync(classicSelector).AsTask(ct);

        await Task.WhenAll(bleDevicesTask, classicDevicesTask).ConfigureAwait(false);

        lock (_lock)
        {
            _devices.Clear();

            foreach (var d in bleDevicesTask.Result)
            {
                string name = !string.IsNullOrWhiteSpace(d.Name) ? d.Name : d.Id;
                _devices[d.Id] = new WatchedDevice(d.Id, name, IsBle: true);
            }

            foreach (var d in classicDevicesTask.Result)
            {
                string name = !string.IsNullOrWhiteSpace(d.Name) ? d.Name : d.Id;
                _devices.TryAdd(d.Id, new WatchedDevice(d.Id, name, IsBle: false));
            }
        }

        DevicesChanged?.Invoke();
    }

    private void WireWatcher(DeviceWatcher watcher, bool isBle)
    {
        watcher.Added += (_, d) =>
            _channel.Writer.TryWrite(new DeviceWatcherEvent.Added(d.Id, d.Name, isBle));
        watcher.Removed += (_, u) =>
            _channel.Writer.TryWrite(new DeviceWatcherEvent.Removed(u.Id));
        watcher.Updated += (_, u) =>
            _channel.Writer.TryWrite(new DeviceWatcherEvent.Updated(u.Id));
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
                            lock (_lock) { _devices[a.DeviceId] = new WatchedDevice(a.DeviceId, name, a.IsBle); }
                            DevicesChanged?.Invoke();
                            break;

                        case DeviceWatcherEvent.Removed r:
                            bool removed;
                            lock (_lock) { removed = _devices.Remove(r.DeviceId); }
                            if (removed) DevicesChanged?.Invoke();
                            break;

                        case DeviceWatcherEvent.Updated:
                            // Updated events don't change our tracked data; ignore.
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
        internal sealed record Added(string DeviceId, string Name, bool IsBle) : DeviceWatcherEvent;
        internal sealed record Removed(string DeviceId) : DeviceWatcherEvent;
        internal sealed record Updated(string DeviceId) : DeviceWatcherEvent;
    }
}
