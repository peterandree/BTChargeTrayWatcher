using System.Runtime.InteropServices;

namespace BTChargeTrayWatcher;


public sealed class ClassicBatteryReader
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(3);

    private readonly IClassicBluetoothDeviceEnumerator _deviceEnumerator;
    private readonly IClassicBluetoothConnectionChecker _connectionChecker;
    private readonly IClassicBatteryPropertyReader _batteryPropertyReader;

    public ClassicBatteryReader()
        : this(new ClassicBluetoothDeviceEnumerator(), new ClassicBluetoothConnectionChecker(), new ClassicBatteryPropertyReader())
    {
    }

    // Internal constructor for test injection
    internal ClassicBatteryReader(
        IClassicBluetoothDeviceEnumerator deviceEnumerator,
        IClassicBluetoothConnectionChecker connectionChecker,
        IClassicBatteryPropertyReader batteryPropertyReader)
    {
        _deviceEnumerator = deviceEnumerator;
        _connectionChecker = connectionChecker;
        _batteryPropertyReader = batteryPropertyReader;
    }

    public Task<List<DeviceBatteryInfo>> ReadAllAsync() =>
        ReadAllAsync(skipConnectionCheck: false, CancellationToken.None);

    /// <summary>
    /// Reads battery levels from all paired Classic Bluetooth devices.
    /// </summary>
    /// <param name="skipConnectionCheck">
    /// When <c>true</c> (background poll), skips the per-device
    /// <c>BluetoothDevice.FromBluetoothAddressAsync</c> active connection check and
    /// accepts all enumerated candidates as connected. This avoids N parallel radio
    /// queries every 60 s — the passive <c>System.Devices.Aep.IsConnected</c> property
    /// from <see cref="DeviceWatcherService"/> provides the same information without
    /// waking peripherals. When <c>false</c> (manual deep scan), each candidate is
    /// actively checked via the WinRT connection API (ADR-019).
    /// </param>
    public Task<List<DeviceBatteryInfo>> ReadAllAsync(CancellationToken cancellationToken) =>
        ReadAllAsync(skipConnectionCheck: false, cancellationToken);

    public async Task<List<DeviceBatteryInfo>> ReadAllAsync(bool skipConnectionCheck, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<ClassicBluetoothCandidate> candidates;
        try
        {
            candidates = _deviceEnumerator.EnumerateCandidates();
        }
        catch (Exception ex) when (IsExpectedBluetoothException(ex))
        {
            System.Diagnostics.Debug.WriteLine($"[ClassicBatteryReader] Radio unavailable: {ex.Message}");
            return [];
        }

        if (candidates.Count == 0)
            return [];

        cancellationToken.ThrowIfCancellationRequested();

        List<ClassicBluetoothCandidate> connected;

        if (skipConnectionCheck)
        {
            // ADR-017: background polls use passive enumeration data only.
            // Accept all enumerated candidates — DeviceWatcherService already
            // tracks IsConnected via System.Devices.Aep for the same devices.
            connected = candidates;
        }
        else
        {
            // Manual deep scan: actively verify each candidate (ADR-019).
            Task<ConnectionCheckResult>[] connectionTasks = [.. candidates.Select(candidate => CheckConnectedAsync(candidate, cancellationToken))];

            ConnectionCheckResult[] connectionResults =
                await Task.WhenAll(connectionTasks).ConfigureAwait(false);

            connected = [.. connectionResults
                .Where(r => r.Connected)
                .Select(r => r.Candidate)];
        }

        if (connected.Count == 0)
            return [];

        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<string, (int Battery, bool? IsCharging)> batteryMap = await Task.Run(() =>
        {
            var instanceIds = connected.Select(c => c.InstanceId);
            return _batteryPropertyReader.ReadBatteryProperties(instanceIds);
        }, cancellationToken).ConfigureAwait(false);

        // Keep null-battery devices in results (same contract as the GATT path)
        // so the scan dialog and _lastKnown can display them.
        // Only drop out-of-range values (< 0 or > 100).
        return [.. connected
            .Select(c =>
            {
                bool found = batteryMap.TryGetValue(c.InstanceId, out var props);
                return new DeviceBatteryInfo(
                    c.InstanceId,
                    c.Name,
                    found ? props.Battery : null,
                    props.IsCharging,
                    BatterySource.Classic);
            })
            .Where(d => !string.IsNullOrWhiteSpace(d.Name) && (d.Battery is null or >= 0 and <= 100))];
    }

    private async Task<ConnectionCheckResult> CheckConnectedAsync(
        ClassicBluetoothCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectionTimeout);

            bool connected = await _connectionChecker
                .IsConnectedAsync(candidate.Address, timeoutCts.Token)
                .ConfigureAwait(false);

            return new ConnectionCheckResult(candidate, connected);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionCheckResult(candidate, false);
        }
        catch (Exception ex) when (IsExpectedBluetoothException(ex))
        {
            System.Diagnostics.Debug.WriteLine($"[ClassicBatteryReader] Connection check failed for '{candidate.Name}': {ex.Message}");
            return new ConnectionCheckResult(candidate, false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClassicBatteryReader] Unexpected connection check fault for '{candidate.Name}': {ex}");
            return new ConnectionCheckResult(candidate, false);
        }
    }

    private sealed record ConnectionCheckResult(ClassicBluetoothCandidate Candidate, bool Connected);

    private static bool IsExpectedBluetoothException(Exception ex)
    {
        return ex is COMException ||
               ex is UnauthorizedAccessException ||
               ex is InvalidOperationException;
    }
}
