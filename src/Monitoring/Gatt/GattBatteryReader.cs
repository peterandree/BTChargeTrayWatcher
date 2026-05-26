using System.Diagnostics;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace BTChargeTrayWatcher;

public sealed class GattBatteryReader : IDisposable
{
    private static readonly Guid BatterySvcUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly TimeSpan DefaultPerDeviceTimeout = TimeSpan.FromSeconds(4);

    private readonly TimeSpan _perDeviceTimeout;
    private readonly SemaphoreSlim _deviceReadGate = new(PollingDefaults.GattMaxConcurrentReads, PollingDefaults.GattMaxConcurrentReads);
    private readonly GattConnectionCache _cache = new();
    private readonly GattBatteryProcessor _processor;

    public GattBatteryReader() : this(null, null)
    {
    }

    internal GattBatteryReader(Func<string, string, CancellationToken, Task<GattDeviceReadResult>>? testProcessOverride, TimeSpan? perDeviceTimeout)
    {
        _perDeviceTimeout = perDeviceTimeout ?? DefaultPerDeviceTimeout;
        _processor = new GattBatteryProcessor(_cache, testProcessOverride);
    }

    public Task<List<DeviceBatteryInfo>> ReadAllAsync() =>
        ReadAllAsync(CancellationToken.None);

    public async Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DeviceInformationCollection dis;
        try
        {
            string selector = GattDeviceService.GetDeviceSelectorFromUuid(BatterySvcUuid);
            dis = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (GattBatteryProcessor.IsExpectedBluetoothException(ex))
        {
            Debug.WriteLine($"[GattBatteryReader] Radio unavailable: {ex.Message}");
            return new List<DeviceBatteryInfo>();
        }

        var deviceList = dis.Select(i => (Id: i.Id, Name: i.Name)).ToList();
        return await ReadAllAsync(deviceList, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<List<DeviceBatteryInfo>> ReadAllAsync(IEnumerable<(string Id, string Name)> deviceInfos, CancellationToken cancellationToken)
    {
        var list = deviceInfos.ToList();

        var currentDeviceIds = new HashSet<string>(list.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var info in list)
            currentDeviceIds.Add(info.Id);

        _cache.PruneStaleDevices(currentDeviceIds);

        var readTasks = new List<Task<GattDeviceReadResult>>(list.Count);
        foreach (var info in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readTasks.Add(ProcessDeviceBoundedAsync(info.Id, info.Name, cancellationToken));
        }

        var perDeviceResults = await Task.WhenAll(readTasks).ConfigureAwait(false);

        var results = new List<DeviceBatteryInfo>(list.Count);
        foreach (GattDeviceReadResult r in perDeviceResults)
        {
            if (!string.IsNullOrWhiteSpace(r.Name))
                results.Add(new DeviceBatteryInfo(r.DeviceId, r.Name, r.Battery, r.IsCharging, BatterySource.Gatt));
        }

        return results;
    }

    private async Task<GattDeviceReadResult> ProcessDeviceBoundedAsync(
        string deviceId, string fallbackName, CancellationToken cancellationToken)
    {
        await _deviceReadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_perDeviceTimeout);

            try
            {
                return await _processor.ProcessDeviceAsync(deviceId, fallbackName, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Debug.WriteLine($"[GattBatteryReader] Timeout while reading '{deviceId}'.");
                return new GattDeviceReadResult(deviceId, fallbackName, null);
            }
            catch (Exception ex) when (GattBatteryProcessor.IsExpectedBluetoothException(ex))
            {
                Debug.WriteLine($"[GattBatteryReader] Device unavailable '{deviceId}': {ex.Message}");
                return new GattDeviceReadResult(deviceId, fallbackName, null);
            }
        }
        finally
        {
            _deviceReadGate.Release();
        }
    }

    public void Dispose()
    {
        _cache.Dispose();
        _deviceReadGate.Dispose();
    }
}
