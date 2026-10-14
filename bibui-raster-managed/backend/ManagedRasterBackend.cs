extern alias freetype;
using System.Numerics;
using System.Runtime.CompilerServices;
using freetype::Bibui.Text.FreeType;
using Bibui.Raster.Managed.Backend.Damage;
using BPaintStyle = Bibui.PaintStyle;
using BStrokeCap = Bibui.StrokeCap;
using BStrokeJoin = Bibui.StrokeJoin;
using MPath = Bibui.Raster.Managed.Path;

namespace Bibui.Raster.Managed.Backend;

// Bibui.Font (a resolved text font) is nearer than the using above; here a font is the engine's.
using Font = freetype::Bibui.Text.FreeType.Font;

/// <summary>
/// bibui's raster backend on the managed rasterizer and the
/// FreeType/HarfBuzz text engine: no Skia anywhere on the drawing path.
///
/// <para>What is not here yet, and what stands in for it:</para>
/// <list type="bullet">
/// <item>Gradients, shadows and backdrop blur are the managed
/// rasterizer's: see <see cref="GradientShader"/>, <see cref="GaussianMaskBlur"/>
/// and <see cref="Canvas.DrawBackdropBlur"/>. An image drawn with
/// <c>ImageBlurSigma</c> is blurred in its own pixels before it is scaled.</item>
/// <item>Fonts come from bytes only: <see cref="MatchFamily"/> answers with the
/// engine's registered or built-in face, and <see cref="MatchCharacter"/> finds
/// no fallback (no fontconfig yet).</item>
/// </list>
///
/// <para>Shapes are antialiased <see cref="Antialiasing.BaselineCompatible"/> by
/// default, so frames match the fixture baselines, which the Skia backend
/// draws; pass
/// <see cref="Antialiasing.Exact"/> for exact coverage.</para>
/// </summary>
public sealed class ManagedRasterBackend : IRasterBackend
{
    private readonly Dictionary<Font, ManagedTypeface> _typefaces = new(ReferenceEqualityComparer.Instance);

    public ManagedRasterBackend(Antialiasing antialiasing = Antialiasing.BaselineCompatible)
    {
        Antialiasing = antialiasing;
        Text = new TextEngine();
        Damage = new DamageTracker(this);
    }

    // ── Damage tracking ──────────────────────────────────────────────────────

    /// <summary>
    /// Damage tracking. When set, a frame — what a canvas of this backend is
    /// told between its <see cref="IRasterCanvas.Clear"/> and
    /// <see cref="EndFrame"/>, which the renderer brackets every frame with — is
    /// recorded instead of drawn, compared with the frame before it, and
    /// rasterised onto its surface only where the two differ
    /// (<see cref="LastRepaint"/>). Pixels are byte-identical to drawing the
    /// whole frame. The layers above are unchanged: every frame is still built,
    /// laid out and issued in full.
    ///
    /// <para>For a surface whose pixels nobody else writes between frames — the
    /// Wayland host's shared-memory buffers. It may hold any frame up to seven
    /// frames old: that is counted here, per surface. Off by default; the
    /// environment variable <c>BIBUI_DAMAGE=1</c> turns it on.</para>
    /// </summary>
    public bool DamageTracking { get; set; } = Environment.GetEnvironmentVariable("BIBUI_DAMAGE") == "1";

    /// <summary>
    /// The rects (physical px) the last damage-tracked frame was rasterised
    /// into on its surface. Null when the whole surface was painted
    /// (<see cref="LastFullRepaintReason"/> says why); empty when nothing changed.
    /// </summary>
    public IReadOnlyList<PixelRect>? LastRepaint => Damage.LastRepaint;

    /// <summary>Why the last damage-tracked frame was painted whole, or null when it was not.</summary>
    public string? LastFullRepaintReason => Damage.LastFullRepaintReason;

    internal DamageTracker Damage { get; }

    public string Name => "managed";

    public Antialiasing Antialiasing { get; }

    /// <summary>The mask blur and the shadow cache, shared by every surface of this backend.</summary>
    public GaussianMaskBlur MaskBlur { get; } = new();

    // Gradient shaders by content, so a gradient drawn every frame is built once.
    private readonly Dictionary<CacheKey, GradientShader> _gradients = [];

    internal GradientShader Gradient(RasterShader s)
    {
        if (_gradients.TryGetValue(s.Key, out var g)) return g;
        if (_gradients.Count >= 256) _gradients.Clear();
        var argb = new uint[s.Colors.Length];
        for (int i = 0; i < argb.Length; i++) argb[i] = s.Colors[i].Argb;
        ReadOnlySpan<float> stops = s.Stops is { Length: > 0 } st && st.Length == argb.Length ? st : default;
        g = s.Type == GradientType.Radial
            ? GradientShader.Radial(s.Start, s.Radius, argb, stops)
            : GradientShader.Linear(s.Start, s.End, argb, stops);
        _gradients[s.Key] = g;
        return g;
    }

    /// <summary>The text engine: one per backend, used on the rendering thread.</summary>
    public TextEngine Text { get; }

    internal GlyphRun ScratchRun { get; } = new();
    internal PlacedGlyph[] Placed = new PlacedGlyph[64];

    // ── Surfaces ─────────────────────────────────────────────────────────────

    public IRasterSurface CreateSurface(int width, int height)
        => new ManagedRasterSurface(this, new Surface(width, height, PixelOrder.Bgra8888));

    public IRasterSurface CreateSurface(in RasterPixels pixels)
        => new ManagedRasterSurface(this, Surface.Wrap(pixels.Address, pixels.Width, pixels.Height, pixels.RowBytes, PixelOrder.Bgra8888));

    // ── Geometry ─────────────────────────────────────────────────────────────

    public RasterPath CreatePath() => new ManagedPath();

    // ── Images ───────────────────────────────────────────────────────────────

    public RasterImage CreateImage(ReadOnlySpan<byte> rgba, int width, int height) => ManagedImage.FromRgba(rgba, width, height);

    public RasterImage? DecodeImage(ReadOnlySpan<byte> encoded)
    {
        var rgba = Png.DecodeRgba(encoded, out int w, out int h);
        return rgba is null ? null : ManagedImage.FromRgba(rgba, w, h);
    }

    public byte[] EncodePng(ReadOnlySpan<byte> bgraPremul, int width, int height) => Png.EncodeBgraPremul(bgraPremul, width, height);

    // ── Typefaces ────────────────────────────────────────────────────────────

    public RasterTypeface? LoadTypeface(ReadOnlySpan<byte> data) => Intern(Text.Load(data));

    public RasterTypeface? MatchFamily(string family, int weight) => Intern(Text.Resolve(family, weight));

    public RasterTypeface? MatchCharacter(int codepoint, int weight)
    {
        // No system font fallback yet: only the built-in faces are asked.
        var f = Text.Resolve(null, weight);
        return f.HasGlyph(codepoint) ? Intern(f) : null;
    }

    public RasterTypeface DefaultTypeface => Intern(Text.DefaultRegular)!;

    private ManagedTypeface? Intern(Font? font)
    {
        if (font is null) return null;
        if (!_typefaces.TryGetValue(font, out var t)) _typefaces[font] = t = new ManagedTypeface(font);
        return t;
    }

    internal FontStrike Strike(in RasterFont font, float scale = 1f)
    {
        var tf = font.Typeface as ManagedTypeface ?? throw new ArgumentException(
            $"A {font.Typeface.GetType().Name} is not a managed typeface; it came from another raster backend.", nameof(font));
        return Text.Strike(tf.Font, font.Size * scale);
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    public float MeasureText(in RasterFont font, string text) => Strike(font).Measure(text);

    public float MeasureText(in RasterFont font, ReadOnlySpan<byte> utf8) => Strike(font).Measure(Utf16(utf8));

    // The text engine shapes UTF-16, so UTF-8 is transcoded into a buffer kept
    // for the next call rather than into a string per call. Valid until the next
    // call: the backend is single-threaded, like the renderer that owns it.
    private char[] _utf16 = new char[256];

    internal ReadOnlySpan<char> Utf16(ReadOnlySpan<byte> utf8)
    {
        int max = System.Text.Encoding.UTF8.GetMaxCharCount(utf8.Length);
        if (_utf16.Length < max) _utf16 = new char[Math.Max(max, _utf16.Length * 2)];
        int n = System.Text.Encoding.UTF8.GetChars(utf8, _utf16);
        return _utf16.AsSpan(0, n);
    }

    public TextMetrics GetMetrics(in RasterFont font)
    {
        var m = Strike(font).Metrics;
        return new TextMetrics(m.Ascent, m.Descent, m.Leading);
    }

    public ShapedRun ShapeText(in RasterFont font, string text)
    {
        var run = ScratchRun;
        Strike(font).Shape(text, run);
        var glyphs = new ushort[run.Count];
        var advances = run.Advances.ToArray();
        var g = run.Glyphs;
        for (int i = 0; i < glyphs.Length; i++) glyphs[i] = (ushort)g[i];
        run.Clear();
        return new ShapedRun(glyphs, advances);
    }

    /// <summary>The renderer finished a frame: a damage-tracked one is compared with the last and rasterised now.</summary>
    public void EndFrame() => Damage.End();

    public void Dispose() => Text.Dispose();
}

/// <summary>A managed surface as a bibui render target.</summary>
public sealed class ManagedRasterSurface : IRasterSurface
{
    private readonly ManagedRasterCanvas _canvas;

    internal ManagedRasterSurface(ManagedRasterBackend backend, Surface native)
    {
        Native = native;
        _canvas = new ManagedRasterCanvas(backend, new Canvas(native) { Antialiasing = backend.Antialiasing, MaskBlur = backend.MaskBlur }, native.Width, native.Height);
    }

    /// <summary>The managed surface: the backend's own escape hatch.</summary>
    public Surface Native { get; }

    public int Width => Native.Width;
    public int Height => Native.Height;
    public IRasterCanvas Canvas => _canvas;

    public bool ReadPixels(Span<byte> bgraPremul, int width, int height)
    {
        if ((long)width * height * 4 != bgraPremul.Length || width > Native.Width || height > Native.Height) return false;
        var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bgraPremul);
        for (int y = 0; y < height; y++) Native.Row(y)[..width].CopyTo(dst.Slice(y * width, width));
        return true;
    }

    public void Dispose() { }
}

/// <summary>
/// A managed path as a bibui path. Change it through these verbs, not through
/// <see cref="Native"/>: damage tracking signs a path by what the verbs built.
/// </summary>
public sealed class ManagedPath : RasterPath
{
    public MPath Native { get; } = new();

    // Damage tracking: a hash of the content, recomputed after a change, and
    // the recorder of a frame that drew this path and has not replayed it yet.
    // That recorder takes a copy before the path changes under it.
    private int _version, _hashedVersion = -1;
    private ulong _hash;
    internal DamageRecorder? Holder;

    private void Changing()
    {
        if (Holder is { } h)
        {
            Holder = null;
            h.Snapshot(this);
        }
        _version++;
    }

    /// <summary>A hash of the path's verbs, points, conic weights and fill rule.</summary>
    internal ulong ContentHash()
    {
        if (_hashedVersion != _version)
        {
            ulong h = Hash64.Mix(Hash64.Seed, (ulong)Native.FillRule);
            h = Hash64.Mix(h, Native.Verbs);
            h = Hash64.Mix(h, Native.Points);
            _hash = Hash64.Mix(h, Native.Weights);
            _hashedVersion = _version;
        }
        return _hash;
    }

    public override void MoveTo(float x, float y) { Changing(); Native.MoveTo(x, y); }
    public override void LineTo(float x, float y) { Changing(); Native.LineTo(x, y); }
    public override void QuadTo(float x1, float y1, float x2, float y2) { Changing(); Native.QuadTo(x1, y1, x2, y2); }
    public override void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3) { Changing(); Native.CubicTo(x1, y1, x2, y2, x3, y3); }
    public override void ArcTo(float rx, float ry, float xAxisRotate, bool largeArc, bool sweep, float x, float y)
    {
        Changing();
        Native.ArcTo(rx, ry, xAxisRotate, largeArc, sweep, x, y);
    }
    public override void AddArc(RectF oval, float startAngle, float sweepAngle)
    {
        Changing();
        Native.AddArc(new Rect(oval.Left, oval.Top, oval.Right, oval.Bottom), startAngle, sweepAngle);
    }
    public override void Close() { Changing(); Native.Close(); }

    public override void AddPath(RasterPath source, in Matrix3x2 transform)
    {
        var src = source as ManagedPath ?? throw new ArgumentException(
            $"A {source.GetType().Name} is not a managed path; it came from another raster backend.", nameof(source));
        Changing();
        Native.AddPath(src.Native, transform);
    }

    public override void Rewind() { Changing(); Native.Rewind(); }
    public override bool IsEmpty => Native.IsEmpty;
}

/// <summary>A FreeType face as a bibui typeface.</summary>
public sealed class ManagedTypeface : RasterTypeface
{
    internal ManagedTypeface(Font font) => Font = font;

    public Font Font { get; }

    public override string FamilyName => Font.Family.Length > 0 ? Font.Family : Font.Data.Name;

    public override bool HasGlyph(int codepoint) => Font.HasGlyph(codepoint);
}

/// <summary>An immutable premultiplied bitmap (<c>0xAARRGGBB</c>).</summary>
public sealed class ManagedImage : RasterImage
{
    private ManagedImage(uint[] pixels, int width, int height)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
    }

    internal uint[] Pixels { get; }
    public override int Width { get; }
    public override int Height { get; }

    /// <summary>From straight-alpha RGBA8888 bytes.</summary>
    public static ManagedImage FromRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var px = new uint[width * height];
        Premultiply(rgba, width, height, px);
        return new ManagedImage(px, width, height);
    }

    internal static void Premultiply(ReadOnlySpan<byte> rgba, int width, int height, Span<uint> into)
    {
        if (rgba.Length < width * height * 4) throw new ArgumentException("Too few pixel bytes.", nameof(rgba));
        for (int i = 0; i < width * height; i++)
        {
            uint argb = ((uint)rgba[4 * i + 3] << 24) | ((uint)rgba[4 * i] << 16) | ((uint)rgba[4 * i + 1] << 8) | rgba[4 * i + 2];
            into[i] = PixelMath.Premul(argb);
        }
    }
}

/// <summary>
/// The canvas calls on the managed <see cref="Canvas"/>. Shapes map one to one;
/// what is still approximate is listed on <see cref="ManagedRasterBackend"/>.
/// With <see cref="ManagedRasterBackend.DamageTracking"/> on, a frame's calls go
/// to the backend's recorder instead, and are rasterised at its end.
/// </summary>
public sealed class ManagedRasterCanvas : IRasterCanvas
{
    private readonly ManagedRasterBackend _backend;
    private readonly ImageShader _imageShader = new();
    private uint[] _pixelScratch = [];

    // The frame being recorded on this canvas, or null when calls draw directly.
    // A teed recorder records and the call is drawn as well.
    private DamageRecorder? _rec;

    internal ManagedRasterCanvas(ManagedRasterBackend backend, Canvas native, int width, int height)
    {
        _backend = backend;
        Native = native;
        Width = width;
        Height = height;
    }

    /// <summary>The managed canvas: the backend's own escape hatch. What is drawn through it is not damage-tracked.</summary>
    public Canvas Native { get; }

    internal int Width { get; }
    internal int Height { get; }

    internal void StopRecording() => _rec = null;

    /// <summary>Clears without starting a frame: a recorded frame's own Clear, replayed.</summary>
    internal void ClearDirect(Rgba color) => Native.Clear(color.Argb);

    public void Clear(Rgba color)
    {
        // Every frame starts with a Clear: damage tracking starts recording here.
        if (_backend.DamageTracking)
        {
            _rec = _backend.Damage.Begin(this, color);
            _rec.Clear(color);
            if (!_rec.Tee) return;
        }
        else _backend.Damage.Forget();
        Native.Clear(color.Argb);
    }

    // ── State ────────────────────────────────────────────────────────────────

    public void Save()
    {
        if (_rec is { } r) { r.Save(); if (!r.Tee) return; }
        Native.Save();
    }

    public void Restore()
    {
        if (_rec is { } r) { r.Restore(); if (!r.Tee) return; }
        Native.Restore();
    }

    public void Translate(float dx, float dy)
    {
        if (_rec is { } r) { r.Translate(dx, dy); if (!r.Tee) return; }
        Native.Translate(dx, dy);
    }

    public void Scale(float sx, float sy)
    {
        if (_rec is { } r) { r.Scale(sx, sy); if (!r.Tee) return; }
        Native.Scale(sx, sy);
    }

    public Matrix3x2 TotalMatrix => _rec is { } r ? r.TotalMatrix : Native.Matrix;

    public void SetMatrix(in Matrix3x2 matrix)
    {
        if (_rec is { } r) { r.SetMatrix(matrix); if (!r.Tee) return; }
        Native.SetMatrix(matrix);
    }

    public void ClipRect(RectF rect)
    {
        if (_rec is { } r) { r.ClipRect(rect); if (!r.Tee) return; }
        Native.ClipRect(ToRect(rect));
    }

    public void SaveLayerAlpha(byte alpha)
    {
        if (_rec is { } r) { r.SaveLayerAlpha(alpha); if (!r.Tee) return; }
        Native.SaveLayer(alpha);
    }

    // ── Shapes ───────────────────────────────────────────────────────────────

    public void DrawRoundRect(RectF rect, float rx, float ry, in RasterPaint paint)
    {
        if (_rec is { } r) { r.DrawRoundRect(rect, rx, ry, paint); if (!r.Tee) return; }
        if (!Convert(paint, out var p)) return;
        Native.DrawRoundRect(ToRect(rect).Sorted(), rx, ry, p);
    }

    public void DrawLine(float x0, float y0, float x1, float y1, in RasterPaint paint)
    {
        if (_rec is { } r) { r.DrawLine(x0, y0, x1, y1, paint); if (!r.Tee) return; }
        DirectLine(x0, y0, x1, y1, paint);
    }

    private void DirectLine(float x0, float y0, float x1, float y1, in RasterPaint paint)
    {
        if (!Convert(paint, out var p)) return;
        Native.DrawLine(x0, y0, x1, y1, p);
    }

    public void DrawPath(RasterPath path, in RasterPaint paint)
    {
        var mp = path as ManagedPath ?? throw new ArgumentException(
            $"A {path.GetType().Name} is not a managed path; it came from another raster backend.", nameof(path));
        if (_rec is { } r) { r.DrawPath(mp, paint); if (!r.Tee) return; }
        if (!Convert(paint, out var p)) return;
        Native.DrawPath(mp.Native, p);
    }

    public void DrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, in RasterPaint paint)
    {
        if (_rec is { } r) { r.DrawPoints(mode, points, paint); if (!r.Tee) return; }
        if (!Convert(paint, out var p)) return;
        p.Style = PaintStyle.Stroke;
        switch (mode)
        {
            case PointMode.Points:
            {
                float h = MathF.Max(p.StrokeWidth, 1f) * 0.5f;
                var fill = p;
                fill.Style = PaintStyle.Fill;
                foreach (var pt in points)
                {
                    var rc = new Rect(pt.X - h, pt.Y - h, pt.X + h, pt.Y + h);
                    if (p.Cap == LineCap.Round) Native.DrawOval(rc, fill);
                    else Native.DrawRect(rc, fill);
                }
                break;
            }
            case PointMode.Lines:
                for (int i = 0; i + 1 < points.Length; i += 2)
                    DirectLine(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, paint);
                break;
            default:
                for (int i = 0; i + 1 < points.Length; i++)
                    DirectLine(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, paint);
                break;
        }
    }

    // ── Images ───────────────────────────────────────────────────────────────

    public void DrawImage(RasterImage image, RectF dest, in RasterPaint paint)
    {
        var img = image as ManagedImage ?? throw new ArgumentException(
            $"A {image.GetType().Name} is not a managed image; it came from another raster backend.", nameof(image));
        if (_rec is { } r) { r.DrawImage(img, dest, paint); if (!r.Tee) return; }
        DrawBitmap(img.Pixels, img.Width, img.Height, dest, paint);
    }

    public void DrawPixels(ReadOnlySpan<byte> rgba, int width, int height, RectF dest, in RasterPaint paint)
    {
        if (_rec is { } r) { r.DrawPixels(rgba, width, height, dest, paint); if (!r.Tee) return; }
        if (width <= 0 || height <= 0) return;
        if (_pixelScratch.Length < width * height) _pixelScratch = new uint[width * height];
        ManagedImage.Premultiply(rgba, width, height, _pixelScratch);
        DrawBitmap(_pixelScratch, width, height, dest, paint);
    }

    private void DrawBitmap(uint[] pixels, int width, int height, RectF dest, in RasterPaint paint)
    {
        var d = ToRect(dest);
        if (d.IsEmpty) return;
        if (paint.ImageBlurSigma > 0) (pixels, width, height, d) = BlurBitmap(pixels, width, height, d, paint.ImageBlurSigma);
        _imageShader.Set(pixels, width, height, d, paint.Spec.FilterQuality != FilterQuality.None);
        // The paint's alpha modulates the image, as an SKPaint's does.
        var p = new Paint { Color = paint.Spec.Color.Argb | 0x00FFFFFFu, Shader = _imageShader };
        Native.DrawRect(d, p);
    }

    /// <summary>
    /// An image drawn with an image blur. The blur
    /// (<see cref="ImageBlur"/>) is applied to the image's own pixels with sigma
    /// mapped into them, transparent beyond the edges; the result, grown by the
    /// blur's reach, is then drawn as the image would be. Exact for an unscaled
    /// draw, which is the only kind bibui makes.
    /// </summary>
    private (uint[], int, int, Rect) BlurBitmap(uint[] pixels, int width, int height, Rect d, float sigma)
    {
        var m = Native.Matrix;
        float sx = d.Width / width * MathF.Sqrt(MathF.Abs(m.GetDeterminant()));
        float s = sx > 0 ? sigma / sx : sigma;
        int spread = ImageBlur.Spread(s);
        var src = new IRect(0, 0, width, height);
        var dst = new IRect(-spread, -spread, width + spread, height + spread);
        var output = new uint[dst.Width * dst.Height];
        ImageBlur.Blur(pixels, width, src, output, dst, s);
        float px = d.Width / width, py = d.Height / height;
        return (output, dst.Width, dst.Height, new Rect(d.Left - spread * px, d.Top - spread * py, d.Right + spread * px, d.Bottom + spread * py));
    }

    /// <summary>
    /// Frosted glass. The surface's device pixels in
    /// <paramref name="region"/> are snapshotted and drawn back blurred through the
    /// current transform, so the transform's translation moves the result and
    /// the clip; its scale scales sigma (bibui only translates here).
    /// </summary>
    public void DrawBackdropBlur(PixelRect region, RectF clip, float sigma)
    {
        if (_rec is { } r) { r.DrawBackdropBlur(region, clip, sigma); if (!r.Tee) return; }
        var m = Native.Matrix;
        float scale = MathF.Sqrt(MathF.Abs(m.GetDeterminant()));
        int dx = (int)MathF.Round(m.M31), dy = (int)MathF.Round(m.M32);
        var c = ToRect(clip);
        var deviceClip = new Rect(c.Left * m.M11 + m.M31, c.Top * m.M22 + m.M32, c.Right * m.M11 + m.M31, c.Bottom * m.M22 + m.M32).Sorted();
        Native.DrawBackdropBlur(new IRect(region.Left, region.Top, region.Right, region.Bottom), deviceClip, sigma * scale, dx, dy);
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    public float DrawText(string text, float x, float baselineY, in RasterFont font, Rgba color)
    {
        if (_rec is { } r)
        {
            if (!r.Tee) return r.DrawText(text, x, baselineY, font, color, drawn: null);
            return r.DrawText(text, x, baselineY, font, color, DrawText(text.AsSpan(), x, baselineY, font, color));
        }
        return DrawText(text.AsSpan(), x, baselineY, font, color);
    }

    public float DrawText(ReadOnlySpan<byte> utf8, float x, float baselineY, in RasterFont font, Rgba color)
    {
        if (_rec is { } r)
        {
            if (!r.Tee) return r.DrawText(utf8, x, baselineY, font, color, drawn: null);
            return r.DrawText(utf8, x, baselineY, font, color, DrawText(_backend.Utf16(utf8), x, baselineY, font, color));
        }
        return DrawText(_backend.Utf16(utf8), x, baselineY, font, color);
    }

    private float DrawText(ReadOnlySpan<char> text, float x, float baselineY, in RasterFont font, Rgba color)
    {
        var run = _backend.ScratchRun;
        var strike = _backend.Strike(font);
        strike.Shape(text, run);
        float advance = run.Width;
        if (TextScale(out float s, out var origin, x, baselineY))
        {
            var ds = s == 1f ? strike : _backend.Strike(font, s);
            if (ds != strike) ds.Shape(text, run);
            int n = Place(run.Count);
            n = ds.Place(run, origin.X, origin.Y, _backend.Placed.AsSpan(0, n));
            Composite(n, color);
        }
        run.Clear();
        return advance;
    }

    public void DrawGlyphRun(ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions,
        float x, float baselineY, in RasterFont font, Rgba color)
    {
        if (glyphs.Length != positions.Length) throw new ArgumentException("One position per glyph.", nameof(positions));
        if (_rec is { } r) { r.DrawGlyphRun(glyphs, positions, x, baselineY, font, color); if (!r.Tee) return; }
        if (glyphs.IsEmpty || !TextScale(out float s, out var origin, x, baselineY)) return;
        var strike = _backend.Strike(font, s);
        int n = Place(glyphs.Length);
        if (s == 1f) n = strike.Place(glyphs, positions, origin.X, origin.Y, _backend.Placed.AsSpan(0, n));
        else
        {
            Span<Vector2> scaled = glyphs.Length <= 256 ? stackalloc Vector2[glyphs.Length] : new Vector2[glyphs.Length];
            for (int i = 0; i < scaled.Length; i++) scaled[i] = positions[i] * s;
            n = strike.Place(glyphs, scaled, origin.X, origin.Y, _backend.Placed.AsSpan(0, n));
        }
        Composite(n, color);
    }

    /// <summary>
    /// Glyph masks are placed in device pixels, so the text origin goes through
    /// the transform here. A uniform scale scales the strike; anything with
    /// rotation or skew is not drawn (bibui never rotates text).
    /// </summary>
    private bool TextScale(out float scale, out Vector2 origin, float x, float y)
    {
        var m = Native.Matrix;
        origin = Vector2.Transform(new Vector2(x, y), m);
        scale = m.M11;
        return m.M12 == 0 && m.M21 == 0 && m.M11 == m.M22 && m.M11 > 0;
    }

    private int Place(int count)
    {
        if (_backend.Placed.Length < count) _backend.Placed = new PlacedGlyph[Math.Max(count, _backend.Placed.Length * 2)];
        return count;
    }

    private void Composite(int n, Rgba color)
    {
        var tc = new freetype::Bibui.Text.FreeType.TextColor(color.R, color.G, color.B, color.A);
        var table = MaskGamma.Table(tc);
        var placed = _backend.Placed;
        for (int i = 0; i < n; i++)
        {
            var g = placed[i];
            Native.DrawA8(g.Mask.Coverage, g.Mask.Width, g.Mask.Height, g.X, g.Y, color.Argb, table);
        }
    }

    // ── Paint ────────────────────────────────────────────────────────────────

    /// <summary>A bibui paint as a managed one; false when the draw is to be skipped.</summary>
    private bool Convert(in RasterPaint rp, out Paint p)
    {
        var spec = rp.Spec;
        p = new Paint
        {
            Color = spec.Color.Argb,
            Style = spec.Style switch
            {
                BPaintStyle.Stroke => PaintStyle.Stroke,
                BPaintStyle.StrokeAndFill => PaintStyle.StrokeAndFill,
                _ => PaintStyle.Fill,
            },
            StrokeWidth = spec.StrokeWidth,
            Cap = spec.StrokeCap switch { BStrokeCap.Round => LineCap.Round, BStrokeCap.Square => LineCap.Square, _ => LineCap.Butt },
            Join = spec.StrokeJoin switch { BStrokeJoin.Round => LineJoin.Round, BStrokeJoin.Bevel => LineJoin.Bevel, _ => LineJoin.Miter },
            Aliased = !spec.IsAntialias,
            Dash = rp.Dash is { } d ? new DashPattern(d.On, d.Off, d.Phase) : null,
        };
        // Shadows blur by sigma mapped through the transform (times the
        // geometric mean of its axis scales, capped at 532); gradients shade per pixel,
        // and the paint's alpha modulates them.
        if (rp.MaskBlurSigma > 0)
        {
            var m = Native.Matrix;
            float d0 = new Vector2(m.M11, m.M12).Length(), d1 = new Vector2(m.M21, m.M22).Length();
            p.MaskBlurSigma = MathF.Min(rp.MaskBlurSigma * MathF.Sqrt(d0 * d1), 532f);
        }
        if (rp.Shader is { Colors.Length: > 0 } sh) p.Shader = _backend.Gradient(sh);
        return true;
    }

    private static Rect ToRect(RectF r) => new(r.Left, r.Top, r.Right, r.Bottom);

}

/// <summary>
/// A bitmap drawn into a destination rect: nearest or bilinear sampling,
/// clamped at the edges; not yet checked against the Skia backend's low-quality sampling.
/// </summary>
internal sealed class ImageShader : Shader
{
    private uint[] _px = [];
    private int _w, _h;
    private Rect _dest;
    private bool _bilinear;
    private Matrix3x2 _deviceToImage;

    public void Set(uint[] pixels, int width, int height, Rect dest, bool bilinear)
    {
        _px = pixels;
        _w = width;
        _h = height;
        _dest = dest;
        _bilinear = bilinear;
    }

    public override void Begin(in Matrix3x2 localToDevice)
    {
        Matrix3x2.Invert(localToDevice, out var deviceToLocal);
        var localToImage = Matrix3x2.CreateTranslation(-_dest.Left, -_dest.Top)
                           * Matrix3x2.CreateScale(_w / _dest.Width, _h / _dest.Height);
        _deviceToImage = deviceToLocal * localToImage;
    }

    public override void ShadeSpan(int x, int y, Span<uint> dst)
    {
        var m = _deviceToImage;
        for (int i = 0; i < dst.Length; i++)
        {
            var p = Vector2.Transform(new Vector2(x + i + 0.5f, y + 0.5f), m);
            dst[i] = _bilinear ? Bilinear(p.X - 0.5f, p.Y - 0.5f) : At((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint At(int x, int y) => _px[Math.Clamp(y, 0, _h - 1) * _w + Math.Clamp(x, 0, _w - 1)];

    private uint Bilinear(float u, float v)
    {
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        uint fx = (uint)((u - x0) * 256f), fy = (uint)((v - y0) * 256f);
        uint a = At(x0, y0), b = At(x0 + 1, y0), c = At(x0, y0 + 1), d = At(x0 + 1, y0 + 1);
        uint top = Lerp(a, b, fx), bot = Lerp(c, d, fx);
        return Lerp(top, bot, fy);
    }

    private static uint Lerp(uint a, uint b, uint t)
    {
        if (t == 0) return a;
        return PixelMath.MulQ(a, 256 - t) + PixelMath.MulQ(b, t);
    }
}
