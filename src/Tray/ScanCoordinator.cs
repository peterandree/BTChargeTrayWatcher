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
    public event Action? ScanCompleted;

    public ScanCoordinator(
        BluetoothBatteryMonitor monitor,
        ThresholdSettings settings,
        SynchronizationContext uiContext)
    {
        _monitor = monitor;
        _settings = settings;
        _uiContext = uiContext;

        _monitor.ScanStarted += Monitor_ScanStarted;
        _monitor.ScanCompleted += Monitor_ScanCompleted;
    }

    public void StartBackgroundScan() =>
        FireAndForget(RunStartupScanAsync(), "Startup scan");

    public Task OpenScanWindowAsync()
    {
        PostToUi(OpenScanWindowAndTriggerScan);
        return Task.CompletedTask;
    }

    public void OpenScanWindowAndTriggerScan()
    {
        if (_disposed) return;

        OpenScanWindowCore();

        if (_monitor.IsScanning)
        {
            Debug.WriteLine("[ScanCoordinator] Scan already in progress.");
            return;
        }

        FireAndForget(Task.Run(async () =>
        {
            try
            {
                Debug.WriteLine("[ScanCoordinator] Manual scan started.");
                await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
                Debug.WriteLine("[ScanCoordinator] Manual scan completed.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScanCoordinator] Manual scan fault: {ex}");
            }
        }), "Manual scan");
    }

    private async Task RunStartupScanAsync()
    {
        if (_disposed) return;

        try
        {
            Debug.WriteLine("[ScanCoordinator] Startup scan started.");
            await Task.Run(() => _monitor.StartTrackedScanAsync()).ConfigureAwait(false);
            Debug.WriteLine("[ScanCoordinator] Startup scan completed.");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ScanCoordinator] Startup scan cancelled.");
        }
        catch (ObjectDisposedException)
        {
            Debug.WriteLine("[ScanCoordinator] Startup scan aborted — monitor disposed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScanCoordinator] Startup scan fault: {ex}");
        }
    }

    private void Monitor_ScanStarted() =>
        PostToUi(() => ScanStarted?.Invoke());

    private void Monitor_ScanCompleted(IReadOnlyList<(string, int)> results) =>
        PostToUi(() =>
        {
            ScanCompleted?.Invoke();

            bool hasAlert = false;
            foreach (var (name, battery) in results)
            {
                if (_settings.IgnoredDevices.Contains(name)) continue;

                if (battery >= 0 &&
                    (battery <= _settings.GetLow(name) || battery >= _settings.GetHigh(name)))
                {
                    hasAlert = true;
                    break;
                }
            }

            AlertStateChanged?.Invoke(hasAlert);
        });

    private void OpenScanWindowCore()
    {
        if (_scanWindow is not null && !_scanWindow.IsDisposed)
        {
            BringExistingWindowToFront(_scanWindow);
            return;
        }

        var window = new ScanWindow(_settings);

        void OnFound(string name, int battery)
        {
            if (window.IsDisposed) return;
            if (window.InvokeRequired)
                window.BeginInvoke(new Action(() => { if (!window.IsDisposed) window.OnDeviceFound(name, battery); }));
            else
                window.OnDeviceFound(name, battery);
        }

        void OnCompleted(IReadOnlyList<(string, int)> results)
        {
            if (window.IsDisposed) return;
            if (window.InvokeRequired)
                window.BeginInvoke(new Action(() => { if (!window.IsDisposed) window.OnScanComplete(results.Count); }));
            else
                window.OnScanComplete(results.Count);
        }

        _monitor.DeviceFound += OnFound;
        _monitor.ScanCompleted += OnCompleted;

        window.FormClosed += (_, _) =>
        {
            _monitor.DeviceFound -= OnFound;
            _monitor.ScanCompleted -= OnCompleted;
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

    private static void FireAndForget(Task task, string operationName)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
                Debug.WriteLine($"[ScanCoordinator] {operationName} fault: {t.Exception}");
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitor.ScanStarted -= Monitor_ScanStarted;
        _monitor.ScanCompleted -= Monitor_ScanCompleted;

        if (_scanWindow is not null && !_scanWindow.IsDisposed)
            _scanWindow.Dispose();
    }
}
