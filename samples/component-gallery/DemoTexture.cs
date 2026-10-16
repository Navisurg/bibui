using Bibui;
using Bibui.Raster;
using Bibui.Raster.Managed.Backend;
using Bibui.Raster.Skia;
using Bibui.Runtime;

/// <summary>
/// Textures: pixels the gallery makes itself, drawn by bibui as a handle. bibui
/// has no image component; an app that shows an image knows which backend it
/// chose, gets the pixels into memory its own way (decoded with
/// <c>Bibui.Imaging.Png</c>, from a video decoder, or made, as here), and makes
/// a texture there. It owns the texture: this page keeps its two for the life
/// of the app.
/// </summary>
internal static class DemoTexture
{
    private const int Size = 32;

    private static TextureHandle s_still, s_moving;
    private static readonly byte[] s_pixels = new byte[Size * Size * 4];
    private static float s_time;

    internal static UiNode Build(BibuiRuntime runtime)
    {
        if (s_still.IsNone)
        {
            Checker(s_pixels);
            s_still = Create(runtime, s_pixels);
            Sweep(s_pixels, 0f);
            s_moving = Create(runtime, s_pixels);
        }

        // The same handle every frame, its pixels rewritten: a video frame's path.
        s_time += runtime.Dt;
        Sweep(s_pixels, s_time);
        Update(runtime, s_moving, s_pixels);
        runtime.RequestFrame();

        return Arrange.Column(
            Bold(runtime, "Made once, drawn scaled up", ComponentSize.Sm, TextColor.Secondary),
            Arrange.Space(10f),
            Picture(s_still),
            Arrange.Space(22f),
            Bold(runtime, "One texture, its pixels updated every frame", ComponentSize.Sm, TextColor.Secondary),
            Arrange.Space(10f),
            Picture(s_moving));
    }

    // Nearest sampling: each texel stays a sharp square at 6x.
    private static Box Picture(TextureHandle texture) => new Box
    {
        Draw = (_, ctx) => ctx.DrawTexture(texture, ctx.Rect,
            new Paint { Fill = Rgba.Black, Filter = FilterQuality.None, Antialias = false }),
    }.Px(Size * 6f, Size * 6f);

    // The gallery chose its backend, so it knows where its textures live.
    private static TextureHandle Create(BibuiRuntime runtime, byte[] rgba) => runtime.Raster switch
    {
        SkiaRasterBackend skia => skia.CreateTexture(rgba, Size, Size),
        ManagedRasterBackend managed => managed.CreateTexture(rgba, Size, Size),
        _ => TextureHandle.None,
    };

    private static void Update(BibuiRuntime runtime, TextureHandle texture, byte[] rgba)
    {
        switch (runtime.Raster)
        {
            case SkiaRasterBackend skia: skia.UpdateTexture(texture, rgba, Size, Size); break;
            case ManagedRasterBackend managed: managed.UpdateTexture(texture, rgba, Size, Size); break;
        }
    }

    private static void Checker(byte[] px)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                bool dark = ((x / 4) + (y / 4)) % 2 == 0;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = dark ? ((byte)37, (byte)99, (byte)235, (byte)255) : ((byte)226, (byte)232, (byte)240, (byte)255);
            }
    }

    // A bright band sweeping across a dark field.
    private static void Sweep(byte[] px, float time)
    {
        float band = (time * 12f + Size / 2f) % (Size + 16) - 4;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                float glow = MathF.Max(0f, 1f - MathF.Abs(x + y * 0.5f - band) / 4f);
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = ((byte)(20 + 200 * glow), (byte)(24 + 150 * glow), (byte)(48 + 60 * glow), (byte)255);
            }
    }
}
