// src/Tray/ScanCoordinator.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

internal sealed class ScanCoordinator : IDisposable
{
    private readonly BluetoothBatteryMonitor _monitor;
    private readonly ThresholdSettings _settings;
    private readonly SynchronizationContext _uiContext;

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

        _monitor.ScanStarted += Monitor_ScanStarted;
        _monitor.ManualScanCompleted += Monitor_ManualScanCompleted;
        _monitor.BackgroundRefreshCompleted += Monitor_BackgroundRefreshCompleted;

        // Alert state is now driven exclusively by the orchestrator's classified,
        // hysteresis-consistent state (ADR-011, fixes #44).
        _monitor.AlertStateChanged += Monitor_AlertStateChanged;
    }

    public void StartBackgroundScan() =>
        FireAndForget(RunStartupScanAsync(), "Startup scan");

    public void RequestOpenScanWindow() =>
        PostToUi(OpenScanWindowAndTriggerScan);

    public void OpenScanWindowAndTriggerScan()
    {
        if (_disposed) return;

        OpenScanWindowCore();

        if (_monitor.IsScanning)
        {
            Debug.WriteLine("[ScanCoordinator] Scan already in progress.");
            return;
        }

        FireAndForget(RunManualScanAsync(), "Manual scan");
    }

    private async Task RunStartupScanAsync()
    {
        if (_disposed) return;
        Debug.WriteLine("[ScanCoordinator] Startup scan started.");
        await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
        Debug.WriteLine("[ScanCoordinator] Startup scan completed.");
    }

    private async Task RunManualScanAsync()
    {
        Debug.WriteLine("[ScanCoordinator] Manual scan started.");
        await _monitor.RefreshTrackedDevicesAsync().ConfigureAwait(false);
        await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
        Debug.WriteLine("[ScanCoordinator] Manual scan completed.");
    }

    private void Monitor_ScanStarted() =>
        PostToUi(() => ScanStarted?.Invoke());

    private void Monitor_ManualScanCompleted(IReadOnlyList<DeviceBatteryInfo> results) =>
        PostToUi(() => ScanCompleted?.Invoke(results));

    private void Monitor_BackgroundRefreshCompleted(IReadOnlyList<DeviceBatteryInfo> results) =>
        PostToUi(() => { /* results available for future extension */ });

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

        static void MarshalToWindow(ScanWindow w, Action action)
        {
            if (w.IsDisposed) return;
            if (w.InvokeRequired)
                w.BeginInvoke(new Action(() => { if (!w.IsDisposed) action(); }));
            else
                action();
        }

        void OnFound(string deviceId, string name, int? battery) =>
            MarshalToWindow(window, () => window.OnDeviceFound(deviceId, name, battery));

        void OnCompleted(IReadOnlyList<DeviceBatteryInfo> results) =>
            MarshalToWindow(window, () => window.OnScanComplete(results.Count, _monitor.TrackedDevices));

        void OnStarted() =>
            MarshalToWindow(window, () => window.OnScanStarted());

        _monitor.DeviceFound += OnFound;
        _monitor.ManualScanCompleted += OnCompleted;
        _monitor.ScanStarted += OnStarted;

        window.FormClosed += (_, _) =>
        {
            _monitor.DeviceFound -= OnFound;
            _monitor.ManualScanCompleted -= OnCompleted;
            _monitor.ScanStarted -= OnStarted;
            if (ReferenceEquals(_scanWindow, window))
                _scanWindow = null;
        };

        _scanWindow = window;
        window.Show();
        window.BringToFront();
        window.Activate();
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
        _monitor.BackgroundRefreshCompleted -= Monitor_BackgroundRefreshCompleted;
        _monitor.AlertStateChanged -= Monitor_AlertStateChanged;

        if (_scanWindow is not null && !_scanWindow.IsDisposed)
            _scanWindow.Dispose();
    }
}
