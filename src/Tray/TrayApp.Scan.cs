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
            PopulateDevicesMenu(_devicesMenu, results);
        });

    private async void ScanMenuItem_Click(object? sender, EventArgs e) =>
        await RunUiActionAsync(OpenScanWindowAsync, "Scan menu");

    private async void TrayIcon_DoubleClick(object? sender, EventArgs e) =>
        await RunUiActionAsync(OpenScanWindowAsync, "Tray double-click");

    private void OnScanStarted()
    {
        _devicesMenu.DropDownItems.Clear();
        _devicesMenu.DropDownItems.Add(
            new ToolStripMenuItem("⏳ Scan in progress…") { Enabled = false });

        _scanMenuItem.Text = "⏳ Scanning…";
        _scanMenuItem.Enabled = false;
        _lowMenu.Enabled = false;
        _highMenu.Enabled = false;
    }

    private void OnScanCompleted()
    {
        _scanMenuItem.Text = "Scan devices…";
        _scanMenuItem.Enabled = true;
        _lowMenu.Enabled = true;
        _highMenu.Enabled = true;
    }

    public Task OpenScanWindowAsync()
    {
        if (_disposed || _exitStarted)
            return Task.CompletedTask;

        return OpenScanWindowCoreAsync();
    }

    private async Task OpenScanWindowCoreAsync()
    {
        PostToUi(() =>
        {
            if (_scanWindow is not null && !_scanWindow.IsDisposed)
            {
                _scanWindow.BringToFront();
                _scanWindow.Activate();
                return;
            }

            var window = new ScanWindow();
            window.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_scanWindow, window))
                    _scanWindow = null;
            };

            _scanWindow = window;
            window.Show();
        });

        ScanWindow? win = _scanWindow;
        if (win is null || win.IsDisposed)
            return;

        void OnFound(string name, int battery)
        {
            PostToUi(() =>
            {
                if (!win.IsDisposed)
                    win.OnDeviceFound(name, battery);
            });
        }

        void OnCompleted(IReadOnlyList<(string, int)> results)
        {
            PostToUi(() =>
            {
                if (!win.IsDisposed)
                    win.OnScanComplete(results.Count);
            });
        }

        _monitor.DeviceFound += OnFound;
        _monitor.ScanCompleted += OnCompleted;

        try
        {
            if (_monitor.IsScanning)
            {
                Debug.WriteLine("[TrayApp] Scan window opened while scan already in progress.");
                return;
            }

            Debug.WriteLine("[TrayApp] Manual scan started.");
            var results = await _monitor.StartTrackedScanAsync().ConfigureAwait(false);
            Debug.WriteLine($"[TrayApp] Manual scan completed. Devices found: {results.Count}.");
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
            throw;
        }
        finally
        {
            _monitor.DeviceFound -= OnFound;
            _monitor.ScanCompleted -= OnCompleted;
        }
    }
}
