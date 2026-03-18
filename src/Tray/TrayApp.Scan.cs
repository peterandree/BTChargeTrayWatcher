using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public partial class TrayApp
{
    private async Task RunStartupScanAsync()
    {
        if (_disposed || _exitStarted)
            return;

        try
        {
            Debug.WriteLine("[TrayApp] Startup scan started.");

            // Push off the UI thread to prevent blocking during boot
            var results = await Task.Run(() => _monitor.StartTrackedScanAsync()).ConfigureAwait(false);

            Debug.WriteLine($"[TrayApp] Startup scan completed. Devices found: {results.Count}.");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[TrayApp] Startup scan cancelled.");
        }
        catch (ObjectDisposedException)
        {
            Debug.WriteLine("[TrayApp] Startup scan aborted because monitor was disposed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayApp] Startup scan fault: {ex}");
        }
    }

    private void Monitor_ScanStarted() =>
        PostToUi(OnScanStarted);

    private void Monitor_ScanCompleted(IReadOnlyList<(string, int)> results) =>
        PostToUi(() =>
        {
            OnScanCompleted();

            bool hasAlert = false;
            foreach (var (name, battery) in results)
            {
                if (battery >= 0 && (battery <= _settings.GetLow(name) || battery >= _settings.GetHigh(name)))
                {
                    hasAlert = true;
                    break;
                }
            }

            UpdateTrayIcon(hasAlert);
        });

    private void ScanMenuItem_Click(object? sender, EventArgs e) =>
        OpenScanWindowAndTriggerScan();

    private void TrayIcon_DoubleClick(object? sender, EventArgs e) =>
        OpenScanWindowAndTriggerScan();

    private void OnScanStarted()
    {
        _scanMenuItem.Text = "⏳ Scanning…";
        _scanMenuItem.Enabled = false;
    }

    private void OnScanCompleted()
    {
        _scanMenuItem.Text = "Scan devices…";
        _scanMenuItem.Enabled = true;
    }

    public void OpenScanWindowAndTriggerScan()
    {
        if (_disposed || _exitStarted)
            return;

        // 1. Ensure window is open and listening
        OpenScanWindowCore();

        // 2. If already scanning, the window will just pick up the events.
        if (_monitor.IsScanning)
        {
            Debug.WriteLine("[TrayApp] Scan already in progress. Window attached to events.");
            return;
        }

        // 3. Launch background scan asynchronously with zero UI thread blocking
        FireAndForget(Task.Run(async () =>
        {
            try
            {
                Debug.WriteLine("[TrayApp] Manual scan started.");
                await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
                Debug.WriteLine("[TrayApp] Manual scan completed.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayApp] Manual scan background fault: {ex}");
            }
        }), "Manual scan");
    }

    public Task OpenScanWindowAsync()
    {
        OpenScanWindowAndTriggerScan();
        return Task.CompletedTask;
    }

    private void OpenScanWindowCore()
    {
        if (_scanWindow is not null && !_scanWindow.IsDisposed)
        {
            _scanWindow.BringToFront();
            _scanWindow.Activate();
            return;
        }

        var window = new ScanWindow();

        void OnFound(string name, int battery)
        {
            if (window.IsDisposed) return;

            // Use the control's own native Invoke marshaling to guarantee safety
            if (window.InvokeRequired)
            {
                window.BeginInvoke(new Action(() =>
                {
                    if (!window.IsDisposed) window.OnDeviceFound(name, battery);
                }));
            }
            else
            {
                window.OnDeviceFound(name, battery);
            }
        }

        void OnCompleted(IReadOnlyList<(string, int)> results)
        {
            if (window.IsDisposed) return;

            if (window.InvokeRequired)
            {
                window.BeginInvoke(new Action(() =>
                {
                    if (!window.IsDisposed) window.OnScanComplete(results.Count);
                }));
            }
            else
            {
                window.OnScanComplete(results.Count);
            }
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

        // Let WinForms naturally show the window and pump messages
        window.Show();
    }
}
