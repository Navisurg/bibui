// Canvas.DrawSubpixel: a subpixel glyph mask, three samples a pixel left to right. The
// reference is DrawA8: each colour channel blends as an A8 coverage of its own
// stripe's sample would (red, green, blue left to right), alpha as the largest.
using Bibui.Raster.Managed;

static class SubpixelTests
{
    static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    // Random masks, colours, coverage tables and pixel orders, placed partly off
    // the surface so clipped rows and columns are read from the right samples.
    public static void EachChannelBlendsAsA8OfItsStripe()
    {
        var rng = new Random(7);
        var map = new byte[256];
        for (int i = 0; i < 256; i++) map[i] = (byte)Math.Min(255, i * 5 / 4);
        const int W = 6, H = 4;
        for (int trial = 0; trial < 300; trial++)
        {
            int w = 1 + rng.Next(9), h = 1 + rng.Next(5), x = rng.Next(-3, W - 1), y = rng.Next(-2, H - 1);
            var mask = new byte[3 * w * h];
            rng.NextBytes(mask);
            uint dst = 0xFF000000 | (uint)rng.Next(0x1000000);
            uint color = (uint)rng.Next(1, 256) << 24 | (uint)rng.Next(0x1000000);
            bool mapped = rng.Next(2) == 1;
            var order = rng.Next(2) == 1 ? PixelOrder.Rgba8888 : PixelOrder.Bgra8888;

            // The A8 masks each channel is drawn as: the red stripe's samples,
            // the green's, the blue's, and the largest for alpha.
            var red = new byte[w * h]; var green = new byte[w * h]; var blue = new byte[w * h]; var most = new byte[w * h];
            for (int i = 0; i < w * h; i++)
            {
                byte left = mask[3 * i], mid = mask[3 * i + 1], right = mask[3 * i + 2];
                (red[i], green[i], blue[i]) = (left, mid, right);
                most[i] = Math.Max(left, Math.Max(mid, right));
            }
            uint[] A8(byte[] a8)
            {
                var s = new Surface(W, H, order);
                var c = new Canvas(s);
                c.Clear(dst);
                c.DrawA8(a8, w, h, x, y, color, mapped ? map : default);
                var px = new uint[W * H];
                for (int i = 0; i < px.Length; i++) px[i] = s.GetPixelArgb(i % W, i / W);
                return px;
            }
            uint[] r = A8(red), g = A8(green), b = A8(blue), a = A8(most);

            var got = new Surface(W, H, order);
            var gc = new Canvas(got);
            gc.Clear(dst);
            gc.DrawSubpixel(mask, w, h, x, y, color, mapped ? map : default);
            for (int i = 0; i < W * H; i++)
            {
                uint want = a[i] & 0xFF000000 | r[i] & 0xFF0000 | g[i] & 0xFF00 | b[i] & 0xFF;
                uint px = got.GetPixelArgb(i % W, i / W);
                Assert(px == want, $"trial {trial} ({i % W},{i / W}), {order}: got {px:X8}, want {want:X8}");
            }
        }
    }

    // Over a pixel that is not opaque the samples' mean is drawn: what DrawA8
    // draws with that coverage, and no colour.
    public static void TranslucentTargetGetsTheMean()
    {
        byte[] mask = [240, 90, 30];
        byte[] mean = [(byte)((240 + 90 + 30 + 1) / 3)];
        var want = new Surface(1, 1);
        var wc = new Canvas(want);
        wc.Clear(0x80202020);
        wc.DrawA8(mean, 1, 1, 0, 0, 0xFFFFFFFF);
        var got = new Surface(1, 1);
        var gc = new Canvas(got);
        gc.Clear(0x80202020);
        gc.DrawSubpixel(mask, 1, 1, 0, 0, 0xFFFFFFFF);
        Assert(want.GetPixel(0, 0) == got.GetPixel(0, 0), $"got {got.GetPixel(0, 0):X8}, want {want.GetPixel(0, 0):X8}");
    }
}
