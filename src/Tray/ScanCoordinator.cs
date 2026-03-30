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
    // operationName identifies which operation faulted; the exception is the unwrapped cause.
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
    }

    public void StartBackgroundScan() =>
        FireAndForget(RunStartupScanAsync(), "Startup scan");

    // Posts a request to the UI thread to open the scan window and trigger a scan.
    // Returns immediately; does not represent completion of either action.
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
        await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
        Debug.WriteLine("[ScanCoordinator] Manual scan completed.");
    }

    private void Monitor_ScanStarted() =>
        PostToUi(() => ScanStarted?.Invoke());

    private void Monitor_ManualScanCompleted(IReadOnlyList<DeviceBatteryInfo> results) =>
        PostToUi(() =>
        {
            ScanCompleted?.Invoke(results);
            AlertStateChanged?.Invoke(EvaluateAlert(results));
        });

    private void Monitor_BackgroundRefreshCompleted(IReadOnlyList<DeviceBatteryInfo> results) =>
        PostToUi(() => AlertStateChanged?.Invoke(EvaluateAlert(results)));

    private bool EvaluateAlert(IReadOnlyList<DeviceBatteryInfo> results)
    {
        foreach (var device in results)
        {
            if (_settings.IgnoredDevices.Contains(device.Name)) continue;
            if (_settings.TrayIconOverlayExcludedDevices.Contains(device.Name)) continue;

            if (device.Battery.HasValue &&
                (device.Battery.Value <= _settings.GetLow(device.Name) ||
                 device.Battery.Value >= _settings.GetHigh(device.Name)))
            {
                return true;
            }
        }
        return false;
    }

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
            MarshalToWindow(window, () => window.OnScanComplete(results.Count));

        _monitor.DeviceFound += OnFound;
        _monitor.ManualScanCompleted += OnCompleted;

        window.FormClosed += (_, _) =>
        {
            _monitor.DeviceFound -= OnFound;
            _monitor.ManualScanCompleted -= OnCompleted;
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

    // Instance method — needs access to ScanFaulted event.
    // Expected cancellation and disposal are not faults; they are swallowed silently.
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

        if (_scanWindow is not null && !_scanWindow.IsDisposed)
            _scanWindow.Dispose();
    }
}
