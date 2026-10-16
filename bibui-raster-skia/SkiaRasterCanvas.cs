using System.Numerics;
using SkiaSharp;

namespace Bibui.Raster.Skia;

/// <summary>An SKCanvas as a bibui canvas: each call is the one Skia call it names.</summary>
public sealed class SkiaRasterCanvas : IRasterCanvas
{
    private readonly SkiaRasterBackend _backend;
    private readonly SkiaRasterSurface? _surface;

    internal SkiaRasterCanvas(SkiaRasterBackend backend, SKCanvas native, SkiaRasterSurface? surface)
    {
        _backend = backend;
        Native = native;
        _surface = surface;
    }

    /// <summary>
    /// The Skia canvas. The Skia backend's own escape hatch — what
    /// <c>Box.CustomDraw</c> is handed; bibui core never reaches for it.
    /// </summary>
    public SKCanvas Native { get; }

    public void Clear(Rgba color) => Native.Clear(color.ToSk());

    // ── State ────────────────────────────────────────────────────────────────

    public void Save() => Native.Save();
    public void Restore() => Native.Restore();
    public void Translate(float dx, float dy) => Native.Translate(dx, dy);
    public void Scale(float sx, float sy) => Native.Scale(sx, sy);

    public Matrix3x2 TotalMatrix => Native.TotalMatrix.ToMatrix3x2();

    public void SetMatrix(in Matrix3x2 matrix) => Native.SetMatrix(matrix.ToSk());

    public void ClipRect(RectF rect) => Native.ClipRect(rect.ToSk());

    public void SaveLayerAlpha(byte alpha)
        => Native.SaveLayer(_backend.Paint(new PaintSpec { Color = new Rgba(0, 0, 0, alpha) }));

    // ── Shapes ───────────────────────────────────────────────────────────────

    public void DrawRoundRect(RectF rect, float rx, float ry, in RasterPaint paint)
        => Native.DrawRoundRect(rect.ToSk(), rx, ry, _backend.Paint(paint));

    public void DrawLine(float x0, float y0, float x1, float y1, in RasterPaint paint)
        => Native.DrawLine(x0, y0, x1, y1, _backend.Paint(paint));

    public void DrawPath(RasterPath path, in RasterPaint paint)
        => Native.DrawPath(Path(path), _backend.Paint(paint));

    public void DrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, in RasterPaint paint)
        => Native.DrawPoints(mode.ToSk(), points.AsSk().ToArray(), _backend.Paint(paint));

    // ── Images ───────────────────────────────────────────────────────────────

    public void DrawTexture(TextureHandle texture, RectF dest, in RasterPaint paint)
    {
        if (_backend.Texture(texture) is { } image) Native.DrawImage(image, dest.ToSk(), _backend.Paint(paint));
    }

    public void DrawBackdropBlur(PixelRect region, RectF clip, float sigma)
    {
        // Only a canvas over a surface has painted pixels to read back.
        if (_surface is null) return;

        using var snapshot = _surface.Native.Snapshot(region.ToSk());
        if (snapshot is null) return;

        // Opaque black, not default: DrawImage modulates by the paint's alpha,
        // and default(Rgba) is transparent.
        var paint = _backend.Paint(new RasterPaint(new PaintSpec { Color = Rgba.Black, IsAntialias = true })
        {
            ImageBlurSigma = sigma,
        });

        Native.Save();
        Native.ClipRect(clip.ToSk());
        Native.DrawImage(snapshot, new SKRect(region.Left, region.Top, region.Right, region.Bottom), paint);
        Native.Restore();
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    public void DrawGlyphs(FontFace face, float sizePx, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions,
        float x, float baselineY, Rgba color)
    {
        if (glyphs.Length != positions.Length)
            throw new ArgumentException("One position per glyph.", nameof(positions));
        if (glyphs.IsEmpty) return;

        var sk = color.ToSk();
        var paint = _backend.TextPaint(face, sizePx, sk);
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(glyphs, _backend.TextFont(face, sizePx, sk, paint), positions.AsSk());
        using var blob = builder.Build();
        if (blob is not null) Native.DrawText(blob, x, baselineY, paint);
    }

    private static SKPath Path(RasterPath path)
        => (path as SkiaPath ?? throw new ArgumentException(
            $"A {path.GetType().Name} is not a Skia path; it came from another raster backend.", nameof(path))).Native;
}
