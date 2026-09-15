using System.Drawing.Drawing2D;

namespace NetPaw.Tray;

/// <summary>Tray icons are drawn at runtime (a paw tinted by adapter state) so the repo ships no binary assets.</summary>
static class Icons
{
    static readonly Dictionary<(Color, Color?), Icon> Cache = [];

    /// <param name="dot">Optional status dot (bottom-right) — red while monitor mode sees a problem.</param>
    public static Icon Paw(Color tint, Color? dot = null)
    {
        if (Cache.TryGetValue((tint, dot), out var cached)) return cached;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(tint);
            // main pad
            g.FillEllipse(b, 7, 15, 18, 14);
            // toes
            g.FillEllipse(b, 3, 8, 7, 8);
            g.FillEllipse(b, 10, 2, 6, 8);
            g.FillEllipse(b, 17, 2, 6, 8);
            g.FillEllipse(b, 23, 8, 7, 8);
            if (dot is { } d)
            {
                using var ring = new SolidBrush(Color.FromArgb(24, 26, 32)); using var db = new SolidBrush(d);
                g.FillEllipse(ring, 18, 18, 14, 14); g.FillEllipse(db, 20, 20, 10, 10);
            }
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        Cache[(tint, dot)] = icon;
        return icon;
    }

    public static Bitmap Dot(Color c, int size = 10)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(c);
        g.FillEllipse(b, 0, 0, size - 1, size - 1);
        return bmp;
    }
}
