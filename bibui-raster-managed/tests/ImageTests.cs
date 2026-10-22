// Images drawn 1:1 (Canvas.DrawImageAligned) against the definition: the same
// image drawn as a rect filled by a shader that samples the pixel under each
// pixel centre, nearest, through the same clip (rects, rounded rects: a mask),
// layer and alpha. On the pixel grid the two must agree byte for byte; off it,
// DrawImageAligned must refuse and draw nothing.
using System.Numerics;
using Bibui.Raster.Managed;

static class ImageTests
{
    static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    /// <summary>Nearest sampling of premultiplied pixels into a rect: the reference.</summary>
    sealed class Nearest(uint[] px, int w, int h, Rect dest, bool opaque) : Shader
    {
        Matrix3x2 _toImage;
        public override bool IsOpaque => opaque;
        public override void Begin(in Matrix3x2 localToDevice)
        {
            Matrix3x2.Invert(localToDevice, out var inv);
            _toImage = inv * Matrix3x2.CreateTranslation(-dest.Left, -dest.Top) * Matrix3x2.CreateScale(w / dest.Width, h / dest.Height);
        }
        public override void ShadeSpan(int x, int y, Span<uint> dst)
        {
            for (int i = 0; i < dst.Length; i++)
            {
                var p = Vector2.Transform(new Vector2(x + i + 0.5f, y + 0.5f), _toImage);
                int sx = Math.Clamp((int)MathF.Floor(p.X), 0, w - 1), sy = Math.Clamp((int)MathF.Floor(p.Y), 0, h - 1);
                dst[i] = px[sy * w + sx];
            }
        }
    }

    static uint[] Picture(int w, int h, bool opaque)
    {
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            uint a = opaque ? 255u : (uint)(40 + (x * 7 + y * 3) % 216);
            if (!opaque && (x + y) % 11 == 0) a = 0;
            uint r = (uint)(x * 255 / w), g = (uint)(y * 255 / h), b = (uint)((x ^ y) & 0xFF);
            px[y * w + x] = PixelMath.Premul(a << 24 | r << 16 | g << 8 | b);
        }
        return px;
    }

    static (Surface, Canvas) Ground()
    {
        var s = new Surface(96, 80);
        var c = new Canvas(s);
        // A ground with detail, so a blend shows.
        c.Clear(0xFF203040);
        c.DrawRect(new Rect(10, 10, 60, 50), Paint.Fill(0x80FF8000));
        return (s, c);
    }

    static uint[] Pixels(Surface s)
    {
        var px = new uint[s.Width * s.Height];
        s.CopyTo(px);
        return px;
    }

    /// <summary>Draws the picture both ways under the same setup; both surfaces' pixels.</summary>
    static (uint[] Fast, uint[] Reference, bool Drew) Both(bool opaque, Rect dest, byte alpha, Action<Canvas> setup, Action<Canvas>? after = null, int w = 40, int h = 30)
    {
        var px = Picture(w, h, opaque);
        var (sf, cf) = Ground();
        setup(cf);
        bool drew = cf.DrawImageAligned(px, w, h, dest, alpha, opaque);
        after?.Invoke(cf);
        var (sr, cr) = Ground();
        setup(cr);
        cr.DrawRect(dest, new Paint { Color = (uint)alpha << 24 | 0xFFFFFF, Shader = new Nearest(px, w, h, dest, opaque) });
        after?.Invoke(cr);
        return (Pixels(sf), Pixels(sr), drew);
    }

    static void Same(string what, (uint[] Fast, uint[] Reference, bool Drew) r)
    {
        Assert(r.Drew, $"{what}: not drawn 1:1");
        for (int i = 0; i < r.Fast.Length; i++)
            if (r.Fast[i] != r.Reference[i])
                throw new Exception($"{what}: pixel ({i % 96},{i / 96}) is {r.Fast[i]:X8}, the shader drew {r.Reference[i]:X8}");
    }

    public static void AlignedImageEqualsTheShaderOpaqueAndTranslucent()
    {
        foreach (bool opaque in new[] { true, false })
        foreach (byte alpha in new byte[] { 255, 200, 1 })
            Same($"opaque {opaque}, alpha {alpha}", Both(opaque, new Rect(7, 5, 47, 35), alpha, _ => { }));
    }

    public static void AlignedImageIsClipped()
    {
        foreach (bool opaque in new[] { true, false })
        {
            Same($"clip rect, opaque {opaque}", Both(opaque, new Rect(7, 5, 47, 35), 255, c => c.ClipRect(new Rect(20, 0, 30, 70))));
            // Off the surface on every side.
            Same($"past the edges, opaque {opaque}", Both(opaque, new Rect(-20, -10, 20, 20), 255, _ => { }));
            Same($"past the far edges, opaque {opaque}", Both(opaque, new Rect(70, 60, 110, 90), 255, _ => { }));
        }
    }

    public static void AlignedImageGoesThroughARoundedClipsMask()
    {
        foreach (bool opaque in new[] { true, false })
        foreach (byte alpha in new byte[] { 255, 128 })
        {
            Same($"rrect clip, opaque {opaque}, alpha {alpha}",
                Both(opaque, new Rect(7, 5, 47, 35), alpha, c => c.ClipRRect(new RRect(new Rect(9.5f, 7.25f, 44, 33), 9f, 9f))));
            Same($"antialiased rect clip, opaque {opaque}, alpha {alpha}",
                Both(opaque, new Rect(7, 5, 47, 35), alpha, c => c.ClipRect(new Rect(12.4f, 8.6f, 30.3f, 31.7f), antiAlias: true)));
        }
    }

    public static void AlignedImageDrawsIntoLayers()
    {
        foreach (bool opaque in new[] { true, false })
            Same($"opacity layer, opaque {opaque}", Both(opaque, new Rect(7, 5, 47, 35), 255,
                c => c.SaveLayer((byte)150), c => c.Restore()));
    }

    public static void AlignedImageUnderAWholePixelTransform()
    {
        // A translation, and a scale that the dest undoes: 1:1 in device px.
        Same("translated", Both(true, new Rect(0, 0, 40, 30), 255, c => c.Translate(13, 9)));
        Same("scaled 1.25", Both(false, new Rect(8, 4, 40, 28), 255, c => c.Scale(1.25f, 1.25f)));
    }

    public static void OffTheGridIsRefusedAndDrawsNothing()
    {
        var px = Picture(40, 30, true);
        Rect[] off = [new Rect(7.5f, 5, 47.5f, 35), new Rect(7, 5.25f, 47, 35.25f), new Rect(7, 5, 48, 35), new Rect(47, 5, 7, 35)];
        foreach (var dest in off)
        {
            var (s, c) = Ground();
            var before = Pixels(s);
            Assert(!c.DrawImageAligned(px, 40, 30, dest, 255, true), $"{dest} was drawn 1:1");
            Assert(Pixels(s).AsSpan().SequenceEqual(before), $"{dest}: refused, yet drew");
        }
        // A rotation and a flip.
        var (s2, c2) = Ground();
        c2.Concat(Matrix3x2.CreateRotation(0.3f));
        Assert(!c2.DrawImageAligned(px, 40, 30, new Rect(7, 5, 47, 35), 255, true), "rotated was drawn 1:1");
        var (s3, c3) = Ground();
        c3.Scale(-1, 1);
        Assert(!c3.DrawImageAligned(px, 40, 30, new Rect(-47, 5, -7, 35), 255, true), "flipped was drawn 1:1");
        // RGBA surfaces are the shader's.
        var rgba = new Canvas(new Surface(64, 64, PixelOrder.Rgba8888));
        Assert(!rgba.DrawImageAligned(px, 40, 30, new Rect(7, 5, 47, 35), 255, true), "an RGBA surface was drawn 1:1");
    }

    public static void ANearlyWholeRectIsTakenAsWhole()
    {
        // Laid out in logical px and scaled by 1.25, a rect lands a few millionths off the grid.
        float k = 1.25f;
        var dest = new Rect(7.2f, 4.8f, 7.2f + 40 / k, 4.8f + 30 / k);
        var px = Picture(40, 30, false);
        var (sa, ca) = Ground();
        ca.Scale(k, k);
        Assert(ca.DrawImageAligned(px, 40, 30, dest, 255, false), "a rect within the tolerance was refused");
        var (sb, cb) = Ground();
        Assert(cb.DrawImageAligned(px, 40, 30, new Rect(9, 6, 49, 36), 255, false), "the whole-pixel rect was refused");
        Assert(Pixels(sa).AsSpan().SequenceEqual(Pixels(sb)), "drawn at the nearest whole pixel");
    }
}
