using System.Diagnostics;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace BTChargeTrayWatcher;

public sealed class GattBatteryReader : IDisposable, IBatteryReader
{
    private static readonly Guid BatterySvcUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly TimeSpan PerDeviceTimeout = TimeSpan.FromSeconds(4);

    private readonly SemaphoreSlim _deviceReadGate = new(2, 2);
    private readonly GattConnectionCache _cache = new();
    private readonly GattBatteryProcessor _processor;

    public GattBatteryReader()
    {
        _processor = new GattBatteryProcessor(_cache);
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
            return [];
        }

        var currentDeviceIds = new HashSet<string>(dis.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var info in dis)
            currentDeviceIds.Add(info.Id);

        _cache.PruneStaleDevices(currentDeviceIds);

        var readTasks = new List<Task<(string Name, int Battery)>>(dis.Count);
        foreach (var info in dis)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readTasks.Add(ProcessDeviceBoundedAsync(info.Id, info.Name, cancellationToken));
        }

        var perDeviceResults = await Task.WhenAll(readTasks).ConfigureAwait(false);

        var results = new List<DeviceBatteryInfo>(dis.Count);
        foreach (var (name, battery) in perDeviceResults)
        {
            if (!string.IsNullOrWhiteSpace(name))
                results.Add(new DeviceBatteryInfo(name, battery));
        }

        return results;
    }

    private async Task<(string Name, int Battery)> ProcessDeviceBoundedAsync(
        string deviceId, string fallbackName, CancellationToken cancellationToken)
    {
        await _deviceReadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(PerDeviceTimeout);

            try
            {
                return await _processor.ProcessDeviceAsync(deviceId, fallbackName, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Debug.WriteLine($"[GattBatteryReader] Timeout while reading '{deviceId}'.");
                return (fallbackName, -1);
            }
            catch (Exception ex) when (GattBatteryProcessor.IsExpectedBluetoothException(ex))
            {
                Debug.WriteLine($"[GattBatteryReader] Device unavailable '{deviceId}': {ex.Message}");
                return (fallbackName, -1);
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
