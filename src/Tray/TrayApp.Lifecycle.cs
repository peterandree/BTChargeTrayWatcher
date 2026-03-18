using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public partial class TrayApp
{
    private async Task ExitAsync()
    {
        if (_exitStarted)
        {
            return;
        }

        _exitStarted = true;

        _scanMenuItem.Enabled = false;
        _lowMenu.Enabled = false;
        _highMenu.Enabled = false;

        try
        {
            Debug.WriteLine("[TrayApp] Exit started.");
            _trayIcon.Visible = false;
            await _monitor.DisposeAsync().ConfigureAwait(true);
            Debug.WriteLine("[TrayApp] Monitor disposed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayApp] Exit fault: {ex}");
        }
        finally
        {
            Application.Exit();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Debug.WriteLine("[TrayApp] Disposing tray app.");

        _monitor.ScanStarted -= Monitor_ScanStarted;
        _monitor.ScanCompleted -= Monitor_ScanCompleted;
        _settings.Changed -= UpdateTooltip;

        _scanMenuItem.Click -= ScanMenuItem_Click;
        _trayIcon.DoubleClick -= TrayIcon_DoubleClick;

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        if (_scanWindow is not null && !_scanWindow.IsDisposed)
        {
            _scanWindow.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
