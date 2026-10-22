using System.Numerics;
using Bibui.Core;
using Bibui.Core.Draw;
using Surface = Bibui.Raster.Managed.Surface;
using Bibui.Raster.Managed.Backend;
using Bibui.Core.Runtime;

/// <summary>
/// Images drawn 1:1 on the pixel grid (<c>Canvas.DrawImageAligned</c>) against the image
/// shader they stand in for: the same scene drawn by a renderer with
/// <see cref="ManagedRenderer.ImageFastPaths"/> off — every image through the shader, as
/// before the fast paths — and one with them on must agree byte for byte, frame after frame.
/// </summary>
internal static partial class DamageOracle
{
    /// <summary>What a run of <see cref="CompareFast"/> found.</summary>
    internal sealed record FastResult(int Frames, int Mismatches, int Aligned, string? FirstMismatch);

    /// <summary>
    /// <paramref name="scene"/> drawn whole every frame by two renderers, the image fast
    /// paths off and on, into a surface each; how many frames differ, and how many images
    /// the fast one drew 1:1.
    /// </summary>
    /// <param name="rgba">RGBA surfaces, where nothing is drawn 1:1 by the canvas and the
    /// image shader reads whole-pixel offsets as rows (and opaque images as copies) itself.</param>
    internal static FastResult CompareFast(Scene scene, bool transparent = false, bool rgba = false)
    {
        var shaded = new ManagedRenderer { ImageFastPaths = false };
        var fast = new ManagedRenderer();
        var a = Make(scene, shaded, out var stepA, transparent);
        var b = Make(scene, fast, out var stepB, transparent);
        int w = SurfaceOptions.PhysicalSize(a.SurfaceWidth, a.ScaleFactor), h = SurfaceOptions.PhysicalSize(a.SurfaceHeight, a.ScaleFactor);
        var order = rgba ? Bibui.Raster.Managed.PixelOrder.Rgba8888 : Bibui.Raster.Managed.PixelOrder.Bgra8888;
        var sa = new Surface(w, h, order);
        var sb = new Surface(w, h, order);
        var pa = new uint[w * h];
        var pb = new uint[w * h];
        int mismatches = 0;
        string? first = null;
        for (int f = 0; f < scene.Frames; f++)
        {
            stepA(f);
            stepB(f);
            a.Step();
            shaded.Draw(a.DrawList, sa);
            b.Step();
            fast.Draw(b.DrawList, sb);
            sa.CopyTo(pa);
            sb.CopyTo(pb);
            if (!pa.AsSpan().SequenceEqual(pb) && mismatches++ == 0) first = $"frame {f}: {Describe(pa, pb, w, h)}";
        }
        if (shaded.AlignedImageDraws != 0) throw new Exception("the renderer with the fast paths off drew an image 1:1");
        return new FastResult(scene.Frames, mismatches, fast.AlignedImageDraws, first);
    }

    /// <summary>A <paramref name="n"/>-pixel square of colour, straight RGBA: a photo-like gradient, opaque.</summary>
    private static byte[] Photo(int n, int seed)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = (y * n + x) * 4;
            px[i] = (byte)(x * 255 / n + seed * 40);
            px[i + 1] = (byte)(y * 255 / n + seed * 17);
            px[i + 2] = (byte)((x ^ y) * 7 + seed * 90);
            px[i + 3] = 255;
        }
        return px;
    }

    /// <summary>
    /// A picture with its corners cut round in its own pixels, as an app rounds a
    /// thumbnail, and a soft alpha ramp across it: translucent, straight RGBA.
    /// </summary>
    private static byte[] Rounded(int n, int radius)
    {
        var px = Photo(n, 3);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float cx = Math.Clamp(x + 0.5f, radius, n - radius), cy = Math.Clamp(y + 0.5f, radius, n - radius);
            float d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            float cover = Math.Clamp(radius + 0.5f - d, 0f, 1f);
            px[(y * n + x) * 4 + 3] = (byte)(cover * (160 + 95 * x / n));
        }
        return px;
    }

    /// <summary>
    /// Image scenes: a grid of tiles at an output scale of 1.25, each tile's dest exactly
    /// its texture's size in device px (as an app sizes a thumbnail), scrolled a whole
    /// device pixel or three a frame. Opaque and translucent tiles, BGRA and RGBA and R8,
    /// nearest and filtered, at full and half paint alpha, under a clip that cuts tiles,
    /// in an opacity group, and a lookup. <paramref name="fractional"/> adds half a
    /// device pixel to every position and stretches every other tile by one: nothing
    /// lands 1:1, and the fast path must stand aside.
    /// </summary>
    internal static Scene ImageGrid(string name, bool fractional, int frames = 40) =>
        new(name, 640, 400, 1.25f, frames, (rt, renderer) =>
        {
            int frame = 0;
            const int Tile = 48;
            TextureHandle opaque = default, bgra = default, round = default, grey = default, table = default;
            UiNode Build()
            {
                var canvas = new Box
                {
                    Draw = (_, ctx) =>
                    {
                        if (opaque.IsNone)
                        {
                            opaque = renderer.CreateTexture(TextureFormat.RGBA8, TextureKind.Static, Tile, Tile, Photo(Tile, 0));
                            var b = Photo(Tile, 1);
                            for (int i = 0; i < b.Length; i += 4) (b[i], b[i + 2]) = (b[i + 2], b[i]);
                            bgra = renderer.CreateTexture(TextureFormat.BGRA8, TextureKind.Static, Tile, Tile, b);
                            round = renderer.CreateTexture(TextureFormat.RGBA8, TextureKind.Static, Tile, Tile, Rounded(Tile, 10));
                            var g = new byte[Tile * Tile];
                            for (int i = 0; i < g.Length; i++) g[i] = (byte)(i * 13);
                            grey = renderer.CreateTexture(TextureFormat.R8, TextureKind.Static, Tile, Tile, g);
                            var t = new byte[256 * 4];
                            for (int i = 0; i < 256; i++) { t[i * 4] = (byte)i; t[i * 4 + 1] = (byte)(255 - i); t[i * 4 + 2] = 90; t[i * 4 + 3] = 255; }
                            table = renderer.CreateTexture(TextureFormat.RGBA8, TextureKind.Static, 256, 1, t);
                        }
                        // Physical px, as ctx draws: whole pixels, scrolled.
                        float off = fractional ? 0.5f : 0f;
                        int scroll = frame * 3 % 60;
                        var plain = Paint.Filled(new Rgba(255, 255, 255));
                        var filtered = plain with { Filter = FilterQuality.Low };
                        var half = Paint.Filled(new Rgba(255, 255, 255, 128));
                        TextureHandle[] kinds = [opaque, bgra, round, grey];
                        for (int r = 0; r < 7; r++)
                        for (int c = 0; c < 9; c++)
                        {
                            int k = (r * 9 + c) % 4;
                            float x = 6 + c * (Tile + 6) + off, y = 6 + r * (Tile + 6) - scroll + off;
                            float stretch = fractional && (r + c) % 2 == 0 ? 1f : 0f;
                            var dest = new RectF(x, y, x + Tile + stretch, y + Tile + stretch);
                            var paint = (r + c) % 5 == 4 ? half : (r + c) % 3 == 1 ? filtered : plain;
                            if (r == 2 && c == 4) ctx.DrawTexture(grey, dest, plain, new TextureLookup(table, TextureChannel.R, TextureChannel.None));
                            else ctx.DrawTexture(kinds[k], dest, paint);
                        }
                        // A clip that cuts the tiles it crosses, on and off the pixel grid.
                        ctx.PushClip(new RectF(530.3f, 20f, 610.6f, 300f));
                        for (int r = 0; r < 4; r++)
                            ctx.DrawTexture(r % 2 == 0 ? opaque : round, new RectF(500 + off, 10 + r * 60 - scroll / 3 + off, 500 + Tile + off, 10 + r * 60 + Tile - scroll / 3 + off), plain);
                        ctx.PopClip();
                        // An opacity group over two tiles.
                        ctx.PushOpacity(0.6f);
                        ctx.DrawTexture(opaque, new RectF(620 + off, 330 + off, 620 + Tile + off, 330 + Tile + off), plain);
                        ctx.DrawTexture(round, new RectF(650 + off, 350 + off, 650 + Tile + off, 350 + Tile + off), plain);
                        ctx.PopOpacity();
                        // Under a whole-pixel translation, and under a scale that undoes the output's.
                        ctx.PushTransform(Matrix3x2.CreateTranslation(300, 420));
                        ctx.DrawTexture(round, new RectF(off, off, Tile + off, Tile + off), plain);
                        ctx.PopTransform();
                        ctx.PushTransform(Matrix3x2.CreateScale(0.5f) * Matrix3x2.CreateTranslation(400, 420));
                        ctx.DrawTexture(opaque, new RectF(off, off, 2 * Tile + off, 2 * Tile + off), plain);
                        ctx.PopTransform();
                    },
                }.Sized(Size.Px(640f), Size.Px(400f));
                return Page(rt, 0f, () => UiNode.PushIntoParent(rt.ParentInScope, canvas));
            }
            return (Build, f => frame = f);
        });
}
