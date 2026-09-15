using System.Drawing.Drawing2D;

namespace NetPaw.Tray;

/// <summary>Tray icons are drawn at runtime (a paw tinted by adapter state) so the repo ships no binary assets.</summary>
static class Icons
{
    static readonly Dictionary<Color, Icon> Cache = [];

    public static Icon Paw(Color tint)
    {
        if (Cache.TryGetValue(tint, out var cached)) return cached;
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
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        Cache[tint] = icon;
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
