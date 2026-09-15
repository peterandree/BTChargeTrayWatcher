// src/Tray/DeepScanPolicy.cs
namespace BTChargeTrayWatcher;

/// <summary>
/// How a user-initiated diagnostic deep scan ended (ADR-019).
/// </summary>
internal enum DeepScanOutcome
{
    /// <summary>The scan read every candidate within its budget.</summary>
    Completed,

    /// <summary>The user pressed Cancel.</summary>
    CancelledByUser,

    /// <summary>The global time budget expired before the scan finished.</summary>
    BudgetExceeded,

    /// <summary>The scan threw. The exception is reported alongside this outcome, never swallowed.</summary>
    Failed
}

/// <summary>
/// Pure classification and reporting for a deep scan (ADR-019). No WinForms, no clock and no radio:
/// every member is a function of the run's outcome and the devices it reported, so the rules can be
/// tested without hardware. The run itself is owned by <see cref="DeepScanRunner"/>.
/// </summary>
internal static class DeepScanPolicy
{
    /// <summary>
    /// Maps a finished run to an outcome. A cancellation with no user request behind it is the time
    /// budget expiring — that distinction is what the ADR asks the UI to report (§2, §3).
    /// </summary>
    internal static DeepScanOutcome Classify(Exception? error, bool cancelledByUser)
    {
        if (error is null) return DeepScanOutcome.Completed;

        if (error is OperationCanceledException)
            return cancelledByUser ? DeepScanOutcome.CancelledByUser : DeepScanOutcome.BudgetExceeded;

        return DeepScanOutcome.Failed;
    }

    /// <summary>
    /// Builds the one-line summary shown when a run ends (ADR-019 §3: "devices found / devices with
    /// battery data"). Counts are only known for a run that completed; a cancelled or over-budget
    /// run aborted before the merged read returned, so it must not claim any.
    /// </summary>
    internal static string Describe(DeepScanOutcome outcome, int devicesFound, int devicesWithBattery) =>
        outcome switch
        {
            DeepScanOutcome.Completed when devicesFound <= 0 =>
                "Deep scan complete: no Bluetooth devices found.",

            DeepScanOutcome.Completed =>
                $"Deep scan complete: {devicesFound} device(s) found, {devicesWithBattery} with battery data.",

            DeepScanOutcome.CancelledByUser =>
                "Deep scan cancelled.",

            DeepScanOutcome.BudgetExceeded =>
                $"Deep scan stopped after the {PollingDefaults.DeepScanTimeBudget.TotalSeconds:0} s time budget.",

            _ => "Deep scan failed — see the diagnostic log for details."
        };
}
