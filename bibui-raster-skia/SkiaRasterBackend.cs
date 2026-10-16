using System.IO.Hashing;
using System.Runtime.InteropServices;
using Bibui.Renderer;
using SkiaSharp;

namespace Bibui.Raster.Skia;

/// <summary>
/// The SkiaSharp raster backend: bibui's drawing calls on Skia's CPU
/// rasterizer (and, through <see cref="Wrap"/>, on a GPU surface a host made).
///
/// <para>Holds the Skia objects the draw path needs — paints and text paints —
/// in touch-evicted caches keyed by the content of the description that asked
/// for them, so nothing native is built twice for the same request.
/// <see cref="EndFrame"/> ages them; the renderer that owns this backend calls
/// it once per painted frame.</para>
/// </summary>
public sealed class SkiaRasterBackend : IRasterBackend
{
    private readonly List<Action> _endFrames = [];
    private readonly List<IDisposable> _disposables = [];

    // A paint owns the Skia children it was built with (shader, filters, dash),
    // so a child can never be evicted from under a live paint: they go together.
    internal readonly ResourceCache<CacheKey, SkiaPaintEntry> Paints;
    internal readonly ResourceCache<TextPaintKey, SKPaint> TextPaints;
    // The font each text paint draws a text blob with.
    internal readonly ResourceCache<TextPaintKey, SKFont> TextFonts;

    // Skia's face for each of bibui's, made from its font file once: the glyph
    // ids FreeType resolves belong to that file, so Skia draws the same glyphs.
    // Kept for the backend's life, like the FontFaces they mirror.
    private readonly Dictionary<int, SKTypeface> _typefaces = [];

    // The last surface handed to Wrap, so a host that renders into the same
    // SKSurface every frame does not allocate a wrapper per frame.
    private SkiaRasterSurface? _wrapped;

    public SkiaRasterBackend()
    {
        Paints = Create<CacheKey, SkiaPaintEntry>();
        TextPaints = Create<TextPaintKey, SKPaint>();
        TextFonts = Create<TextPaintKey, SKFont>();
    }

    private ResourceCache<TKey, TValue> Create<TKey, TValue>() where TKey : notnull
    {
        var cache = new ResourceCache<TKey, TValue>();
        _endFrames.Add(cache.EndFrame);
        _disposables.Add(cache);
        return cache;
    }

    public string Name => "skia";

    // ── Surfaces ─────────────────────────────────────────────────────────────

    private static SKImageInfo Bgra(int width, int height)
        => new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

    public IRasterSurface CreateSurface(int width, int height)
    {
        var surface = SKSurface.Create(Bgra(width, height))
            ?? throw new InvalidOperationException($"SKSurface.Create returned null for {width}x{height}.");
        return new SkiaRasterSurface(this, surface, owns: true);
    }

    public IRasterSurface CreateSurface(in RasterPixels pixels)
    {
        var surface = SKSurface.Create(Bgra(pixels.Width, pixels.Height), pixels.Address, pixels.RowBytes)
            ?? throw new InvalidOperationException(
                $"SKSurface.Create returned null over {pixels.Width}x{pixels.Height} (row {pixels.RowBytes} bytes).");
        return new SkiaRasterSurface(this, surface, owns: true);
    }

    /// <summary>
    /// A surface a caller made with SkiaSharp — a GPU surface over a window's
    /// framebuffer, an offscreen texture — as a bibui render target. The caller
    /// keeps owning it; disposing the wrapper does not dispose it. Wrapping the
    /// same surface again returns the same wrapper.
    /// </summary>
    public IRasterSurface Wrap(SKSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (_wrapped is not null && ReferenceEquals(_wrapped.Native, surface)) return _wrapped;
        return _wrapped = new SkiaRasterSurface(this, surface, owns: false);
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    public RasterPath CreatePath() => new SkiaPath(new SKPath());

    // ── Images ───────────────────────────────────────────────────────────────

    public RasterImage CreateImage(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var image = SKImage.FromPixelCopy(info, rgba, width * 4)
            ?? throw new ArgumentException($"Pixels do not describe a {width}x{height} RGBA image.", nameof(rgba));
        return new SkiaImage(image);
    }

    public RasterImage? DecodeImage(ReadOnlySpan<byte> encoded)
    {
        var image = SKImage.FromEncodedData(encoded);
        return image is null ? null : new SkiaImage(image);
    }

    public byte[] EncodePng(ReadOnlySpan<byte> bgraPremul, int width, int height)
    {
        using var img = SKImage.FromPixelCopy(Bgra(width, height), bgraPremul);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ── Typefaces ────────────────────────────────────────────────────────────

    internal SKTypeface Typeface(FontFace face)
    {
        if (_typefaces.TryGetValue(face.Id, out var typeface)) return typeface;
        using var data = SKData.CreateCopy(face.FontFile);
        typeface = SKTypeface.FromData(data) ?? throw new InvalidOperationException($"Skia cannot open {face}.");
        _typefaces[face.Id] = typeface;
        return typeface;
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The cached paint for one run: a colour, a size and a typeface. Callers
    /// must not dispose or mutate it.
    /// </summary>
    internal SKPaint TextPaint(in RasterFont font, SKColor color)
    {
        var tf = Typeface(font.Face);
        var key = TextPaintKey.For(font.Size, color, font.Face.Id);
        return TextPaints.FromKey(key, (font.Size, color, tf),
            static p => new SKPaint
            {
                TextSize = p.Size,
                Typeface = p.tf,
                Color = p.color,
                IsAntialias = true,
                SubpixelText = true,
                LcdRenderText = false,
                IsAutohinted = true,
                HintingLevel = SKPaintHinting.Full,
            });
    }

    /// <summary>
    /// The font <see cref="TextPaint"/> draws with, for a text blob built over
    /// chars: what drawing a string does inside Skia, without the string.
    /// </summary>
    internal SKFont TextFont(in RasterFont font, SKColor color, SKPaint paint)
        => TextFonts.FromKey(TextPaintKey.For(font.Size, color, font.Face.Id), paint,
            static p => p.ToFont());

    public ShapedRun ShapeText(in RasterFont font, ReadOnlySpan<char> text)
    {
        using var skFont = TextPaint(font, SKColors.Black).ToFont();
        var glyphs = new ushort[skFont.CountGlyphs(text)];
        skFont.GetGlyphs(text, glyphs);
        var advances = new float[glyphs.Length];
        skFont.GetGlyphWidths(glyphs, advances, Span<SKRect>.Empty, null);
        return new ShapedRun(glyphs, advances);
    }

    // ── Paints ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The SKPaint for <paramref name="paint"/>, built on the first ask for its
    /// content and reused after. Every field that changes the object is in the key.
    /// </summary>
    internal SKPaint Paint(in RasterPaint paint)
    {
        // Each child gets its own sub-key, so a mask blur and an image blur of
        // the same sigma — or a dash and no dash — never share a paint.
        CacheKey shader = paint.Shader?.Key ?? default;
        CacheKey mask = paint.MaskBlurSigma > 0f
            ? CacheKey.Of([CacheKey.Bits(paint.MaskBlurSigma), (int)SKBlurStyle.Normal])
            : default;
        CacheKey image = paint.ImageBlurSigma > 0f
            ? CacheKey.Of([CacheKey.Bits(paint.ImageBlurSigma), 1])
            : default;
        CacheKey dash = paint.Dash is { } d
            ? CacheKey.Of([CacheKey.Bits(d.On), CacheKey.Bits(d.Off), CacheKey.Bits(d.Phase)])
            : default;

        return Paints.FromKey(
            CacheKey.OfSpec(paint.Spec, shader, mask, image, dash),
            paint,
            static p => SkiaPaintEntry.Build(p)).Paint;
    }

    // ── Frame clock ──────────────────────────────────────────────────────────

    public void EndFrame()
    {
        foreach (var endFrame in _endFrames) endFrame();
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var typeface in _typefaces.Values) typeface.Dispose();
        _typefaces.Clear();
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

    public static SkiaPaintEntry Build(in RasterPaint p)
    {
        var shader = p.Shader is { } s ? CreateShader(s) : null;
        var mask = p.MaskBlurSigma > 0f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, p.MaskBlurSigma) : null;
        var image = p.ImageBlurSigma > 0f ? SKImageFilter.CreateBlur(p.ImageBlurSigma, p.ImageBlurSigma) : null;
        var dash = p.Dash is { } d ? SKPathEffect.CreateDash([d.On, d.Off], d.Phase) : null;

        var spec = p.Spec;
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
                PathEffect = dash,
            },
            _shader = shader,
            _mask = mask,
            _image = image,
            _dash = dash,
        };
    }

    private static SKShader CreateShader(RasterShader s)
    {
        var colors = ((ReadOnlySpan<Rgba>)s.Colors).AsSk().ToArray();
        return s.Type == GradientType.Radial
            ? SKShader.CreateRadialGradient(s.Start.ToSk(), s.Radius, colors, s.Stops, SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(s.Start.ToSk(), s.End.ToSk(), colors, s.Stops, SKShaderTileMode.Clamp);
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
        return new(XxHash128.HashToUInt128(MemoryMarshal.AsBytes(parts)));
    }
}

/// <summary>An SKImage as a bibui image.</summary>
internal sealed class SkiaImage(SKImage native) : RasterImage
{
    public SKImage Native { get; } = native;
    public override int Width => Native.Width;
    public override int Height => Native.Height;

    protected override void Dispose(bool disposing)
    {
        if (disposing) Native.Dispose();
    }
}
