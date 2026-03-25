// src/Tray/TrayIconRenderer.cs
using Svg;

namespace BTChargeTrayWatcher;

internal sealed class TrayIconRenderer : IDisposable
{
    private const int RenderSize = 64;

    private readonly Icon _normal;
    private readonly Icon _alert;

    public TrayIconRenderer()
    {
        _normal = RenderSvg(TrayIcons.Normal);
        _alert  = RenderSvg(TrayIcons.Alert);
    }

    /// <summary>Returns a clone of the cached icon. The caller is responsible for disposing it.</summary>
    public Icon Render(bool hasAlert) =>
        (Icon)(hasAlert ? _alert : _normal).Clone();

    private static Icon RenderSvg(string svgContent)
    {
        var doc = SvgDocument.FromSvg<SvgDocument>(svgContent);
        using var bmp = doc.Draw(RenderSize, RenderSize);
        IntPtr hicon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hicon).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hicon);
        }
    }

    public void Dispose()
    {
        _normal.Dispose();
        _alert.Dispose();
    }
}
