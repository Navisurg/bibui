using System.Numerics;
using System.Runtime.InteropServices;
using Bibui.Draw;
using SkiaSharp;

namespace Bibui.Raster.Skia;

/// <summary>
/// Draws a <see cref="DrawList"/> with Skia: onto an <see cref="SKSurface"/> the
/// caller made (a GPU surface over a window's framebuffer, a CPU one), or into
/// CPU memory the caller owns.
///
/// <para>Holds what Skia draws with, built once from what the ops describe and
/// reused while frames keep asking for it: paints by content, text paints and
/// fonts per face and size, a typeface per bibui face, and a native path per kept
/// <see cref="VectorPath"/>. Each is touch-evicted when frames stop drawing it;
/// <see cref="Draw(DrawList, SKSurface)"/> ages them once per frame. Textures are
/// the caller's: made here, drawn by handle, released by the caller.</para>
///
/// <para>Not thread-safe: one thread draws at a time.</para>
/// </summary>
public sealed class SkiaRenderer : IDisposable
{
    // A paint owns the Skia children it was built with (shader, filters, dash),
    // so a child can never be evicted from under a live paint: they go together.
    private readonly ResourceCache<CacheKey, SkiaPaintEntry> _paints = new();
    private readonly ResourceCache<TextPaintKey, SKPaint> _textPaints = new();
    // The font each text paint draws a text blob with.
    private readonly ResourceCache<TextPaintKey, SKFont> _textFonts = new();
    // The native path each kept VectorPath was built into, by its Id.
    private readonly ResourceCache<int, SKPath> _paths = new();
    // A path the list carries in its payload, rebuilt into this per draw.
    private readonly SKPath _scratch = new();

    // Skia's face for each of bibui's, made from its font file once: the glyph
    // ids FreeType resolves belong to that file, so Skia draws the same glyphs.
    // Kept for the renderer's life, like the FontFaces they mirror.
    private readonly Dictionary<int, SKTypeface> _typefaces = [];

    // The surface over the memory Draw(list, pixels) was last handed, so a host
    // that draws into the same few buffers makes no surface per frame.
    private SKSurface? _wrapped;
    private RasterPixels _wrappedPixels;

    // ── Textures ─────────────────────────────────────────────────────────────
    // Made by usage code, which owns them: a draw list only names a handle.

    private readonly TextureTable<SKImage> _textures = new();

    /// <summary>
    /// A texture copied from straight-alpha RGBA8888 pixels (<c>width * 4</c>
    /// bytes a row). The caller owns it: <see cref="ReleaseTexture"/> frees it.
    /// </summary>
    public TextureHandle CreateTexture(ReadOnlySpan<byte> rgba, int width, int height)
        => _textures.Add(Image(rgba, width, height));

    /// <summary>
    /// Replaces the pixels behind <paramref name="texture"/>, at any size. A
    /// released handle is left alone.
    /// </summary>
    public void UpdateTexture(TextureHandle texture, ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (_textures.Get(texture) is null) return;
        _textures.Replace(texture, Image(rgba, width, height))?.Dispose();
    }

    /// <summary>Frees <paramref name="texture"/>; drawing it afterwards draws nothing.</summary>
    public void ReleaseTexture(TextureHandle texture) => _textures.Remove(texture)?.Dispose();

    private static SKImage Image(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        return SKImage.FromPixelCopy(info, rgba, width * 4)
            ?? throw new ArgumentException($"Pixels do not describe a {width}x{height} RGBA image.", nameof(rgba));
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws <paramref name="target"/>'s pixels from <paramref name="list"/>:
    /// BGRA8888 premultiplied memory the caller owns, a <c>wl_shm</c> buffer or a
    /// test's array, on the CPU.
    /// </summary>
    public void Draw(DrawList list, RasterPixels target)
    {
        if (_wrapped is null || _wrappedPixels != target)
        {
            _wrapped?.Dispose();
            var info = new SKImageInfo(target.Width, target.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            _wrapped = SKSurface.Create(info, target.Address, target.RowBytes)
                ?? throw new InvalidOperationException(
                    $"SKSurface.Create returned null over {target.Width}x{target.Height} (row {target.RowBytes} bytes).");
            _wrappedPixels = target;
        }
        Draw(list, _wrapped);
    }

    /// <summary>
    /// Draws <paramref name="list"/> onto <paramref name="surface"/>, then ages
    /// what this renderer caches: once per frame. The caller flushes a GPU
    /// surface and presents it.
    /// </summary>
    public void Draw(DrawList list, SKSurface surface)
    {
        var canvas = surface.Canvas;
        foreach (ref readonly var op in list.Ops)
        {
            switch (op.Kind)
            {
                case DrawOpKind.Clear: canvas.Clear(op.Color.ToSk()); break;
                case DrawOpKind.PushClip: canvas.Save(); canvas.ClipRect(op.Rect.ToSk()); break;
                case DrawOpKind.PushTransform: canvas.Save(); Transform(canvas, op.Matrix); break;
                case DrawOpKind.PushOpacity: canvas.SaveLayer(Paint(new PaintSpec { Color = new Rgba(0, 0, 0, op.Alpha) })); break;
                case DrawOpKind.Pop: canvas.Restore(); break;
                case DrawOpKind.Rect: canvas.DrawRoundRect(op.Rect.ToSk(), op.A, op.B, Paint(op, list)); break;
                case DrawOpKind.Line: canvas.DrawLine(op.A, op.B, op.C, op.D, Paint(op, list)); break;
                case DrawOpKind.Path: canvas.DrawPath(Path(op, list), Paint(op, list)); break;
                case DrawOpKind.Points:
                    canvas.DrawPoints(op.Mode.ToSk(), list.Points.Slice(op.Start, op.Count).AsSk().ToArray(), Paint(op, list));
                    break;
                case DrawOpKind.Texture:
                    if (_textures.Get(op.Texture) is { } image) canvas.DrawImage(image, op.Rect.ToSk(), Paint(op, list));
                    break;
                case DrawOpKind.BackdropBlur: BackdropBlur(surface, op.Region, op.Rect, op.A); break;
                case DrawOpKind.Glyphs:
                    Glyphs(canvas, list.FaceAt(op.Resource), op.C, list.Glyphs.Slice(op.Start, op.Count),
                        list.Points.Slice(op.Start2, op.Count), op.A, op.B, op.Color);
                    break;
            }
        }

        _paints.EndFrame();
        _textPaints.EndFrame();
        _textFonts.EndFrame();
        _paths.EndFrame();
    }

    // A translation as a translation, a uniform scale after it as a scale, and
    // anything else as the whole matrix before the canvas's own: the cheapest
    // call that draws it, and the calls the frame was drawn with before it was
    // recorded.
    private static void Transform(SKCanvas canvas, in Matrix3x2 m)
    {
        if (m.M12 != 0f || m.M21 != 0f || m.M11 != m.M22) canvas.SetMatrix((m * canvas.TotalMatrix.ToMatrix3x2()).ToSk());
        else
        {
            canvas.Translate(m.M31, m.M32);
            if (m.M11 != 1f) canvas.Scale(m.M11, m.M11);
        }
    }

    // Snapshots region of what the surface holds, blurs it and draws it back
    // clipped: frosted glass. Device pixels; the transform is not applied to
    // the snapshot.
    private void BackdropBlur(SKSurface surface, PixelRect region, RectF clip, float sigma)
    {
        using var snapshot = surface.Snapshot(region.ToSk());
        if (snapshot is null) return;

        // Opaque black, not default: DrawImage modulates by the paint's alpha,
        // and default(Rgba) is transparent.
        var paint = Paint(new PaintSpec { Color = Rgba.Black, IsAntialias = true }, imageBlur: sigma);
        var canvas = surface.Canvas;
        canvas.Save();
        canvas.ClipRect(clip.ToSk());
        canvas.DrawImage(snapshot, new SKRect(region.Left, region.Top, region.Right, region.Bottom), paint);
        canvas.Restore();
    }

    // There is one rendering mode: glyphs at subpixel positions, outlines
    // autohinted at full strength, grayscale antialiasing.
    private void Glyphs(SKCanvas canvas, FontFace face, float sizePx, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions,
        float x, float baselineY, Rgba color)
    {
        if (glyphs.IsEmpty) return;
        var sk = color.ToSk();
        var tf = Typeface(face);
        var key = TextPaintKey.For(sizePx, sk, face.Id);
        var paint = _textPaints.FromKey(key, (Size: sizePx, sk, tf),
            static p => new SKPaint
            {
                TextSize = p.Size,
                Typeface = p.tf,
                Color = p.sk,
                IsAntialias = true,
                SubpixelText = true,
                LcdRenderText = false,
                IsAutohinted = true,
                HintingLevel = SKPaintHinting.Full,
            });
        var font = _textFonts.FromKey(key, paint, static p => p.ToFont());
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(glyphs, font, positions.AsSk());
        using var blob = builder.Build();
        if (blob is not null) canvas.DrawText(blob, x, baselineY, paint);
    }

    private SKTypeface Typeface(FontFace face)
    {
        if (_typefaces.TryGetValue(face.Id, out var typeface)) return typeface;
        using var data = SKData.CreateCopy(face.FontFile);
        typeface = SKTypeface.FromData(data) ?? throw new InvalidOperationException($"Skia cannot open {face}.");
        _typefaces[face.Id] = typeface;
        return typeface;
    }

    // ── Paths ────────────────────────────────────────────────────────────────

    // A kept path is built once and found by its Id; any other is rebuilt from
    // the payload into the scratch path.
    private SKPath Path(in DrawOp op, DrawList list)
    {
        if (op.Resource >= 0)
        {
            var path = list.PathAt(op.Resource);
            return _paths.FromKey(path.Id, path, static p =>
            {
                var native = new SKPath();
                Build(p.Verbs, p.Args, native, p);
                return native;
            });
        }
        _scratch.Rewind();
        Build(list.Verbs.Slice(op.Start, op.Count), list.Floats.Slice(op.Start2, op.Count2), _scratch, null);
        return _scratch;
    }

    // Verb for verb into a native path. An added path is built into its own
    // native path first, then mapped through its transform.
    private static void Build(ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> a, SKPath into, VectorPath? owner)
    {
        int at = 0, added = 0;
        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case PathVerb.Move: into.MoveTo(a[at], a[at + 1]); break;
                case PathVerb.Line: into.LineTo(a[at], a[at + 1]); break;
                case PathVerb.Quad: into.QuadTo(a[at], a[at + 1], a[at + 2], a[at + 3]); break;
                case PathVerb.Cubic: into.CubicTo(a[at], a[at + 1], a[at + 2], a[at + 3], a[at + 4], a[at + 5]); break;
                case PathVerb.Arc:
                    into.ArcTo(a[at], a[at + 1], a[at + 2],
                        a[at + 3] != 0f ? SKPathArcSize.Large : SKPathArcSize.Small,
                        a[at + 4] != 0f ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise,
                        a[at + 5], a[at + 6]);
                    break;
                case PathVerb.AddArc: into.AddArc(new SKRect(a[at], a[at + 1], a[at + 2], a[at + 3]), a[at + 4], a[at + 5]); break;
                case PathVerb.Close: into.Close(); break;
                case PathVerb.AddPath:
                {
                    var (source, transform) = owner!.Added(added++);
                    using var built = new SKPath();
                    Build(source.Verbs, source.Args, built, source);
                    var m = transform.ToSk();
                    // Into an empty path, Transform writes the mapped copy outright:
                    // the operation icons were scaled with when their pixels were
                    // blessed, so their geometry stays bit-identical.
                    if (into.IsEmpty) built.Transform(m, into);
                    else into.AddPath(built, ref m);
                    break;
                }
            }
            at += VectorPath.ArgCount(verb);
        }
    }

    // ── Paints ───────────────────────────────────────────────────────────────

    // The op's paint: its spec, the effects beside it, and its gradient, whose
    // colours and stops are in the payload.
    private SKPaint Paint(in DrawOp op, DrawList list)
    {
        DashPattern? dash = op.Dashed ? op.Dash : null;
        if (!op.Shading.Present) return Paint(op.Paint, op.MaskBlurSigma, op.ImageBlurSigma, dash);
        var s = op.Shading;
        var colors = list.Colors.Slice(s.ColorStart, s.ColorCount);
        var stops = list.Floats.Slice(s.StopStart, s.StopCount);
        var key = Key(op.Paint, op.MaskBlurSigma, op.ImageBlurSigma, dash,
            CacheKey.OfGradient(s.Type == GradientType.Radial ? 1 : 0, s.Start, s.End, s.Radius, colors, stops));
        if (_paints.TryGet(key, out var hit)) return hit.Paint;
        return _paints.Add(key, SkiaPaintEntry.Build(op.Paint, Gradient(s, colors, stops), op.MaskBlurSigma, op.ImageBlurSigma, dash)).Paint;
    }

    /// <summary>
    /// The SKPaint for a spec and its effects, built on the first ask for its
    /// content and reused after. Every field that changes the object is in the key.
    /// </summary>
    private SKPaint Paint(in PaintSpec spec, float maskBlur = 0f, float imageBlur = 0f, DashPattern? dash = null)
        => _paints.FromKey(Key(spec, maskBlur, imageBlur, dash, default), (spec, maskBlur, imageBlur, dash),
            static p => SkiaPaintEntry.Build(p.spec, null, p.maskBlur, p.imageBlur, p.dash)).Paint;

    // Each child gets its own sub-key, so a mask blur and an image blur of the
    // same sigma — or a dash and no dash — never share a paint.
    private static CacheKey Key(in PaintSpec spec, float maskBlur, float imageBlur, DashPattern? dash, CacheKey shader)
    {
        CacheKey mask = maskBlur > 0f ? CacheKey.Of([CacheKey.Bits(maskBlur), (int)SKBlurStyle.Normal]) : default;
        CacheKey image = imageBlur > 0f ? CacheKey.Of([CacheKey.Bits(imageBlur), 1]) : default;
        CacheKey effect = dash is { } d ? CacheKey.Of([CacheKey.Bits(d.On), CacheKey.Bits(d.Off), CacheKey.Bits(d.Phase)]) : default;
        return CacheKey.OfSpec(spec, shader, mask, image, effect);
    }

    // A gradient as Skia takes it. Linear passes no stops as none (evenly
    // spread); radial passes its stops as they are, empty included: the calls
    // the pixels are pinned to.
    private static SKShader Gradient(in Shading s, ReadOnlySpan<Rgba> colors, ReadOnlySpan<float> stops)
    {
        var sk = colors.AsSk().ToArray();
        return s.Type == GradientType.Radial
            ? SKShader.CreateRadialGradient(s.Start.ToSk(), s.Radius, sk, stops.ToArray(), SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(s.Start.ToSk(), s.End.ToSk(), sk, stops.IsEmpty ? null : stops.ToArray(), SKShaderTileMode.Clamp);
    }

    public void Dispose()
    {
        _paints.Dispose();
        _textPaints.Dispose();
        _textFonts.Dispose();
        _paths.Dispose();
        _scratch.Dispose();
        foreach (var typeface in _typefaces.Values) typeface.Dispose();
        _typefaces.Clear();
        for (var node = _textures.FirstLive; node is not null; node = node.Next) node.Item!.Dispose();
        _wrapped?.Dispose();
        _wrapped = null;
    }
}

/// <summary>A paint and the Skia children it carries, disposed together.</summary>
internal sealed class SkiaPaintEntry : IDisposable
{
    public required SKPaint Paint { get; init; }
    private SKShader? _shader;
    private SKMaskFilter? _mask;
    private SKImageFilter? _image;
    private SKPathEffect? _dash;

    public static SkiaPaintEntry Build(in PaintSpec spec, SKShader? shader, float maskBlur, float imageBlur, DashPattern? dash)
    {
        var mask = maskBlur > 0f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, maskBlur) : null;
        var image = imageBlur > 0f ? SKImageFilter.CreateBlur(imageBlur, imageBlur) : null;
        var effect = dash is { } d ? SKPathEffect.CreateDash([d.On, d.Off], d.Phase) : null;
        return new SkiaPaintEntry
        {
            Paint = new SKPaint
            {
                Color = spec.Color.ToSk(),
                Style = spec.Style.ToSk(),
                StrokeWidth = spec.StrokeWidth,
                StrokeCap = spec.StrokeCap.ToSk(),
                StrokeJoin = spec.StrokeJoin.ToSk(),
                IsAntialias = spec.IsAntialias,
                FilterQuality = spec.FilterQuality.ToSk(),
                Shader = shader,
                MaskFilter = mask,
                ImageFilter = image,
                PathEffect = effect,
            },
            _shader = shader,
            _mask = mask,
            _image = image,
            _dash = effect,
        };
    }

    public void Dispose()
    {
        Paint.Dispose();
        _shader?.Dispose();
        _mask?.Dispose();
        _image?.Dispose();
        _dash?.Dispose();
    }
}

/// <summary>
/// Text paint key: size, colour and the face the run resolved to. Keyed by the
/// face's id rather than the (family, weight) asked for, because a run is one
/// face, whatever was asked for.
/// </summary>
internal readonly record struct TextPaintKey(UInt128 Hash)
{
    public static TextPaintKey For(float sizePhysical, SKColor color, int typefaceId)
    {
        Span<int> parts =
        [
            BitConverter.SingleToInt32Bits(sizePhysical),
            (int)(uint)color,
            typefaceId,
        ];
        return new(System.IO.Hashing.XxHash128.HashToUInt128(MemoryMarshal.AsBytes(parts)));
    }
}
