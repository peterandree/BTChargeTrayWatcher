// src/Tray/DeepScanRunner.cs
namespace BTChargeTrayWatcher;

/// <summary>
/// Result of one deep-scan run (ADR-019 §3). <paramref name="Error"/> carries an unexpected failure
/// back to the caller so it can be logged where the app already logs scan faults — the runner never
/// swallows it.
/// </summary>
internal readonly record struct DeepScanResult(
    DeepScanOutcome Outcome,
    int DevicesFound,
    int DevicesWithBattery,
    Exception? Error = null);

/// <summary>
/// Owns a single user-initiated diagnostic deep scan and the operational rules around it
/// (ADR-019 §2): one run per invocation, a global time budget, cooperative cancellation, and the
/// outcome classification. The radio work is injected as a delegate so those rules are testable
/// without a Bluetooth radio; the delegate must honour the token it is handed.
/// <para>
/// The runner deliberately holds no polling state: a deep scan is diagnostic and never changes the
/// background cadence (ADR-019 §4). Everything a finished run leaves behind is its result.
/// </para>
/// </summary>
internal sealed class DeepScanRunner
{
    private readonly Func<CancellationToken, Task<List<DeviceBatteryInfo>>> _runScan;
    private readonly TimeSpan _budget;

    private CancellationTokenSource? _cts;
    private bool _cancelledByUser;

    internal DeepScanRunner(
        Func<CancellationToken, Task<List<DeviceBatteryInfo>>> runScan,
        TimeSpan budget)
    {
        _runScan = runScan;
        _budget = budget;
    }

    /// <summary>True while a run is in progress — the only time Cancel means anything.</summary>
    internal bool IsRunning => _cts is not null;

    /// <summary>
    /// Cancels the run in progress because the user asked for it. Returns <c>true</c> when a run was
    /// actually cancelled; <c>false</c> means there was nothing to cancel. This flag is what makes a
    /// user cancel distinguishable from the time budget expiring.
    /// </summary>
    internal bool TryCancel()
    {
        CancellationTokenSource? cts = _cts;
        if (cts is null) return false;

        _cancelledByUser = true;
        return CancelCore(cts);
    }

    /// <summary>
    /// Aborts a run in progress without recording it as a user cancel — used when the app is
    /// shutting down (ADR-007) and when the scan window closes. Safe to call when nothing is running.
    /// </summary>
    internal void Cancel()
    {
        CancellationTokenSource? cts = _cts;
        if (cts is not null) CancelCore(cts);
    }

    /// <summary>
    /// Cancels a source that may have been disposed in the meantime: the run can finish between
    /// reading the field and cancelling, which is a benign race with nothing left to cancel rather
    /// than an error to surface. Returns <c>true</c> when the cancellation was delivered.
    /// </summary>
    private static bool CancelCore(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs one deep scan under the budget. Returns <c>null</c> when a run is already in progress,
    /// because the ADR allows a single run per user invocation (refused, not queued).
    /// </summary>
    internal async Task<DeepScanResult?> RunAsync()
    {
        if (_cts is not null) return null;

        // Started before the first await so the slot is reserved synchronously: two overlapping
        // callers cannot both pass the check above.
        var cts = new CancellationTokenSource(_budget);
        _cts = cts;
        _cancelledByUser = false;

        Exception? error = null;
        int devicesFound = 0;
        int devicesWithBattery = 0;

        try
        {
            List<DeviceBatteryInfo> results = await _runScan(cts.Token).ConfigureAwait(false);
            devicesFound = results.Count;
            devicesWithBattery = results.Count(device => device.Battery is not null);
        }
        catch (Exception ex)
        {
            // Not swallowed: the exception is classified and handed back to the caller, which logs
            // it and reports the run as failed.
            error = ex;
        }
        finally
        {
            _cts = null;
            cts.Dispose();
        }

        return new DeepScanResult(
            DeepScanPolicy.Classify(error, _cancelledByUser),
            devicesFound,
            devicesWithBattery,
            error);
    }
}
