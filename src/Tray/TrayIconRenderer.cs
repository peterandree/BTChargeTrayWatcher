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

        Color batteryBlue = Color.FromArgb(0, 120, 215);

        using (SolidBrush bgBrush = new(batteryBlue))
            g.FillEllipse(bgBrush, 4, 4, size - 8, size - 8);

        RectangleF body = new(size * 0.24f, size * 0.34f, size * 0.52f, size * 0.34f);
        RectangleF terminal = new(size * 0.76f, size * 0.43f, size * 0.08f, size * 0.16f);

        using (Pen borderPen = new(Color.White, size * 0.05f))
        {
            borderPen.LineJoin = LineJoin.Round;
            g.DrawRectangle(borderPen, body.X, body.Y, body.Width, body.Height);
            g.DrawRectangle(borderPen, terminal.X, terminal.Y, terminal.Width, terminal.Height);
        }

        RectangleF charge = new(
            body.X + size * 0.04f,
            body.Y + size * 0.04f,
            body.Width * 0.62f,
            body.Height - size * 0.08f);

        using (SolidBrush chargeBrush = new(Color.White))
            g.FillRectangle(chargeBrush, charge);

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
