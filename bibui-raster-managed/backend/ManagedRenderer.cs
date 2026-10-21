using System.Numerics;
using System.Runtime.CompilerServices;
using Bibui.Core;
using Bibui.Core.Draw;
using Bibui.Core.FreeType;
using Bibui.Raster.Managed.Backend.Damage;
using BPaintStyle = Bibui.Core.PaintStyle;
using BStrokeCap = Bibui.Core.StrokeCap;
using BStrokeJoin = Bibui.Core.StrokeJoin;
using BPathVerb = Bibui.Core.Draw.PathVerb;
using MPath = Bibui.Raster.Managed.Path;

namespace Bibui.Raster.Managed.Backend;

// Bibui.Core.TextColor (a theme role) is nearer than the using above; here a text colour is the engine's.
using FtTextColor = Bibui.Core.FreeType.TextColor;

/// <summary>
/// Draws a <see cref="DrawList"/> with the managed rasterizer and the
/// FreeType/HarfBuzz text engine: no Skia anywhere on the drawing path, and
/// Native AOT clean. Into a managed <see cref="Surface"/>, or into CPU memory
/// the caller owns (a <c>wl_shm</c> buffer, a test's array).
///
/// <para>Holds what the rasterizer draws with: a native path per kept
/// <see cref="VectorPath"/>, gradient shaders by content, the mask blur and its
/// shadow cache, and the caller's textures. Shapes are antialiased
/// <see cref="Antialiasing.BaselineCompatible"/> by default, so frames match the
/// fixture baselines, which Skia draws; pass <see cref="Antialiasing.Exact"/>
/// for exact coverage.</para>
///
/// <para>With <see cref="DamageTracking"/> on, a frame is compared with the
/// last before anything is drawn and rasterised only where the two differ.</para>
///
/// <para>Not thread-safe: one thread draws at a time.</para>
/// </summary>
public sealed class ManagedRenderer : IDisposable
{
    /// <summary>A renderer bound to nothing: it draws into the memory or the surface each call names.</summary>
    public ManagedRenderer(Antialiasing antialiasing = Antialiasing.BaselineCompatible)
    {
        Antialiasing = antialiasing;
        Damage = new DamageTracker();
    }

    /// <summary>
    /// A renderer bound to <paramref name="target"/>, memory a platform presents:
    /// <see cref="Draw(DrawList)"/> draws into whatever memory it holds then. A
    /// platform that cycles buffers points it at each in turn; damage tracking
    /// knows each buffer by its memory.
    /// </summary>
    public ManagedRenderer(CpuTarget target, Antialiasing antialiasing = Antialiasing.BaselineCompatible)
        : this(antialiasing)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    // The memory this renderer is bound to, or null; fixed for its life.
    private readonly CpuTarget? _target;

    /// <summary>Draws <paramref name="list"/> into the memory the bound target holds now.</summary>
    public void Draw(DrawList list)
        => Draw(list, (_target ?? throw new InvalidOperationException(
            "This renderer is bound to no target: draw with Draw(list, pixels).")).Pixels);

    public Antialiasing Antialiasing { get; }

    /// <summary>The mask blur and the shadow cache, shared by every surface this draws.</summary>
    public GaussianMaskBlur MaskBlur { get; } = new();

    // ── Damage tracking ──────────────────────────────────────────────────────

    /// <summary>
    /// Damage tracking. When set, each frame's list is compared with the last
    /// one drawn, and the frame is rasterised onto its surface only where the
    /// two differ (<see cref="LastRepaint"/>). Pixels are byte-identical to
    /// drawing the whole frame. The layers above are unchanged: every frame is
    /// still built, laid out and recorded in full.
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

    // ── Textures ─────────────────────────────────────────────────────────────
    // Made by usage code, which owns them: a draw list only names a handle.

    private readonly TextureTable<ManagedTexture> _textures = new();

    /// <summary>
    /// A <paramref name="width"/> × <paramref name="height"/> texture of
    /// <paramref name="format"/>, from <paramref name="pixels"/> (rows
    /// <paramref name="rowBytes"/> apart, or packed tight when 0; RGBA8 is straight
    /// alpha). Empty pixels make a Stream texture of zeros. The kind is a hint
    /// here: a Stream texture is overwritten in place. The caller owns it:
    /// <see cref="ReleaseTexture"/> frees it.
    /// </summary>
    public TextureHandle CreateTexture(TextureFormat format, TextureKind kind, int width, int height,
        ReadOnlySpan<byte> pixels, int rowBytes = 0)
    {
        var texture = new ManagedTexture(format, kind, width, height);
        if (!(pixels.IsEmpty && kind == TextureKind.Stream))
            texture.Write(pixels, TexturePixels.RowBytes(format, width, height, pixels, rowBytes));
        return _textures.Add(texture);
    }

    /// <summary>
    /// Overwrites a Stream texture's pixels, the whole texture, in place, at the
    /// size and format it was made with. A Static texture does not change: its
    /// update is ignored. So is a released handle.
    /// </summary>
    public void UpdateTexture(TextureHandle texture, ReadOnlySpan<byte> pixels, int rowBytes = 0)
    {
        if (_textures.Get(texture) is not { Kind: TextureKind.Stream } t) return;
        t.Write(pixels, TexturePixels.RowBytes(t.Format, t.Width, t.Height, pixels, rowBytes));
        _textures.Touch(texture);
    }

    /// <summary>Frees <paramref name="texture"/>; drawing it afterwards draws nothing.</summary>
    public void ReleaseTexture(TextureHandle texture) => _textures.Remove(texture);

    /// <summary>The texture's content version: what damage tracking compares, since a handle stays the same across updates.</summary>
    internal uint TextureVersion(TextureHandle texture) => _textures.Version(texture);

    // ── Targets ──────────────────────────────────────────────────────────────

    // A canvas per surface drawn on: a canvas keeps its state stacks, and damage
    // tracking counts the frame each surface holds. Surfaces over memory the
    // caller owns are found again by the memory they were made over.
    private readonly Dictionary<Surface, Canvas> _canvases = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RasterPixels, Surface> _wrapped = [];

    /// <summary>
    /// Draws <paramref name="target"/>'s pixels from <paramref name="list"/>:
    /// BGRA8888 premultiplied memory the caller owns. The same memory drawn into
    /// again is the same surface, so damage tracking knows what it holds.
    /// </summary>
    public void Draw(DrawList list, RasterPixels target)
    {
        if (!_wrapped.TryGetValue(target, out var surface))
        {
            // A host cycles a few buffers; one that resizes leaves the old ones
            // behind, so the table is dropped rather than grown past a handful.
            if (_wrapped.Count >= 8)
            {
                foreach (var old in _wrapped.Values) _canvases.Remove(old);
                _wrapped.Clear();
            }
            surface = Surface.Wrap(target.Address, target.Width, target.Height, target.RowBytes, PixelOrder.Bgra8888);
            _wrapped[target] = surface;
        }
        Draw(list, surface);
    }

    /// <summary>
    /// Draws <paramref name="list"/> onto <paramref name="surface"/>: the whole
    /// frame, or, with <see cref="DamageTracking"/>, only where it differs from
    /// the frame the surface holds.
    /// </summary>
    public void Draw(DrawList list, Surface surface)
    {
        if (!_canvases.TryGetValue(surface, out var canvas))
            _canvases[surface] = canvas = new Canvas(surface) { Antialiasing = Antialiasing, MaskBlur = MaskBlur };

        ResolvePaths(list);
        if (DamageTracking) Damage.Draw(this, list, canvas, surface.Width, surface.Height);
        else
        {
            Damage.Forget();
            for (int k = 0; k < list.Ops.Length; k++) Exec(list, k, canvas);
        }
        _paths.EndFrame();
    }

    // ── Paths ────────────────────────────────────────────────────────────────

    // A kept path's native form and its content hash, built once and found by
    // its Id. A kept path does not change after it is built.
    internal sealed class KeptPath
    {
        public readonly MPath Native = new();
        public ulong Hash;
    }

    private readonly ResourceCache<int, KeptPath> _paths = new();

    // This frame's native path per Path op, by op index; payload paths are
    // built into pooled paths, reused frame to frame.
    private MPath?[] _opPaths = new MPath?[64];
    private ulong[] _opPathHashes = new ulong[64];
    private readonly List<MPath> _pool = [];

    /// <summary>The native path the <paramref name="k"/>-th op draws, and a hash of its content.</summary>
    internal (MPath Path, ulong Hash) PathOf(int k) => (_opPaths[k]!, _opPathHashes[k]);

    private void ResolvePaths(DrawList list)
    {
        var ops = list.Ops;
        if (_opPaths.Length < ops.Length)
        {
            _opPaths = new MPath?[Math.Max(ops.Length, _opPaths.Length * 2)];
            _opPathHashes = new ulong[_opPaths.Length];
        }
        int pooled = 0;
        for (int k = 0; k < ops.Length; k++)
        {
            ref readonly var op = ref ops[k];
            if (op.Kind != DrawOpKind.Path) continue;
            if (op.Resource >= 0)
            {
                var kept = _paths.FromKey(list.PathAt(op.Resource).Id, list.PathAt(op.Resource), static source =>
                {
                    var entry = new KeptPath();
                    Build(source.Verbs, source.Args, entry.Native, source);
                    entry.Hash = ContentHash(entry.Native);
                    return entry;
                });
                (_opPaths[k], _opPathHashes[k]) = (kept.Native, kept.Hash);
                continue;
            }
            if (pooled == _pool.Count) _pool.Add(new MPath());
            var path = _pool[pooled++];
            path.Rewind();
            Build(list.Verbs.Slice(op.Start, op.Count), list.Floats.Slice(op.Start2, op.Count2), path, null);
            (_opPaths[k], _opPathHashes[k]) = (path, ContentHash(path));
        }
    }

    // Verb for verb into a native path; an added path is built on its own first,
    // then appended through its transform.
    private static void Build(ReadOnlySpan<BPathVerb> verbs, ReadOnlySpan<float> a, MPath into, VectorPath? owner)
    {
        int at = 0, added = 0;
        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case BPathVerb.Move: into.MoveTo(a[at], a[at + 1]); break;
                case BPathVerb.Line: into.LineTo(a[at], a[at + 1]); break;
                case BPathVerb.Quad: into.QuadTo(a[at], a[at + 1], a[at + 2], a[at + 3]); break;
                case BPathVerb.Cubic: into.CubicTo(a[at], a[at + 1], a[at + 2], a[at + 3], a[at + 4], a[at + 5]); break;
                case BPathVerb.Arc: into.ArcTo(a[at], a[at + 1], a[at + 2], a[at + 3] != 0f, a[at + 4] != 0f, a[at + 5], a[at + 6]); break;
                case BPathVerb.AddArc: into.AddArc(new Rect(a[at], a[at + 1], a[at + 2], a[at + 3]), a[at + 4], a[at + 5]); break;
                case BPathVerb.Close: into.Close(); break;
                case BPathVerb.AddPath:
                {
                    var (source, transform) = owner!.Added(added++);
                    var built = new MPath();
                    Build(source.Verbs, source.Args, built, source);
                    into.AddPath(built, transform);
                    break;
                }
            }
            at += VectorPath.ArgCount(verb);
        }
    }

    /// <summary>A hash of a path's verbs, points, conic weights and fill rule.</summary>
    private static ulong ContentHash(MPath path)
    {
        ulong h = Hash64.Mix(Hash64.Seed, (ulong)path.FillRule);
        h = Hash64.Mix(h, path.Verbs);
        h = Hash64.Mix(h, path.Points);
        return Hash64.Mix(h, path.Weights);
    }

    // ── Ops ──────────────────────────────────────────────────────────────────

    private readonly ImageShader _imageShader = new();
    private PlacedGlyph[] _placed = new PlacedGlyph[64];

    // Gradient shaders by content, so a gradient drawn every frame is built once.
    private readonly Dictionary<CacheKey, GradientShader> _gradients = [];

    /// <summary>Draws the <paramref name="k"/>-th op of <paramref name="list"/> onto <paramref name="c"/>.</summary>
    internal void Exec(DrawList list, int k, Canvas c)
    {
        ref readonly var op = ref list.Ops[k];
        switch (op.Kind)
        {
            case DrawOpKind.Clear: c.Clear(op.Color.Argb); break;
            case DrawOpKind.PushClip: c.Save(); c.ClipRect(ToRect(op.Rect)); break;
            case DrawOpKind.PushTransform: c.Save(); Transform(c, op.Matrix); break;
            case DrawOpKind.PushBlendMode:
                // SrcOver composites as if there were no layer: none is made.
                if (op.Blend == BlendMode.SrcOver) c.Save();
                else c.SaveLayer((LayerMode)op.Blend);
                break;
            case DrawOpKind.PushColorMatrix:
            {
                var m = list.Floats.Slice(op.Start, op.Count);
                if (ColorMatrices.IsIdentity(m)) c.Save();
                else if (ColorMatrices.IsOpacity(m, out byte alpha)) c.SaveLayer(alpha);
                else c.SaveLayer(m);
                break;
            }
            case DrawOpKind.Pop: c.Restore(); break;
            case DrawOpKind.Rect:
                c.DrawRoundRect(ToRect(op.Rect).Sorted(), op.A, op.B, Convert(op, list, c));
                break;
            case DrawOpKind.Line: c.DrawLine(op.A, op.B, op.C, op.D, Convert(op, list, c)); break;
            case DrawOpKind.Path: c.DrawPath(_opPaths[k]!, Convert(op, list, c)); break;
            case DrawOpKind.Points: Points(c, op.Mode, list.Points.Slice(op.Start, op.Count), Convert(op, list, c)); break;
            case DrawOpKind.Texture:
                if (_textures.Get(op.Texture) is not { } img) break;
                Bitmap(c, img, list.LookupAt(op.Resource), op);
                break;
            case DrawOpKind.BackdropBlur: BackdropBlur(c, op.Region, op.Rect, op.A); break;
            case DrawOpKind.Glyphs:
                Glyphs(c, list.FaceAt(op.Resource), op.C, list.Glyphs.Slice(op.Start, op.Count),
                    list.Points.Slice(op.Start2, op.Count), op.A, op.B, op.Color);
                break;
        }
    }

    // A translation as a translation, a uniform scale after it as a scale, and
    // anything else as the whole matrix before the canvas's own: the calls the
    // frame was drawn with before it was recorded.
    private static void Transform(Canvas c, in Matrix3x2 m)
    {
        if (m.M12 != 0f || m.M21 != 0f || m.M11 != m.M22) c.SetMatrix(m * c.Matrix);
        else
        {
            c.Translate(m.M31, m.M32);
            if (m.M11 != 1f) c.Scale(m.M11, m.M11);
        }
    }

    private static void Points(Canvas c, PointMode mode, ReadOnlySpan<Vector2> points, Paint p)
    {
        var line = p;
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
                    if (p.Cap == LineCap.Round) c.DrawOval(rc, fill);
                    else c.DrawRect(rc, fill);
                }
                break;
            }
            case PointMode.Lines:
                for (int i = 0; i + 1 < points.Length; i += 2)
                    c.DrawLine(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, line);
                break;
            default:
                for (int i = 0; i + 1 < points.Length; i++)
                    c.DrawLine(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, line);
                break;
        }
    }

    // A texture, plainly or through a table (TextureLookup); a released table draws nothing.
    private void Bitmap(Canvas c, ManagedTexture texture, TextureLookup lookup, in DrawOp op)
    {
        var table = lookup.IsNone ? null : _textures.Get(lookup.Table);
        if (!lookup.IsNone && table is null) return;
        var d = ToRect(op.Rect);
        if (d.IsEmpty) return;
        var (pixels, width, height) = (texture.Argb, texture.Width, texture.Height);
        if (op.ImageBlurSigma > 0) (pixels, width, height, d) = BlurBitmap(c, pixels, width, height, d, op.ImageBlurSigma);
        _imageShader.Set(pixels, width, height, d, op.Paint.FilterQuality != FilterQuality.None);
        if (table is not null) _imageShader.SetLookup(texture.Format, lookup, table);
        // The paint's alpha modulates the image, as an SKPaint's does.
        var p = new Paint { Color = op.Paint.Color.Argb | 0x00FFFFFFu, Shader = _imageShader };
        c.DrawRect(d, p);
    }

    /// <summary>
    /// An image drawn with an image blur. The blur (<see cref="ImageBlur"/>) is
    /// applied to the image's own pixels with sigma mapped into them,
    /// transparent beyond the edges; the result, grown by the blur's reach, is
    /// then drawn as the image would be. Exact for an unscaled draw, which is the
    /// only kind bibui makes.
    /// </summary>
    private static (uint[], int, int, Rect) BlurBitmap(Canvas c, uint[] pixels, int width, int height, Rect d, float sigma)
    {
        var m = c.Matrix;
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
    /// Frosted glass. The surface's device pixels in <paramref name="region"/>
    /// are snapshotted and drawn back blurred through the current transform, so
    /// the transform's translation moves the result and the clip; its scale
    /// scales sigma (bibui only translates here).
    /// </summary>
    private static void BackdropBlur(Canvas c, PixelRect region, RectF clip, float sigma)
    {
        var m = c.Matrix;
        float scale = MathF.Sqrt(MathF.Abs(m.GetDeterminant()));
        int dx = (int)MathF.Round(m.M31), dy = (int)MathF.Round(m.M32);
        var r = ToRect(clip);
        var deviceClip = new Rect(r.Left * m.M11 + m.M31, r.Top * m.M22 + m.M32, r.Right * m.M11 + m.M31, r.Bottom * m.M22 + m.M32).Sorted();
        c.DrawBackdropBlur(new IRect(region.Left, region.Top, region.Right, region.Bottom), deviceClip, sigma * scale, dx, dy);
    }

    // Glyph masks are placed in device pixels, so the text origin goes through
    // the transform. A uniform scale scales the strike; anything with rotation
    // or skew is not drawn (bibui never rotates text).
    private void Glyphs(Canvas c, FontFace face, float sizePx, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions,
        float x, float baselineY, Rgba color)
    {
        if (glyphs.IsEmpty) return;
        var m = c.Matrix;
        var origin = Vector2.Transform(new Vector2(x, baselineY), m);
        float s = m.M11;
        if (!(m.M12 == 0 && m.M21 == 0 && m.M11 == m.M22 && m.M11 > 0)) return;

        var strike = Strike(face, sizePx, s);
        if (_placed.Length < glyphs.Length) _placed = new PlacedGlyph[Math.Max(glyphs.Length, _placed.Length * 2)];
        int n;
        if (s == 1f) n = strike.Place(glyphs, positions, origin.X, origin.Y, _placed.AsSpan(0, glyphs.Length));
        else
        {
            Span<Vector2> scaled = glyphs.Length <= 256 ? stackalloc Vector2[glyphs.Length] : new Vector2[glyphs.Length];
            for (int i = 0; i < scaled.Length; i++) scaled[i] = positions[i] * s;
            n = strike.Place(glyphs, scaled, origin.X, origin.Y, _placed.AsSpan(0, glyphs.Length));
        }

        var table = MaskGamma.Table(new FtTextColor(color.R, color.G, color.B, color.A));
        for (int i = 0; i < n; i++)
        {
            var g = _placed[i];
            c.DrawA8(g.Mask.Coverage, g.Mask.Width, g.Mask.Height, g.X, g.Y, color.Argb, table);
        }
    }

    internal static FontStrike Strike(FontFace face, float sizePx, float scale = 1f) => face.Strike(sizePx * scale);

    // ── Paint ────────────────────────────────────────────────────────────────

    /// <summary>The op's paint as the rasterizer's.</summary>
    private Paint Convert(in DrawOp op, DrawList list, Canvas c)
    {
        var spec = op.Paint;
        var p = new Paint
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
            Dash = op.Dashed ? new DashPattern(op.Dash.On, op.Dash.Off, op.Dash.Phase) : null,
        };
        // Shadows blur by sigma mapped through the transform (times the
        // geometric mean of its axis scales, capped at 532); gradients shade per
        // pixel, and the paint's alpha modulates them.
        if (op.MaskBlurSigma > 0)
        {
            var m = c.Matrix;
            float d0 = new Vector2(m.M11, m.M12).Length(), d1 = new Vector2(m.M21, m.M22).Length();
            p.MaskBlurSigma = MathF.Min(op.MaskBlurSigma * MathF.Sqrt(d0 * d1), 532f);
        }
        if (op.Shading is { Present: true, ColorCount: > 0 } s) p.Shader = Gradient(s, list);
        return p;
    }

    private GradientShader Gradient(in Shading s, DrawList list)
    {
        var colors = list.Colors.Slice(s.ColorStart, s.ColorCount);
        var stops = list.Floats.Slice(s.StopStart, s.StopCount);
        var key = CacheKey.OfGradient(s.Type == GradientType.Radial ? 1 : 0, s.Start, s.End, s.Radius, colors, stops);
        if (_gradients.TryGetValue(key, out var g)) return g;
        if (_gradients.Count >= 256) _gradients.Clear();
        var argb = new uint[colors.Length];
        for (int i = 0; i < argb.Length; i++) argb[i] = colors[i].Argb;
        ReadOnlySpan<float> evenOr = stops.Length == argb.Length ? stops : default;
        g = s.Type == GradientType.Radial
            ? GradientShader.Radial(s.Start, s.Radius, argb, evenOr)
            : GradientShader.Linear(s.Start, s.End, argb, evenOr);
        _gradients[key] = g;
        return g;
    }

    internal static Rect ToRect(RectF r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public void Dispose() => _paths.Dispose();
}

/// <summary>
/// One texture, as this renderer keeps it: premultiplied ARGB with the format's
/// default look (<see cref="Argb"/>: what drawing, lookups, blurring and damage
/// read), made once and written in place. Draw thread only.
/// </summary>
internal sealed class ManagedTexture(TextureFormat format, TextureKind kind, int width, int height)
{
    public readonly TextureFormat Format = format;
    public readonly TextureKind Kind = kind;
    public readonly int Width = width, Height = height;
    public readonly uint[] Argb = new uint[width * height];

    /// <summary>Takes <paramref name="pixels"/>, rows <paramref name="rowBytes"/> apart, into <see cref="Argb"/>.</summary>
    public void Write(ReadOnlySpan<byte> pixels, int rowBytes)
    {
        // The default looks: R8 grey (r, r, r, 1), A8 white coverage (1, 1, 1, a),
        // RG8 (r, g, 0, 1), RGBA8 and BGRA8 as they are — premultiplied, as the canvas draws.
        for (int y = 0; y < Height; y++)
        {
            var raw = pixels.Slice(y * rowBytes);
            var argb = Argb.AsSpan(y * Width, Width);
            switch (Format)
            {
                case TextureFormat.R8:
                    for (int i = 0; i < argb.Length; i++) { uint v = raw[i]; argb[i] = 0xFF000000u | v << 16 | v << 8 | v; }
                    break;
                case TextureFormat.A8:
                    for (int i = 0; i < argb.Length; i++) { uint a = raw[i]; argb[i] = a << 24 | a << 16 | a << 8 | a; }
                    break;
                case TextureFormat.RG8:
                    for (int i = 0; i < argb.Length; i++) argb[i] = 0xFF000000u | (uint)raw[2 * i] << 16 | (uint)raw[2 * i + 1] << 8;
                    break;
                case TextureFormat.BGRA8:
                    for (int i = 0; i < argb.Length; i++)
                        argb[i] = PixelMath.Premul((uint)raw[4 * i + 3] << 24 | (uint)raw[4 * i + 2] << 16 | (uint)raw[4 * i + 1] << 8 | raw[4 * i]);
                    break;
                default:
                    for (int i = 0; i < argb.Length; i++)
                        argb[i] = PixelMath.Premul((uint)raw[4 * i + 3] << 24 | (uint)raw[4 * i] << 16 | (uint)raw[4 * i + 1] << 8 | raw[4 * i + 2]);
                    break;
            }
        }
    }
}

/// <summary>
/// A bitmap drawn into a destination rect: nearest or bilinear sampling,
/// clamped at the edges (not yet checked against Skia's low-quality sampling),
/// then the sample mapped. A plain draw's map is the identity; a lookup's takes
/// the sample's raw channels — what the format stores, straight — and draws the
/// table entry they pick, as the Skia lookup does.
/// </summary>
internal sealed class ImageShader : Shader
{
    private uint[] _px = [];
    private int _w, _h;
    private Rect _dest;
    private bool _bilinear;
    private Matrix3x2 _deviceToImage;

    // The lookup, when there is one: _table is null for a plain draw.
    private uint[]? _table;
    private int _tw, _th;
    private uint _rawMask;          // ARGB bytes the format really stores; the rest read 0
    private bool _straight;         // RGBA8/BGRA8: un-premultiply before reading
    private int _xShift, _yShift;   // bit position of the picking channel in ARGB; -1 picks entry 0

    public void Set(uint[] pixels, int width, int height, Rect dest, bool bilinear)
    {
        _px = pixels;
        _w = width;
        _h = height;
        _dest = dest;
        _bilinear = bilinear;
        _table = null;
    }

    public void SetLookup(TextureFormat format, TextureLookup lookup, ManagedTexture table)
    {
        (_table, _tw, _th) = (table.Argb, table.Width, table.Height);
        // The look of each format (ManagedTexture.Write) holds its raw channels
        // where they belong; what it fills in that the format lacks is masked off.
        _rawMask = format switch
        {
            TextureFormat.R8 => 0xFFFF0000u,
            TextureFormat.A8 => 0xFF000000u,
            TextureFormat.RG8 => 0xFFFFFF00u,
            _ => 0xFFFFFFFFu,
        };
        _straight = format is TextureFormat.RGBA8 or TextureFormat.BGRA8;
        (_xShift, _yShift) = (Shift(lookup.X), Shift(lookup.Y));
    }

    private static int Shift(TextureChannel channel) => channel switch
    {
        TextureChannel.R => 16,
        TextureChannel.G => 8,
        TextureChannel.B => 0,
        TextureChannel.A => 24,
        _ => -1,
    };

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
            uint s = _bilinear ? Bilinear(p.X - 0.5f, p.Y - 0.5f) : At((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));
            dst[i] = _table is null ? s : Lookup(s);
        }
    }

    private uint Lookup(uint s)
    {
        if (_straight) s = Unpremul(s);
        s &= _rawMask;
        int col = _xShift < 0 ? 0 : Math.Min((int)(s >> _xShift & 0xFF), _tw - 1);
        int row = _yShift < 0 ? 0 : Math.Min((int)(s >> _yShift & 0xFF), _th - 1);
        return _table![row * _tw + col];
    }

    private static uint Unpremul(uint p)
    {
        uint a = p >> 24;
        if (a is 0 or 255) return p;
        uint Ch(int sh) => Math.Min(255u, (((p >> sh) & 0xFF) * 255 + a / 2) / a) << sh;
        return a << 24 | Ch(16) | Ch(8) | Ch(0);
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
