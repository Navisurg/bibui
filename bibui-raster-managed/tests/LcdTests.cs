// Subpixel (LCD) glyph masks: Canvas.DrawLcd blends a coverage per channel.
using Bibui.Raster.Managed;

static class LcdTests
{
    const uint White = 0xFFFFFFFF, Black = 0xFF000000;

    static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    static byte[] Lcd(params (byte R, byte G, byte B)[] px)
    {
        var cov = new byte[px.Length * 3];
        for (int i = 0; i < px.Length; i++) (cov[3 * i], cov[3 * i + 1], cov[3 * i + 2]) = px[i];
        return cov;
    }

    public static void LcdBlendsEachChannelByItsOwnCoverage()
    {
        var s = new Surface(4, 1);
        var c = new Canvas(s);
        c.Clear(Black);
        c.DrawLcd(Lcd((255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 128, 0)), 4, 1, 0, 0, White);
        Assert(s.GetPixelArgb(0, 0) == 0xFFFF0000, $"red stripe only, got {s.GetPixelArgb(0, 0):X8}");
        Assert(s.GetPixelArgb(1, 0) == 0xFF00FF00, $"green stripe only, got {s.GetPixelArgb(1, 0):X8}");
        Assert(s.GetPixelArgb(2, 0) == 0xFF0000FF, $"blue stripe only, got {s.GetPixelArgb(2, 0):X8}");
        Assert(s.GetPixelArgb(3, 0) == 0xFFFF8000, $"mixed, got {s.GetPixelArgb(3, 0):X8}");
    }

    public static void LcdWithEqualCoveragesMatchesA8()
    {
        // Three equal coverages are a greyscale mask, and blend to the byte as DrawA8 does.
        var a = new Surface(256, 1);
        var b = new Surface(256, 1);
        var ca = new Canvas(a);
        var cb = new Canvas(b);
        ca.Clear(0xFF203040);
        cb.Clear(0xFF203040);
        var a8 = new byte[256];
        var lcd = new byte[256 * 3];
        for (int i = 0; i < 256; i++) a8[i] = lcd[3 * i] = lcd[3 * i + 1] = lcd[3 * i + 2] = (byte)i;
        ca.DrawA8(a8, 256, 1, 0, 0, 0xFFD0C0B0);
        cb.DrawLcd(lcd, 256, 1, 0, 0, 0xFFD0C0B0);
        for (int x = 0; x < 256; x++)
        {
            uint p = a.GetPixelArgb(x, 0), q = b.GetPixelArgb(x, 0);
            Assert(p == q, $"coverage {x}: A8 {p:X8}, LCD {q:X8}");
        }
    }

    public static void LcdOverATranslucentPixelIsGreyscale()
    {
        // Fringes over a pixel that is composited again later would tint it: there the mask is its mean.
        var s = new Surface(2, 1);
        var c = new Canvas(s);
        c.Clear(0);
        c.DrawLcd(Lcd((255, 0, 0), (90, 90, 90)), 2, 1, 0, 0, White);
        Assert(s.GetPixelArgb(0, 0) == 0x55555555, $"mean of 255, 0, 0 is 85 in every channel, got {s.GetPixelArgb(0, 0):X8}");
        Assert(s.GetPixelArgb(1, 0) == 0x5A5A5A5A, $"equal coverages stay as they are, got {s.GetPixelArgb(1, 0):X8}");
    }

    public static void LcdRespectsRgbaOrder()
    {
        var s = new Surface(1, 1, PixelOrder.Rgba8888);
        var c = new Canvas(s);
        c.Clear(Black);
        c.DrawLcd(Lcd((255, 0, 0)), 1, 1, 0, 0, White);
        Assert(s.GetPixelArgb(0, 0) == 0xFFFF0000, $"red coverage lands in red, got {s.GetPixelArgb(0, 0):X8}");
    }

    public static void LcdIsClipped()
    {
        var s = new Surface(4, 1);
        var c = new Canvas(s);
        c.Clear(Black);
        c.Save();
        c.ClipRect(new Rect(1, 0, 3, 1));
        c.DrawLcd(Lcd((255, 255, 255), (255, 255, 255), (255, 255, 255), (255, 255, 255)), 4, 1, 0, 0, White);
        c.Restore();
        Assert(s.GetPixelArgb(0, 0) == Black && s.GetPixelArgb(3, 0) == Black, "outside the clip is untouched");
        Assert(s.GetPixelArgb(1, 0) == White && s.GetPixelArgb(2, 0) == White, "inside the clip is drawn");
    }
}
