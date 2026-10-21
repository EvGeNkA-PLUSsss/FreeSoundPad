using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FreeSoundPad.App.Services;

public static class AppIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon CreateTrayIcon()
    {
        using var bitmap = DrawIcon(64);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static Bitmap DrawIcon(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var pad = Math.Max(2, size / 10);
        using var background = new SolidBrush(Color.FromArgb(255, 9, 13, 20));
        using var border = new Pen(Color.FromArgb(90, 63, 224, 176), Math.Max(1f, size / 32f));
        var rect = new Rectangle(pad, pad, size - pad * 2, size - pad * 2);
        using var path = RoundedRect(rect, size / 5f);
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);

        var barHeights = new[] { 0.22f, 0.42f, 0.68f, 0.48f, 0.30f };
        var barWidth = size / 12f;
        var gap = size / 24f;
        var totalWidth = barHeights.Length * barWidth + (barHeights.Length - 1) * gap;
        var startX = (size - totalWidth) / 2f;
        using var barBrush = new SolidBrush(Color.FromArgb(255, 63, 224, 176));

        for (var i = 0; i < barHeights.Length; i++)
        {
            var height = size * barHeights[i];
            var x = startX + i * (barWidth + gap);
            var y = (size - height) / 2f;
            using var barPath = RoundedRect(new RectangleF(x, y, barWidth, height), barWidth / 2f);
            graphics.FillPath(barBrush, barPath);
        }

        return bitmap;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
