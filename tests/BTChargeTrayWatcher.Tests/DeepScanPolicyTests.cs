using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Tests for the deep-scan outcome rules of ADR-019 (§2 single run + budget, §3 summary).
/// No radio, no clock, no WinForms — every member under test is a pure function.
/// </summary>
public sealed class DeepScanPolicyTests
{
    [Fact]
    public void Finished_run_is_completed()
    {
        Assert.Equal(DeepScanOutcome.Completed, DeepScanPolicy.Classify(error: null, cancelledByUser: false));
    }

    [Fact]
    public void Cancellation_the_user_asked_for_is_reported_as_cancelled()
    {
        Assert.Equal(
            DeepScanOutcome.CancelledByUser,
            DeepScanPolicy.Classify(error: new OperationCanceledException(), cancelledByUser: true));
    }

    [Fact]
    public void Cancellation_nobody_asked_for_is_the_time_budget_expiring()
    {
        // The token was cancelled without a user request, so only the 30 s budget can explain it.
        Assert.Equal(
            DeepScanOutcome.BudgetExceeded,
            DeepScanPolicy.Classify(error: new OperationCanceledException(), cancelledByUser: false));
    }

    [Fact]
    public void Task_cancelled_exception_is_treated_as_a_cancellation()
    {
        // StartTrackedDeepScanAsync reports cancellation as a TaskCanceledException.
        Assert.Equal(
            DeepScanOutcome.BudgetExceeded,
            DeepScanPolicy.Classify(error: new TaskCanceledException(), cancelledByUser: false));
    }

    [Fact]
    public void Any_other_failure_is_a_failure()
    {
        Assert.Equal(
            DeepScanOutcome.Failed,
            DeepScanPolicy.Classify(error: new InvalidOperationException("radio gone"), cancelledByUser: false));
    }

    [Fact]
    public void Completed_summary_reports_devices_found_and_devices_with_battery_data()
    {
        string text = DeepScanPolicy.Describe(DeepScanOutcome.Completed, devicesFound: 7, devicesWithBattery: 5);

        Assert.Contains("7", text);
        Assert.Contains("5", text);
        Assert.Contains("complete", text);
    }

    [Fact]
    public void Completed_summary_with_no_devices_says_so_instead_of_showing_zeros()
    {
        string text = DeepScanPolicy.Describe(DeepScanOutcome.Completed, devicesFound: 0, devicesWithBattery: 0);

        Assert.Contains("no Bluetooth devices found", text);
        Assert.DoesNotContain("0 device", text);
    }

    [Fact]
    public void Cancelled_summary_distinguishes_the_user_from_the_budget()
    {
        string userCancel = DeepScanPolicy.Describe(DeepScanOutcome.CancelledByUser, 0, 0);
        string budget     = DeepScanPolicy.Describe(DeepScanOutcome.BudgetExceeded, 0, 0);

        Assert.Equal("Deep scan cancelled.", userCancel);
        Assert.NotEqual(userCancel, budget);
        Assert.Contains("budget", budget);
    }

    [Fact]
    public void Budget_summary_quotes_the_configured_budget()
    {
        // Guards against the text and the constant drifting apart.
        int budgetSeconds = (int)PollingDefaults.DeepScanTimeBudget.TotalSeconds;

        Assert.Equal(30, budgetSeconds);
        Assert.Contains(
            $"{budgetSeconds} s",
            DeepScanPolicy.Describe(DeepScanOutcome.BudgetExceeded, 0, 0));
    }

    [Fact]
    public void Failed_summary_points_at_the_diagnostic_log()
    {
        Assert.Contains(
            "log",
            DeepScanPolicy.Describe(DeepScanOutcome.Failed, 0, 0));
    }
}
