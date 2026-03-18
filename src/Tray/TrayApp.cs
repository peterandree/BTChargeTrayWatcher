using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

public partial class TrayApp : IDisposable
{
    private readonly ThresholdSettings _settings;
    private readonly BluetoothBatteryMonitor _monitor;
    private readonly NotificationService _notifier;
    private readonly DeviceDumper _dumper = new();
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _lowMenu;
    private readonly ToolStripMenuItem _highMenu;
    private readonly ToolStripMenuItem _devicesMenu;
    private readonly ToolStripMenuItem _scanMenuItem;
    private readonly SynchronizationContext _uiContext;

    private ScanWindow? _scanWindow;
    private bool _disposed;
    private bool _exitStarted;

    public TrayApp(
        ThresholdSettings settings,
        BluetoothBatteryMonitor monitor,
        NotificationService notifier)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayApp must be created on the UI thread.");

        _settings = settings;
        _monitor = monitor;
        _notifier = notifier;

        _trayIcon = new NotifyIcon
        {
            Visible = true
        };

        _lowMenu = BuildLowMenu();
        _highMenu = BuildHighMenu();
        _devicesMenu = BuildDevicesMenu();
        _scanMenuItem = new ToolStripMenuItem("Scan devices…");
        _scanMenuItem.Click += ScanMenuItem_Click;

        _trayIcon.ContextMenuStrip = BuildContextMenu();
        _trayIcon.DoubleClick += TrayIcon_DoubleClick;

        _monitor.ScanStarted += Monitor_ScanStarted;
        _monitor.ScanCompleted += Monitor_ScanCompleted;

        _settings.Changed += UpdateTooltip;
        UpdateTooltip();
        UpdateTrayIcon(false);

        _notifier.OnNotificationClicked += () => PostToUi(() => _ = OpenScanWindowAsync());
    }

    private void UpdateTrayIcon(bool hasAlert)
    {
        // Render at a fixed high resolution for supreme crispness on all High-DPI scales
        int size = 128;
        using Bitmap bmp = new(size, size);
        using Graphics g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Base Bluetooth Blue
        Color btBlue = Color.FromArgb(0, 120, 215);

        using (SolidBrush bgBrush = new(btBlue))
        {
            g.FillEllipse(bgBrush, 4, 4, size - 8, size - 8);
        }

        using (Pen whitePen = new(Color.White, size / 10f)) // Thick, scalable pen
        {
            whitePen.LineJoin = LineJoin.Round;
            whitePen.StartCap = LineCap.Round;
            whitePen.EndCap = LineCap.Round;

            float midX = size / 2f;
            float top = size * 0.22f;
            float btm = size * 0.78f;
            float right = size * 0.68f;
            float left = size * 0.32f;

            g.DrawLine(whitePen, midX, top, midX, btm);
            g.DrawLine(whitePen, left, size * 0.35f, midX, btm);
            g.DrawLine(whitePen, midX, btm, right, size * 0.62f);
            g.DrawLine(whitePen, right, size * 0.62f, left, size * 0.38f);

            g.DrawLine(whitePen, left, size * 0.65f, midX, top);
            g.DrawLine(whitePen, midX, top, right, size * 0.38f);
            g.DrawLine(whitePen, right, size * 0.38f, left, size * 0.62f);
        }

        if (hasAlert)
        {
            float badgeSize = size * 0.45f;
            float badgeX = size - badgeSize;
            float badgeY = size - badgeSize;

            using SolidBrush badgeBg = new(Color.FromArgb(255, 193, 7));
            g.FillEllipse(badgeBg, badgeX, badgeY, badgeSize, badgeSize);

            using Pen darkEdge = new(Color.Black, size * 0.02f);
            g.DrawEllipse(darkEdge, badgeX, badgeY, badgeSize, badgeSize);

            using StringFormat sf = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using Font f = new("Tahoma", badgeSize * 0.75f, FontStyle.Bold);
            g.DrawString("!", f, Brushes.Black, new RectangleF(badgeX, badgeY + (size * 0.05f), badgeSize, badgeSize), sf);
        }

        Icon? oldIcon = _trayIcon.Icon;
        _trayIcon.Icon = Icon.FromHandle(bmp.GetHicon());

        if (oldIcon != null)
        {
            NativeMethods.DestroyIcon(oldIcon.Handle);
            oldIcon.Dispose();
        }
    }

    public void Run() => Application.Run();

    public Task StartBackgroundScanAsync() => RunStartupScanAsync();

    public void StartBackgroundScan() =>
        FireAndForget(RunStartupScanAsync(), "Startup scan");

    private void PostToUi(Action action)
    {
        if (_disposed) return;
        _uiContext.Post(_ =>
        {
            if (_disposed) return;
            try { action(); }
            catch (Exception ex) { Debug.WriteLine($"[TrayApp] UI dispatch fault: {ex}"); }
        }, null);
    }

    private void FireAndForget(Task task, string operationName)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
                Debug.WriteLine($"[TrayApp] {operationName} fault: {t.Exception}");
        }, TaskScheduler.Default);
    }

    private async Task RunUiActionAsync(Func<Task> action, string operationName)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) { Debug.WriteLine($"[TrayApp] {operationName} fault: {ex}"); }
    }
}
