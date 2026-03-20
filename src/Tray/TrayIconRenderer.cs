// src/Tray/TrayIconRenderer.cs
using System.Drawing;
using System.Drawing.Drawing2D;

namespace BTChargeTrayWatcher;

internal sealed class TrayIconRenderer
{
    public Icon Render(bool hasAlert)
    {
        const int size = 128;
        using Bitmap bmp = new(size, size);
        using Graphics g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Color btBlue = Color.FromArgb(0, 120, 215);

        using (SolidBrush bgBrush = new(btBlue))
            g.FillEllipse(bgBrush, 4, 4, size - 8, size - 8);

        using (Pen whitePen = new(Color.White, size / 10f))
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

            using StringFormat sf = new()
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            using Font f = new("Tahoma", badgeSize * 0.75f, FontStyle.Bold);
            g.DrawString("!", f, Brushes.Black,
                new RectangleF(badgeX, badgeY + (size * 0.05f), badgeSize, badgeSize), sf);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }
}
