// src/Tray/ScanCoordinator.cs
using System.Diagnostics;

namespace BTChargeTrayWatcher;

internal sealed class ScanCoordinator : IDisposable
{
    private readonly BluetoothBatteryMonitor _monitor;
    private readonly ThresholdSettings _settings;
    private readonly SynchronizationContext _uiContext;
    private readonly DeepScanRunner _deepScanRunner;

    private ScanWindow? _scanWindow;
    private bool _disposed;

    public event Action<bool>? AlertStateChanged;
    public event Action? ScanStarted;
    public event Action<IReadOnlyList<DeviceBatteryInfo>>? ScanCompleted;

    // Raised on the thread-pool when a background scan task faults.
    public event Action<string, Exception>? ScanFaulted;

    public ScanCoordinator(
        BluetoothBatteryMonitor monitor,
        ThresholdSettings settings,
        SynchronizationContext uiContext)
    {
        _monitor = monitor;
        _settings = settings;
        _uiContext = uiContext;

        // ADR-019: a deep scan is budgeted and cancellable, and only a user request may start one.
        _deepScanRunner = new DeepScanRunner(
            monitor.StartTrackedDeepScanAsync,
            PollingDefaults.DeepScanTimeBudget);

        _monitor.ScanStarted += Monitor_ScanStarted;
        _monitor.ManualScanCompleted += Monitor_ManualScanCompleted;

        // Alert state is now driven exclusively by the orchestrator's classified,
        // hysteresis-consistent state (ADR-011, fixes #44).
        _monitor.AlertStateChanged += Monitor_AlertStateChanged;
    }

    public void StartBackgroundScan() =>
        FireAndForget(RunStartupScanAsync(), "Startup scan");

    public void RequestOpenScanWindow() =>
        PostToUi(OpenScanWindowAndTriggerScan);

    /// <summary>
    /// Starts the confirmed diagnostic deep scan. Called only from <see cref="ScanWindow"/> after the
    /// user acknowledged the warning — nothing automatic may reach this path (ADR-019 §1).
    /// </summary>
    public void RequestDeepScan() => PostToUi(StartDeepScan);

    /// <summary>Stops a running deep scan early because the user pressed Cancel.</summary>
    public void RequestDeepScanCancel() => PostToUi(() => _deepScanRunner.TryCancel());

    public void OpenScanWindowAndTriggerScan()
    {
        if (_disposed) return;

        OpenScanWindowCore();

        if (_monitor.IsScanning)
        {
            Debug.WriteLine("[ScanCoordinator] Scan already in progress.");
            return;
        }

        FireAndForget(RunWindowScanAsync(), "Scan");
    }

    private async Task RunStartupScanAsync()
    {
        if (_disposed) return;
        Debug.WriteLine("[ScanCoordinator] Startup scan started.");

        // Automatic, unconfirmed: always the passive background path (ADR-017, issue #164).
        await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
        Debug.WriteLine("[ScanCoordinator] Startup scan completed.");
    }

    /// <summary>
    /// The scan behind the tray's <i>Scan devices…</i> action, the window opening and the window's
    /// auto-refresh. Passive on purpose: opening a window is not confirmation, and the auto-refresh
    /// loop would otherwise run an active scan every 30 s. Active probing belongs to the confirmed
    /// deep scan (ADR-019 §1, issue #165).
    /// </summary>
    private async Task RunWindowScanAsync()
    {
        Debug.WriteLine("[ScanCoordinator] Window scan started.");
        await _monitor.RefreshTrackedDevicesAsync().ConfigureAwait(false);
        await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
        Debug.WriteLine("[ScanCoordinator] Window scan completed.");
    }

    /// <summary>
    /// Starts a confirmed deep scan (ADR-019). Runs on the UI thread so the window's controls and the
    /// runner's single-run reservation cannot interleave with a second click.
    /// </summary>
    private void StartDeepScan()
    {
        if (_disposed) return;
        if (_scanWindow is not { IsDisposed: false } window) return;

        FireAndForget(RunDeepScanAsync(window), "Deep scan");
    }

    private async Task RunDeepScanAsync(ScanWindow window)
    {
        // The runner reserves its single-run slot synchronously, and this method is entered on the UI
        // thread, so nothing can be notified before the reservation exists.
        Task<DeepScanResult?> run = _deepScanRunner.RunAsync();
        if (!_deepScanRunner.IsRunning)
        {
            // A run was already in flight: one run per invocation (ADR-019 §2).
            Debug.WriteLine("[ScanCoordinator] Deep scan request refused: a run is already in progress.");
            return;
        }

        window.OnDeepScanStarted();
        Debug.WriteLine("[ScanCoordinator] Deep scan started.");

        DeepScanResult? result = await run.ConfigureAwait(false);
        if (result is not { } completed)
        {
            // Unreachable while the reservation above succeeds; release the window instead of leaving
            // it stuck in the running state if it ever changes.
            Debug.WriteLine("[ScanCoordinator] Deep scan produced no result.");
            MarshalToWindow(window, () =>
                window.OnDeepScanCompleted(new DeepScanResult(DeepScanOutcome.Failed, 0, 0)));
            return;
        }

        if (completed.Outcome == DeepScanOutcome.Failed && completed.Error is { } cause)
        {
            Trace.TraceError($"[ScanCoordinator] Deep scan fault: {cause}");
            ScanFaulted?.Invoke("Deep scan", cause);
        }

        // Budget expiry and user cancel are normal outcomes, not faults: the window reports them.
        MarshalToWindow(window, () => window.OnDeepScanCompleted(completed));
        Debug.WriteLine(
            $"[ScanCoordinator] Deep scan finished ({completed.Outcome}): " +
            $"{completed.DevicesFound} found, {completed.DevicesWithBattery} with battery data.");
    }

    private void Monitor_ScanStarted() =>
        PostToUi(() => ScanStarted?.Invoke());

    private void Monitor_ManualScanCompleted(IReadOnlyList<DeviceBatteryInfo> results) =>
        PostToUi(() => ScanCompleted?.Invoke(results));

    private void Monitor_AlertStateChanged(bool hasAlert) =>
        PostToUi(() => AlertStateChanged?.Invoke(hasAlert));

    private void OpenScanWindowCore()
    {
        if (_scanWindow is not null && !_scanWindow.IsDisposed)
        {
            BringExistingWindowToFront(_scanWindow);
            return;
        }

        var window = new ScanWindow(_settings);

        void OnFound(string deviceId, string name, int? battery) =>
            MarshalToWindow(window, () => window.OnDeviceFound(deviceId, name, battery));

        void OnCompleted(IReadOnlyList<DeviceBatteryInfo> results) =>
            MarshalToWindow(window, () => window.OnScanComplete(_monitor.TrackedDevices));

        void OnStarted() =>
            MarshalToWindow(window, () => window.OnScanStarted());

        _monitor.DeviceFound += OnFound;
        _monitor.ManualScanCompleted += OnCompleted;
        _monitor.ScanStarted += OnStarted;

        // Feat 61: Auto-refresh integration
        EventHandler? autoRefreshHandler = null;
        autoRefreshHandler = (_, _) =>
        {
            if (_monitor.IsScanning) return;
            FireAndForget(RunWindowScanAsync(), "Auto-refresh scan");
        };
        window.AutoRefreshRequested += autoRefreshHandler;

        // ADR-019 §1–§3: the diagnostic scan is requested and cancelled from the window only.
        EventHandler deepScanHandler        = (_, _) => RequestDeepScan();
        EventHandler deepScanCancelHandler  = (_, _) => RequestDeepScanCancel();
        window.DeepScanRequested       += deepScanHandler;
        window.DeepScanCancelRequested += deepScanCancelHandler;

        window.FormClosed += (_, _) =>
        {
            _monitor.DeviceFound -= OnFound;
            _monitor.ManualScanCompleted -= OnCompleted;
            _monitor.ScanStarted -= OnStarted;
            window.AutoRefreshRequested -= autoRefreshHandler!;
            window.DeepScanRequested       -= deepScanHandler;
            window.DeepScanCancelRequested -= deepScanCancelHandler;
            if (ReferenceEquals(_scanWindow, window))
                _scanWindow = null;

            // Closing the window is the last control surface the running deep scan has, so closing
            // it stops the scan instead of leaving it probing devices with nothing on screen
            // (ADR-019 §2: the run is cancellable).
            if (_deepScanRunner.TryCancel())
                Debug.WriteLine("[ScanCoordinator] Deep scan cancelled: scan window closed.");
        };

        _scanWindow = window;
        window.Show();
        window.BringToFront();
        window.Activate();
    }

    /// <summary>Marshals a window call onto the UI thread; a disposed window is simply dropped.</summary>
    private static void MarshalToWindow(ScanWindow window, Action action)
    {
        if (window.IsDisposed) return;
        if (window.InvokeRequired)
            window.BeginInvoke(new Action(() => { if (!window.IsDisposed) action(); }));
        else
            action();
    }

    private static void BringExistingWindowToFront(ScanWindow window)
    {
        if (window.IsDisposed) return;

        void Bring()
        {
            if (window.IsDisposed) return;
            if (!window.Visible) window.Show();
            if (window.WindowState == FormWindowState.Minimized)
                window.WindowState = FormWindowState.Normal;
            window.BringToFront();
            window.Activate();
        }

        if (window.InvokeRequired)
            window.BeginInvoke(new Action(Bring));
        else
            Bring();
    }

    private void PostToUi(Action action)
    {
        if (_disposed) return;
        _uiContext.Post(_ =>
        {
            if (_disposed) return;
            try { action(); }
            catch (Exception ex) { Debug.WriteLine($"[ScanCoordinator] UI dispatch fault: {ex}"); }
        }, null);
    }

    private void FireAndForget(Task task, string operationName)
    {
        _ = task.ContinueWith(t =>
        {
            if (!t.IsFaulted || t.Exception is null) return;

            Exception cause = t.Exception.Flatten().InnerException ?? t.Exception;

            if (cause is OperationCanceledException || cause is ObjectDisposedException) return;

            Trace.TraceError($"[ScanCoordinator] {operationName} fault: {cause}");
            ScanFaulted?.Invoke(operationName, cause);
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitor.ScanStarted -= Monitor_ScanStarted;
        _monitor.ManualScanCompleted -= Monitor_ManualScanCompleted;
        _monitor.AlertStateChanged -= Monitor_AlertStateChanged;

        // Cooperative shutdown (ADR-007): abort a running deep scan; the scan's own task is tracked
        // by the monitor, which waits for it in its own DisposeAsync.
        _deepScanRunner.Cancel();

        if (_scanWindow is not null && !_scanWindow.IsDisposed)
            _scanWindow.Dispose();
    }
}
