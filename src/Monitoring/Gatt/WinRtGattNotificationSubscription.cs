using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace BTChargeTrayWatcher;

/// <summary>
/// Production <see cref="IGattNotificationSubscription"/>: writes the Client Characteristic
/// Configuration Descriptor of Battery Level (0x2A19) to <c>Notify</c> and surfaces pushed values.
/// <para>
/// This is the only place in the app that intentionally holds a <c>BluetoothLEDevice</c> and a
/// <c>GattCharacteristic</c> open — the bounded, revocable deviation approved for issue #158
/// (ADR-003 and ADR-017 amendments). Every teardown path detaches the handlers and drops those
/// references in the same call; nothing here is cached across calls except the live subscriptions
/// themselves.
/// </para>
/// </summary>
internal sealed class WinRtGattNotificationSubscription : IGattNotificationSubscription
{
    private static readonly TimeSpan WinRtTimeout = GattSubscriptionDefaults.WinRtTimeout;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, LiveSubscription> _live =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    /// <summary>Handles held so they can be detached again when the subscription is released.</summary>
    private sealed class LiveSubscription
    {
        public required BluetoothLEDevice Device { get; init; }
        public required GattCharacteristic Characteristic { get; init; }
        public required TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> ValueChangedHandler { get; init; }
        public required TypedEventHandler<BluetoothLEDevice, object> ConnectionStatusHandler { get; init; }
    }

    /// <inheritdoc/>
    public event EventHandler<GattNotificationReceivedEventArgs>? NotificationReceived;

    /// <inheritdoc/>
    public event EventHandler<GattSubscriptionLostEventArgs>? SubscriptionLost;

    /// <inheritdoc/>
    public bool IsSubscribed(string deviceId)
    {
        lock (_lock) { return _live.ContainsKey(deviceId); }
    }

    /// <inheritdoc/>
    public async Task<bool> SubscribeAsync(string deviceId, string fallbackName, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            // Idempotent: a repeated subscribe attempt for a device that is already subscribed
            // must not be reported as a failure.
            if (_live.ContainsKey(deviceId)) return true;
        }

        BluetoothLEDevice? bleDevice = null;
        GattCharacteristic? characteristic = null;
        bool descriptorWritten = false;

        try
        {
            bleDevice = await BluetoothLEDevice.FromIdAsync(deviceId)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (bleDevice is null) return false;

            // Never force a connection (ADR-017): a sleeping peripheral is left asleep.
            if (bleDevice.ConnectionStatus != BluetoothConnectionStatus.Connected) return false;

            characteristic = await GattBatteryCharacteristicLocator
                .FindBatteryLevelAsync(bleDevice, ct)
                .ConfigureAwait(false);

            if (characteristic is null) return false;

            if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Notify) == 0)
                return false;

            var descriptorResult = await characteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);

            if (descriptorResult != GattCommunicationStatus.Success) return false;

            // From here on the descriptor is written: every remaining failure path must write it
            // back off, or the peripheral would keep notifying into a subscription we no longer
            // track (the #78 class of leak).
            descriptorWritten = true;

            var live = new LiveSubscription
            {
                Device = bleDevice,
                Characteristic = characteristic,
                ValueChangedHandler = (_, args) => OnValueChanged(deviceId, args),
                ConnectionStatusHandler = (sender, _) => OnConnectionStatusChanged(sender),
            };

            // Attach defensively: if the second subscription fails, the first handler must not
            // survive on a characteristic this manager no longer tracks.
            bool valueChangedAttached = false;
            try
            {
                characteristic.ValueChanged += live.ValueChangedHandler;
                valueChangedAttached = true;
                bleDevice.ConnectionStatusChanged += live.ConnectionStatusHandler;
            }
            catch (Exception)
            {
                if (valueChangedAttached)
                    characteristic.ValueChanged -= live.ValueChangedHandler;
                throw;
            }

            bool disposeRace;
            lock (_lock)
            {
                disposeRace = _disposed;
                if (!disposeRace) _live[deviceId] = live;
            }

            if (disposeRace)
            {
                Detach(live, deviceId);
                await WriteDescriptorOffAsync(characteristic, deviceId).ConfigureAwait(false);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // The token is already gone; release the descriptor without it rather than leaking it.
            if (descriptorWritten) await WriteDescriptorOffAsync(characteristic, deviceId).ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            Debug.WriteLine($"[WinRtGattNotificationSubscription] Timeout subscribing '{deviceId}'");
            if (descriptorWritten) await WriteDescriptorOffAsync(characteristic, deviceId).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (
            ex is COMException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            Debug.WriteLine($"[WinRtGattNotificationSubscription] Subscribe failed '{deviceId}': {ex.Message}");
            if (descriptorWritten) await WriteDescriptorOffAsync(characteristic, deviceId).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Best-effort CCCD write-off used on subscribe failure paths, with an independent token: the
    /// caller's token may already be cancelled, and leaving the descriptor set would keep the
    /// peripheral notifying into a subscription this app no longer tracks.
    /// </summary>
    private static async Task WriteDescriptorOffAsync(GattCharacteristic? characteristic, string deviceId)
    {
        if (characteristic is null) return;

        try
        {
            await characteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask(CancellationToken.None)
                .WaitAsync(WinRtTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[WinRtGattNotificationSubscription] Cleanup write-off failed '{deviceId}': {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task UnsubscribeAsync(string deviceId, CancellationToken ct)
    {
        LiveSubscription? live;
        lock (_lock)
        {
            if (!_live.Remove(deviceId, out live)) return;
        }

        // Handlers come off first: whatever happens with the descriptor write below, the peripheral
        // can no longer push into this app, and no event can re-enter with a half-released state.
        Detach(live, deviceId);

        // Best-effort CCCD write-off. WinRT references are already dropped above, so a failure or
        // an unplugged device cannot leak a session — it only leaves the peripheral's CCCD set
        // until the link drops, which is what #161 accepts as the reconnect behaviour.
        try
        {
            if (live.Device.ConnectionStatus != BluetoothConnectionStatus.Connected) return;

            await live.Characteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask(ct)
                .WaitAsync(WinRtTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Suspend/shutdown cancelled the descriptor write; references are already released.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinRtGattNotificationSubscription] CCCD write-off failed '{deviceId}': {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        string[] deviceIds;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            deviceIds = [.. _live.Keys];
        }

        foreach (string deviceId in deviceIds)
            await UnsubscribeAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnValueChanged(string deviceId, GattValueChangedEventArgs args)
    {
        try
        {
            if (args.CharacteristicValue.Length == 0) return;

            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte value = reader.ReadByte();
            if (value > 100) return;

            NotificationReceived?.Invoke(this, new GattNotificationReceivedEventArgs(deviceId, value));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinRtGattNotificationSubscription] Notification fault '{deviceId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Self-teardown on disconnect. The peripheral is gone, so no descriptor write is attempted —
    /// detaching the handlers and dropping the references is what releases the radio session.
    /// </summary>
    private void OnConnectionStatusChanged(BluetoothLEDevice sender)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected) return;

        string? deviceId = sender.DeviceId;
        if (string.IsNullOrEmpty(deviceId)) return;

        LiveSubscription? live;
        lock (_lock)
        {
            if (!_live.Remove(deviceId, out live)) return;
        }

        Detach(live, deviceId);

        SubscriptionLost?.Invoke(
            this,
            new GattSubscriptionLostEventArgs(deviceId, GattSubscriptionDropReason.Disconnected));
    }

    private static void Detach(LiveSubscription live, string deviceId)
    {
        try
        {
            live.Characteristic.ValueChanged -= live.ValueChangedHandler;
            live.Device.ConnectionStatusChanged -= live.ConnectionStatusHandler;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinRtGattNotificationSubscription] Detach fault '{deviceId}': {ex.Message}");
        }
    }
}
