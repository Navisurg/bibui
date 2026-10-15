using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Bibui.Theming;
using SkiaSharp;

namespace Bibui.Raster.Skia;

/// <summary>
/// Rendering onto surfaces made with SkiaSharp — a GPU surface over a window's
/// framebuffer, an offscreen texture. These were instance methods of
/// <see cref="BibuiRenderer"/> until the Skia backend left the core library;
/// as extensions they keep their call syntax for code that says
/// <c>using Bibui.Raster.Skia;</c>.
/// </summary>
public static class SkiaRendering
{
    /// <summary>
    /// <paramref name="target"/> as a render target of <paramref name="renderer"/>'s
    /// Skia backend. Wrapping the same surface again returns the same wrapper, so
    /// a per-frame call allocates nothing.
    /// </summary>
    public static IRasterSurface SkiaSurface(this BibuiRenderer renderer, SKSurface target) =>
        Backend(renderer.Raster).Wrap(target);

    /// <summary>
    /// <see cref="BibuiRenderer.RenderTo(IRasterSurface, UiNode, SurfaceOptions, ThemeTokens, FrameInput?)"/>
    /// onto a surface made with SkiaSharp. Only on the Skia backend: the surface is Skia's.
    /// </summary>
    public static void RenderTo(this BibuiRenderer renderer, SKSurface target, UiNode root,
        SurfaceOptions surface, ThemeTokens theme, FrameInput? input = null) =>
        renderer.RenderTo(renderer.SkiaSurface(target), root, surface, theme, input);

    /// <summary>
    /// <paramref name="raster"/> as the Skia backend, or an exception saying an
    /// SKSurface needs one.
    /// </summary>
    public static SkiaRasterBackend Backend(IRasterBackend raster) =>
        raster as SkiaRasterBackend ?? throw new InvalidOperationException(
            $"An SKSurface can only be rendered to by the Skia raster backend; this renderer uses '{raster.Name}'.");
}

/// <summary>
/// Raw SkiaSharp drawing inside a box: what <c>Box.CustomDraw</c> was before
/// the Skia backend left the core library. It runs only where Skia
/// paints; on any other backend the box draws its style and nothing more.
/// Prefer a plain <see cref="DrawCallback"/>, whose context speaks the backend's
/// own vocabulary and so draws on every backend.
/// <code>new Box { Draw = SkiaCustomDraw.Of((canvas, rect) => …) }</code>
/// </summary>
public static class SkiaCustomDraw
{
    /// <summary>A <see cref="Box.Draw"/> hook handing <paramref name="draw"/> the Skia canvas and the box's rect in physical pixels.</summary>
    public static DrawCallback Of(Action<SKCanvas, SKRect> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        return ctx =>
        {
            if (ctx.Canvas is SkiaRasterCanvas skia) draw(skia.Native, ctx.Rect.ToSk());
        };
    }
}
