using Xunit;

namespace BTChargeTrayWatcher.Tests;

/// <summary>
/// Tests for the deep-scan presentation state of <see cref="ScanViewModel"/> (ADR-019 §1 warning text
/// and §3 progress/summary). The view model has no WinForms dependency, so none of this needs a UI
/// thread; the window only binds to what is asserted here.
/// </summary>
public sealed class ScanViewModelDeepScanTests
{
    [Fact]
    public void Warning_text_matches_the_wording_fixed_by_the_ADR()
    {
        // ADR-019 §1 requires this exact one-line warning to be presented before the scan runs.
        Assert.Equal(
            "This scan may temporarily increase Bluetooth activity; recommended for troubleshooting only.",
            ScanViewModel.DeepScanWarningText);
    }

    [Fact]
    public void Button_text_matches_the_label_fixed_by_the_ADR()
    {
        Assert.Equal("Deep scan (diagnostic)", ScanViewModel.DeepScanButtonText);
    }

    [Fact]
    public void A_deep_scan_marks_itself_running_and_explains_the_budget()
    {
        using var vm = new ScanViewModel(new ThresholdSettings());
        var states = new List<bool>();
        string? status = null;
        vm.DeepScanActiveChanged += active => states.Add(active);
        vm.StatusChanged += text => status = text;

        vm.OnDeepScanStarted();

        Assert.True(vm.DeepScanActive);
        Assert.Collection(states, active => Assert.True(active));
        Assert.Contains("Cancel", status);
        Assert.Contains($"{PollingDefaults.DeepScanTimeBudget.TotalSeconds:0} s", status);
    }

    [Fact]
    public void A_finished_deep_scan_clears_the_running_state_and_shows_the_summary()
    {
        using var vm = new ScanViewModel(new ThresholdSettings());
        var states = new List<bool>();
        string? status = null;
        vm.DeepScanActiveChanged += active => states.Add(active);
        vm.StatusChanged += text => status = text;

        vm.OnDeepScanStarted();
        vm.OnDeepScanCompleted(new DeepScanResult(DeepScanOutcome.Completed, 6, 4));

        Assert.False(vm.DeepScanActive);
        Assert.Collection(states, active => Assert.True(active), active => Assert.False(active));
        Assert.Equal(DeepScanPolicy.Describe(DeepScanOutcome.Completed, 6, 4), status);
    }

    [Fact]
    public void The_generic_scan_status_does_not_replace_the_deep_scan_running_text()
    {
        // Starting a scan raises OnScanStarted; during a deep run that must not drop the budget and
        // cancel hint the user just confirmed.
        using var vm = new ScanViewModel(new ThresholdSettings());
        string? status = null;
        vm.StatusChanged += text => status = text;

        vm.OnDeepScanStarted();
        vm.OnScanStarted();

        Assert.Contains("Deep scan running", status);
    }

    [Fact]
    public void A_normal_scan_still_uses_the_generic_status_text()
    {
        using var vm = new ScanViewModel(new ThresholdSettings());
        string? status = null;
        vm.StatusChanged += text => status = text;

        vm.OnScanStarted();

        Assert.Equal("Scanning for Bluetooth devices...", status);
    }

    [Fact]
    public void A_cancelled_deep_scan_says_it_was_cancelled()
    {
        using var vm = new ScanViewModel(new ThresholdSettings());
        string? status = null;
        vm.StatusChanged += text => status = text;

        vm.OnDeepScanStarted();
        vm.OnDeepScanCompleted(new DeepScanResult(DeepScanOutcome.CancelledByUser, 0, 0));

        Assert.Equal("Deep scan cancelled.", status);
    }

    [Fact]
    public void The_auto_refresh_status_does_not_overwrite_the_deep_scan_summary()
    {
        using var vm = new ScanViewModel(new ThresholdSettings());
        vm.OnDeepScanStarted();
        vm.OnDeepScanCompleted(new DeepScanResult(DeepScanOutcome.Completed, 3, 3));

        // A completed deep scan leaves ScanComplete true (the scanner raised it), so the 1 s tick
        // would otherwise replace the answer with the countdown a second later.
        var statuses = new List<string>();
        vm.StatusChanged += text => statuses.Add(text);
        vm.OnScanComplete([]);

        Assert.True(vm.ScanComplete);
        Assert.True(vm.IsDeepScanSummaryPending);
        Assert.Empty(statuses);
    }

    [Fact]
    public void Starting_a_new_scan_releases_the_summary_slot()
    {
        using var vm = new ScanViewModel(new ThresholdSettings());
        vm.OnDeepScanStarted();
        vm.OnDeepScanCompleted(new DeepScanResult(DeepScanOutcome.Completed, 3, 3));

        vm.OnScanStarted();

        Assert.False(vm.IsDeepScanSummaryPending);
        Assert.False(vm.DeepScanActive);
    }

    [Fact]
    public void Starting_a_deep_scan_while_one_is_running_leaves_it_running()
    {
        // The shell disables the action, but the view model must not flap if it is called twice.
        using var vm = new ScanViewModel(new ThresholdSettings());
        vm.OnDeepScanStarted();
        vm.OnDeepScanStarted();

        Assert.True(vm.DeepScanActive);
    }
}
