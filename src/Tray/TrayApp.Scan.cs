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
            var results = await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
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

    private async void ScanMenuItem_Click(object? sender, EventArgs e) =>
        await RunUiActionAsync(OpenScanWindowAsync, "Scan menu");

    private async void TrayIcon_DoubleClick(object? sender, EventArgs e) =>
        await RunUiActionAsync(OpenScanWindowAsync, "Tray double-click");

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

    public Task OpenScanWindowAsync()
    {
        if (_disposed || _exitStarted)
            return Task.CompletedTask;

        return OpenScanWindowCoreAsync();
    }

    private async Task OpenScanWindowCoreAsync()
    {
        bool isAlreadyScanning = _monitor.IsScanning;
        ScanWindow? currentWindow = null;
        var windowReadyCompletionSource = new TaskCompletionSource<bool>();

        PostToUi(() =>
        {
            if (_scanWindow is not null && !_scanWindow.IsDisposed)
            {
                _scanWindow.BringToFront();
                _scanWindow.Activate();
                currentWindow = _scanWindow;
                windowReadyCompletionSource.SetResult(true);
                return;
            }

            var window = new ScanWindow();

            void OnFound(string name, int battery)
            {
                PostToUi(() =>
                {
                    if (!window.IsDisposed)
                        window.OnDeviceFound(name, battery);
                });
            }

            void OnCompleted(IReadOnlyList<(string, int)> results)
            {
                PostToUi(() =>
                {
                    if (!window.IsDisposed)
                        window.OnScanComplete(results.Count);
                });
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

            // Ensure window paints before doing work
            window.Shown += (_, _) =>
            {
                window.Refresh(); // Force synchronous paint
                windowReadyCompletionSource.TrySetResult(true);
            };

            _scanWindow = window;
            currentWindow = window;
            window.Show(); // Show non-modal so it pumps messages
        });

        // Wait until the UI thread has completely shown and painted the window
        await windowReadyCompletionSource.Task.ConfigureAwait(false);

        if (isAlreadyScanning)
        {
            Debug.WriteLine("[TrayApp] Scan window opened while scan already in progress. Listening to ongoing scan.");
            return;
        }

        try
        {
            Debug.WriteLine("[TrayApp] Manual scan started.");
            var results = await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
            Debug.WriteLine($"[TrayApp] Manual scan completed. Devices found: {results.Count}.");

            PostToUi(() =>
            {
                if (currentWindow is not null && !currentWindow.IsDisposed)
                {
                    currentWindow.OnScanComplete(results.Count);
                }
            });
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[TrayApp] Manual scan cancelled.");
        }
        catch (ObjectDisposedException)
        {
            Debug.WriteLine("[TrayApp] Manual scan aborted because monitor was disposed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayApp] Manual scan fault: {ex}");
        }
    }
}
