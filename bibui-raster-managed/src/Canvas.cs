using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed;

/// <summary>
/// Draws into a <see cref="Surface"/>: shapes under an affine transform, a
/// clip stack of rects, rrects and paths, and offscreen layers composited back
/// at an opacity. Save/Restore bracket all three, as a stack.
///
/// <para>Single-threaded and deterministic: the same calls on the same surface
/// give the same bytes on every run and machine. Nothing allocates per frame
/// once the scratch buffers (edges, accumulator, masks, layers) have grown to
/// the frame's high-water mark.</para>
///
/// <para>Fast paths: an axis-aligned rect is exact analytic coverage per edge;
/// an rrect (and an rrect ring, which is how an rrect is stroked) runs its
/// straight middle band as spans and only its corners through the scanline
/// core. Everything else is a path, filled with exact coverage.
/// Strokes of at most one device pixel are hairlines.</para>
///
/// <para>Where it reproduces Skia 2.88 exactly — hairlines, the hairline test
/// (<c>SkDrawTreatAsHairline</c>) and its alpha scaling, rect strokes, cap
/// extension of dashed lines (<c>extend_pts</c>), the A8 mask blend, layers
/// composited back through the clip, and in
/// <see cref="Managed.Antialiasing.BaselineCompatible"/> mode the choice between
/// supersampled and analytic coverage (<c>SkScan_AntiPath.cpp</c>,
/// <c>ShouldUseAAA</c>) — it does so because the fixture baselines are drawn by
/// the Skia backend, and the managed backend is verified against them pixel for
/// pixel.</para>
/// </summary>
public sealed unsafe partial class Canvas // strokes and dashes in Stroke/Canvas.Stroke.cs; blur in Canvas.Effects.cs
{
    /// <summary>
    /// An offscreen layer as big as the clip it was saved under — but only the
    /// part anything was drawn into (<see cref="Dirty"/>) is ever cleared or
    /// composited, so an opacity layer over one small widget costs that
    /// widget's pixels, not the window's.
    /// </summary>
    private sealed class Layer
    {
        public uint[] Buffer = [];
        public uint* Ptr;
        public IRect Bounds;
        public IRect Dirty;
        public byte Alpha;
        public LayerMode Mode;
        // A matrix layer's 4×5, copied in at save; the array lives with the pooled layer.
        public bool HasMatrix;
        public float[] Matrix = new float[20];

        public uint* Origin => Ptr - (long)Bounds.Top * Bounds.Width - Bounds.Left;

        public void Reset(IRect bounds)
        {
            int n = bounds.Width * bounds.Height;
            if (Buffer.Length < n)
            {
                Buffer = GC.AllocateArray<uint>(n, pinned: true);
                Ptr = (uint*)Unsafe.AsPointer(ref Buffer[0]);
            }
            Bounds = bounds;
            Dirty = default;
        }

        /// <summary>Grows the cleared region to cover <paramref name="b"/>, clearing only what is new.</summary>
        public void Touch(IRect b)
        {
            b = b.Intersect(Bounds);
            if (b.IsEmpty) return;
            var d = Dirty;
            if (!d.IsEmpty && b.Left >= d.Left && b.Top >= d.Top && b.Right <= d.Right && b.Bottom <= d.Bottom) return;
            var u = d.IsEmpty ? b : new IRect(
                Math.Min(d.Left, b.Left), Math.Min(d.Top, b.Top), Math.Max(d.Right, b.Right), Math.Max(d.Bottom, b.Bottom));
            uint* o = Origin;
            int stride = Bounds.Width;
            for (int y = u.Top; y < u.Bottom; y++)
            {
                uint* row = o + (long)y * stride;
                if (d.IsEmpty || y < d.Top || y >= d.Bottom) new Span<uint>(row + u.Left, u.Width).Clear();
                else
                {
                    if (d.Left > u.Left) new Span<uint>(row + u.Left, d.Left - u.Left).Clear();
                    if (u.Right > d.Right) new Span<uint>(row + d.Right, u.Right - d.Right).Clear();
                }
            }
            Dirty = u;
        }
    }

    private struct State
    {
        public Matrix3x2 Matrix;
        public IRect Clip;
        public CoverageMask? Mask;
        public bool OwnsMask;
        public Layer? Layer;
        public bool OwnsLayer;
    }

    private readonly Surface _surface;
    private readonly Rasterizer _raster = new();
    private readonly uint[] _shadeScratch = new uint[256];
    private readonly Path _scratch = new();
    private readonly Path _stroked = new();
    private readonly List<CoverageMask> _maskPool = [];
    private readonly List<Layer> _layerPool = [];
    private State[] _stack = new State[16];
    private int _depth;
    private State _s;

    public Canvas(Surface surface)
    {
        _surface = surface;
        _s.Matrix = Matrix3x2.Identity;
        _s.Clip = surface.Bounds;
    }

    public Surface Surface => _surface;

    /// <summary>
    /// Turns strokes wider than a hairline into outlines:
    /// <see cref="PathStroker"/> unless replaced. Set to null, such
    /// strokes throw <see cref="NotSupportedException"/>.
    /// </summary>
    public IPathStroker? Stroker { get; set; } = new PathStroker();

    /// <summary>Blurs coverage for <see cref="Paint.MaskBlurSigma"/>. Without one, blurred draws throw <see cref="NotSupportedException"/>.</summary>
    public IMaskBlur? MaskBlur { get; set; }

    /// <summary>
    /// How shape edges are antialiased: <see cref="Managed.Antialiasing.Exact"/>
    /// area coverage (the default), or <see cref="Managed.Antialiasing.BaselineCompatible"/>,
    /// which supersamples or snaps edges as the baselines were drawn. Hairlines
    /// and rects are drawn the same in both.
    /// </summary>
    public Antialiasing Antialiasing { get; set; } = Antialiasing.Exact;

    // ── State ──────────────────────────────────────────────────────────────

    /// <summary>How many saves are open, plus one: 1 with none open.</summary>
    public int SaveCount => _depth + 1;

    public Matrix3x2 Matrix => _s.Matrix;

    /// <summary>The pixels the current clip can touch. Empty when everything is clipped.</summary>
    public IRect DeviceClipBounds => _s.Clip;

    public void Save()
    {
        if (_depth == _stack.Length) Array.Resize(ref _stack, _depth * 2);
        _stack[_depth++] = _s;
        _s.OwnsMask = false;
        _s.OwnsLayer = false;
    }

    /// <summary>
    /// Saves, and starts an offscreen layer the size of the current clip
    /// bounds (or <paramref name="bounds"/>, in local coordinates, within
    /// them). The matching <see cref="Restore"/> composites it back at
    /// <paramref name="alpha"/>/255 through the clip in force there.
    /// </summary>
    public void SaveLayer(byte alpha, Rect? bounds = null)
    {
        Save();
        var lb = _s.Clip;
        if (bounds is { } b) lb = lb.Intersect(MapRect(b).RoundOut());
        Layer layer = RentLayer();
        layer.Reset(lb.IsEmpty ? new IRect(0, 0, 1, 1) : lb);
        layer.Alpha = alpha;
        layer.Mode = LayerMode.SrcOver;
        layer.HasMatrix = false;
        _s.Layer = layer;
        _s.OwnsLayer = true;
        _s.Clip = lb;
    }

    /// <summary>
    /// <see cref="SaveLayer(byte, Rect?)"/> composited with <paramref name="mode"/>
    /// instead of src-over.
    /// </summary>
    public void SaveLayer(LayerMode mode, Rect? bounds = null)
    {
        SaveLayer((byte)255, bounds);
        _s.Layer!.Mode = mode;
    }

    /// <summary>
    /// <see cref="SaveLayer(byte, Rect?)"/> whose pixels go through the
    /// row-major 4×5 colour matrix <paramref name="matrix"/> on the way back:
    /// on straight RGBA in 0..1, the fifth column a translation in 0..1.
    /// </summary>
    public void SaveLayer(ReadOnlySpan<float> matrix, Rect? bounds = null)
    {
        SaveLayer((byte)255, bounds);
        var layer = _s.Layer!;
        layer.HasMatrix = true;
        matrix[..20].CopyTo(layer.Matrix);
    }

    public void SaveLayer(float opacity, Rect? bounds = null)
        => SaveLayer((byte)Math.Clamp((int)MathF.Round(opacity * 255f), 0, 255), bounds);

    /// <summary>Pops the last save; extra restores are ignored.</summary>
    public void Restore()
    {
        if (_depth == 0) return;
        var cur = _s;
        _s = _stack[--_depth];
        if (cur.OwnsMask && cur.Mask is not null) _maskPool.Add(cur.Mask);
        if (cur.OwnsLayer && cur.Layer is not null)
        {
            Composite(cur.Layer);
            _layerPool.Add(cur.Layer);
        }
    }

    public void RestoreToCount(int count)
    {
        while (SaveCount > Math.Max(1, count)) Restore();
    }

    public void Translate(float dx, float dy) => _s.Matrix = Matrix3x2.CreateTranslation(dx, dy) * _s.Matrix;

    public void Scale(float sx, float sy) => _s.Matrix = Matrix3x2.CreateScale(sx, sy) * _s.Matrix;

    /// <summary>Pre-multiplies the transform: <paramref name="m"/> applies first.</summary>
    public void Concat(in Matrix3x2 m) => _s.Matrix = m * _s.Matrix;

    public void SetMatrix(in Matrix3x2 m) => _s.Matrix = m;

    public void ResetMatrix() => _s.Matrix = Matrix3x2.Identity;

    // ── Clip ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Intersects the clip with a rect. Aliased (the default, and what bibui
    /// asks for) snaps each edge to the nearest pixel boundary, so the clip
    /// stays a plain pixel rect. Antialiased keeps fractional edges as coverage.
    /// </summary>
    public void ClipRect(Rect rect, bool antiAlias = false)
    {
        if (IsAxisAligned(_s.Matrix))
        {
            var d = MapRect(rect);
            if (!antiAlias || IsIntegral(d))
            {
                _s.Clip = _s.Clip.Intersect(d.Round());
                return;
            }
        }
        _scratch.Rewind();
        _scratch.AddRect(rect);
        ClipCoverage(_scratch, simple: true);
    }

    /// <summary>Intersects the clip with a rounded rect, antialiased.</summary>
    public void ClipRRect(in RRect rrect)
    {
        if (rrect.IsRect) { ClipRect(rrect.Rect, antiAlias: true); return; }
        _scratch.Rewind();
        _scratch.AddRRect(rrect);
        ClipCoverage(_scratch, simple: true);
    }

    /// <summary>Intersects the clip with a path under its fill rule, antialiased.</summary>
    public void ClipPath(Path path) => ClipCoverage(path, simple: false);

    private void ClipCoverage(Path path, bool simple)
    {
        var dev = DeviceBounds(path);
        var bounds = dev.IsEmpty ? default : _s.Clip.Intersect(dev.RoundOut());
        if (bounds.IsEmpty)
        {
            _s.Clip = default;
            return;
        }
        var mask = RentMask(bounds);
        var prev = _s.Mask;
        var sink = new MaskSink
        {
            Origin = mask.Origin, Stride = mask.Stride,
            Prev = prev is null ? null : prev.Origin, PrevStride = prev?.Stride ?? 0,
        };
        var mode = LoadPath(path, bounds, exact: !simple);
        // A clip rect or rrect is one clockwise contour (anticlockwise under a flip).
        if (mode == RasterMode.Supersample && simple)
            _raster.FillSupersampledSimple(ref sink, leftDir: _s.Matrix.GetDeterminant() < 0 ? 1 : -1);
        else if (mode == RasterMode.Supersample) _raster.FillSupersampled(ref sink, path.FillRule);
        else _raster.Fill(ref sink, path.FillRule, exact: mode == RasterMode.Exact);
        if (_s.OwnsMask && prev is not null) _maskPool.Add(prev);
        _s.Mask = mask;
        _s.OwnsMask = true;
        _s.Clip = bounds;
    }

    // ── Drawing ────────────────────────────────────────────────────────────

    /// <summary>Fills the current layer (or the whole surface) with a colour, ignoring the clip and transform.</summary>
    public void Clear(uint argb)
    {
        uint pm = PmFor(argb);
        if (_s.Layer is { } layer)
        {
            new Span<uint>(layer.Ptr, layer.Bounds.Width * layer.Bounds.Height).Fill(pm);
            layer.Dirty = layer.Bounds;
            return;
        }
        if (_surface.RowPixels == _surface.Width)
        {
            new Span<uint>(_surface.Base, _surface.Width * _surface.Height).Fill(pm);
            return;
        }
        for (int y = 0; y < _surface.Height; y++)
            new Span<uint>(_surface.Base + (long)y * _surface.RowPixels, _surface.Width).Fill(pm);
    }

    /// <summary>
    /// Sets the device pixels of <paramref name="rect"/> (clamped to the surface) to a
    /// colour, translucent or not, replacing what was there: what <see cref="Clear"/> does
    /// to the whole surface, done to part of it. The clip, the transform and any layer are
    /// ignored: it writes the surface.
    /// </summary>
    public void ClearRect(IRect rect, uint argb)
    {
        var b = rect.Intersect(new IRect(0, 0, _surface.Width, _surface.Height));
        if (b.IsEmpty) return;
        uint pm = PmFor(argb);
        for (int y = b.Top; y < b.Bottom; y++)
            new Span<uint>(_surface.Base + (long)y * _surface.RowPixels + b.Left, b.Width).Fill(pm);
    }

    public void DrawRect(Rect rect, in Paint paint)
    {
        rect = rect.Sorted();
        if (_s.Clip.IsEmpty) return;
        if (paint.MaskBlurSigma > 0) { DrawBlurredRect(rect, paint); return; } // Canvas.Effects.cs
        bool aligned = IsAxisAligned(_s.Matrix);

        if (paint.Style != PaintStyle.Stroke)
        {
            var r = paint.Style == PaintStyle.StrokeAndFill ? rect.Outset(paint.StrokeWidth * 0.5f, paint.StrokeWidth * 0.5f) : rect;
            if (aligned) { var op = new RectOp(MapRect(r)); Run(paint, ref op); }
            else { _scratch.Rewind(); _scratch.AddRect(r); FillPathCore(_scratch, paint, exact: false); }
            return;
        }

        // A stroked rect is a frame unless it is a true hairline (width 0).
        if (paint.StrokeWidth == 0 && paint.Dash is null) // a dashed one dashes first
        {
            Span<Vector2> pts =
            [
                new(rect.Left, rect.Top), new(rect.Right, rect.Top), new(rect.Right, rect.Bottom),
                new(rect.Left, rect.Bottom), new(rect.Left, rect.Top),
            ];
            HairPolyline(pts, paint, 1f);
            return;
        }
        if (aligned && paint.Join == LineJoin.Miter && paint.Dash is null)
        {
            float hx = paint.StrokeWidth * 0.5f;
            var outer = MapRect(rect.Outset(hx, hx));
            var inner = MapRect(rect.Outset(-hx, -hx));
            bool solid = inner.IsEmpty || rect.Width < paint.StrokeWidth || rect.Height < paint.StrokeWidth;
            var op = new RectOp(outer, solid ? default : inner);
            Run(paint, ref op);
            return;
        }
        _scratch.Rewind();
        _scratch.AddRect(rect);
        StrokePathCore(_scratch, paint);
    }

    public void DrawRoundRect(Rect rect, float rx, float ry, in Paint paint)
    {
        if (rx > 0 && ry > 0) DrawRRect(new RRect(rect, rx, ry), paint);
        else DrawRect(rect, paint);
    }

    public void DrawRRect(in RRect rr, in Paint paint)
    {
        if (rr.IsRect) { DrawRect(rr.Rect, paint); return; }
        if (rr.IsOval) { DrawOval(rr.Rect, paint); return; }
        if (_s.Clip.IsEmpty) return;
        if (paint.MaskBlurSigma > 0) { DrawBlurredRRect(rr, paint); return; } // Canvas.Effects.cs
        bool aligned = IsAxisAligned(_s.Matrix);

        if (paint.Style == PaintStyle.Fill)
        {
            if (aligned) FillRRectFast(rr, paint);
            else { _scratch.Rewind(); _scratch.AddRRect(rr); FillPathCore(_scratch, paint, exact: false); }
            return;
        }

        if (paint.Dash is null && TreatAsHairline(paint, _s.Matrix, out float coverage) && paint.Style == PaintStyle.Stroke)
        {
            _scratch.Rewind();
            _scratch.AddRRect(rr);
            HairPath(_scratch, paint, coverage);
            return;
        }

        float h = paint.StrokeWidth * 0.5f;
        var outer = new RRect(rr.Rect.Outset(h, h), rr.Rx + h, rr.Ry + h);
        if (paint.Style == PaintStyle.StrokeAndFill)
        {
            if (aligned) FillRRectFast(outer, paint);
            else { _scratch.Rewind(); _scratch.AddRRect(outer); FillPathCore(_scratch, paint, exact: false); }
            return;
        }
        // An rrect's sides meet its corners tangentially, so the join does not
        // matter: the stroke is the ring between the rrect outset and inset by
        // half the width (the inset corner square once the radius is used up).
        if (aligned && paint.Dash is null)
        {
            StrokeRRectFast(rr, h, paint);
            return;
        }
        _scratch.Rewind();
        _scratch.AddRRect(rr);
        StrokePathCore(_scratch, paint);
    }

    public void DrawOval(Rect oval, in Paint paint)
    {
        oval = oval.Sorted();
        if (_s.Clip.IsEmpty || oval.IsEmpty) return;
        _scratch.Rewind();
        _scratch.AddOval(oval);
        if (paint.MaskBlurSigma > 0) { DrawBlurred(_scratch, paint); return; }
        if (paint.Style == PaintStyle.Fill) { FillPathCore(_scratch, paint, exact: false); return; }
        if (paint.Style == PaintStyle.Stroke && paint.Dash is null && TreatAsHairline(paint, _s.Matrix, out float coverage))
        {
            HairPath(_scratch, paint, coverage);
            return;
        }
        float h = paint.StrokeWidth * 0.5f;
        if (paint.Style == PaintStyle.StrokeAndFill)
        {
            _scratch.Rewind();
            _scratch.AddOval(oval.Outset(h, h));
            FillPathCore(_scratch, paint, exact: false);
            return;
        }
        if (oval.Width == oval.Height && paint.Dash is null)
        {
            // A circle's stroke is the ring between two circles.
            _scratch.Rewind();
            _scratch.AddOval(oval.Outset(h, h));
            var inner = oval.Outset(-h, -h);
            if (!inner.IsEmpty) _scratch.AddOval(inner, clockwise: false);
            FillPathCore(_scratch, paint, exact: false);
            return;
        }
        StrokePathCore(_scratch, paint);
    }

    public void DrawCircle(float cx, float cy, float radius, in Paint paint)
        => DrawOval(new Rect(cx - radius, cy - radius, cx + radius, cy + radius), paint);

    /// <summary>Fills or strokes a path. A fill is exact under its fill rule, however the path overlaps itself.</summary>
    public void DrawPath(Path path, in Paint paint)
    {
        if (_s.Clip.IsEmpty || path.IsEmpty) return;
        if (paint.MaskBlurSigma > 0)
        {
            if (paint.Style == PaintStyle.Fill) { DrawBlurred(path, paint); return; }
            DrawBlurredStroke(path, paint);
            return;
        }
        // Stroke-and-fill is one outline (the stroker adds the path), filled once.
        if (paint.Style == PaintStyle.Fill || (paint.Style == PaintStyle.StrokeAndFill && !(paint.StrokeWidth > 0)))
        {
            FillPathCore(path, paint, exact: true);
            return;
        }
        if (paint.Dash is null && TreatAsHairline(paint, _s.Matrix, out float coverage))
        {
            HairPath(path, paint, coverage);
            return;
        }
        StrokePathCore(path, paint);
    }

    /// <summary>
    /// A line, always stroked. At most one device pixel wide it is a hairline;
    /// wider, a butt or square cap is drawn as the rectangle it is, and a round
    /// cap, or a dash, by the stroker.
    /// </summary>
    public void DrawLine(float x0, float y0, float x1, float y1, in Paint paint)
    {
        if (_s.Clip.IsEmpty) return;
        var p = paint;
        p.Style = PaintStyle.Stroke;
        if (p.Dash is { } dash && DashedLineAsRects(x0, y0, x1, y1, dash, p)) return;
        if (p.Dash is null && TreatAsHairline(p, _s.Matrix, out float coverage))
        {
            var a = Vector2.Transform(new Vector2(x0, y0), _s.Matrix);
            var b = Vector2.Transform(new Vector2(x1, y1), _s.Matrix);
            if (p.Cap != LineCap.Butt)
            {
                // Caps extend the line: square ones by ½ px, round ones by π/8.
                float outset = p.Cap == LineCap.Square ? 0.5f : MathF.PI / 8;
                var dir = b - a;
                float len = dir.Length();
                dir = len > 0 ? dir / len : new Vector2(1, 0);
                a -= dir * outset;
                b += dir * outset;
            }
            DrawHairDevice(a, b, p, coverage);
            return;
        }
        if (p.Dash is null && p.Cap != LineCap.Round)
        {
            var a = new Vector2(x0, y0);
            var b = new Vector2(x1, y1);
            var d = b - a;
            float len = d.Length();
            if (len == 0) return;
            var u = d / len;
            var n = new Vector2(-u.Y, u.X) * (p.StrokeWidth * 0.5f);
            if (p.Cap == LineCap.Square) { a -= u * (p.StrokeWidth * 0.5f); b += u * (p.StrokeWidth * 0.5f); }
            _scratch.Rewind();
            _scratch.AddPolygon([a + n, b + n, b - n, a - n]);
            var fill = p;
            fill.Style = PaintStyle.Fill;
            FillPathCore(_scratch, fill, exact: false);
            return;
        }
        _scratch.Rewind();
        _scratch.MoveTo(x0, y0);
        _scratch.LineTo(x1, y1);
        StrokePathCore(_scratch, p);
    }

    /// <summary>Paints a coverage mask (device space) with the paint's colour or shader, through the clip.</summary>
    public void DrawMask(CoverageMask mask, in Paint paint)
    {
        var op = new MaskOp(mask);
        Run(paint, ref op);
    }

    /// <summary>
    /// Composites an 8-bit coverage mask whose top-left pixel is device
    /// (<paramref name="x"/>, <paramref name="y"/>) — a glyph — in a solid colour,
    /// through the clip, into the current layer. The transform is not applied:
    /// the caller places the mask. Each coverage is first mapped through
    /// <paramref name="coverageMap"/> (256 entries, e.g. a text gamma table) if
    /// given, then blended:
    /// <c>l = pm·(k+1)/256</c>, <c>d = l + d·(256 − l.a)/256</c>.
    /// </summary>
    public void DrawA8(ReadOnlySpan<byte> coverage, int width, int height, int x, int y, uint argb, ReadOnlySpan<byte> coverageMap = default)
    {
        if (width <= 0 || height <= 0) return;
        if (coverage.Length < width * height) throw new ArgumentException("Too few coverage bytes.", nameof(coverage));
        if (!coverageMap.IsEmpty && coverageMap.Length < 256) throw new ArgumentException("A coverage map has 256 entries.", nameof(coverageMap));
        var b = new IRect(x, y, x + width, y + height).Intersect(_s.Clip);
        if (b.IsEmpty) return;
        _s.Layer?.Touch(b);
        uint pm = PmFor(argb);
        if (pm == 0) return;
        var t = Target();
        var mask = _s.Mask;
        bool mapped = !coverageMap.IsEmpty;
        for (int yy = b.Top; yy < b.Bottom; yy++)
        {
            var src = coverage.Slice((yy - y) * width, width);
            uint* d = t.Origin + (long)yy * t.Stride;
            byte* m = mask is null ? null : mask.Origin + (long)yy * mask.Stride;
            for (int xx = b.Left; xx < b.Right; xx++)
            {
                uint k = src[xx - x];
                if (k == 0) continue;
                if (mapped) k = coverageMap[(int)k];
                if (m != null) k = PixelMath.MulDiv255Round(k, m[xx]);
                if (k == 0) continue;
                uint left = PixelMath.MulQ(pm, k + 1);
                d[xx] = left + PixelMath.MulQ(d[xx], 256 - (left >> 24));
            }
        }
    }

    // ── Cores ──────────────────────────────────────────────────────────────

    private void FillPathCore(Path path, in Paint paint, bool exact)
    {
        var mode = LoadPath(path, _s.Clip, exact);
        if (_raster.IsEmpty) return;
        var op = RasterOp.Plain(path.FillRule, mode);
        Run(paint, ref op);
    }

    /// <summary>Flattens a path into the rasterizer and says how to scan it, per <see cref="Antialiasing"/>.</summary>
    private RasterMode LoadPath(Path path, IRect clip, bool exact)
    {
        bool compat = Antialiasing == Antialiasing.BaselineCompatible;
        bool ssaa = compat && !UsesAnalytic(path, _s.Matrix);
        _raster.Reset(clip, snapY: compat && !ssaa);
        if (ssaa) SupersampleEdges.Fill(path, _s.Matrix, _raster); // coarse chords, where the baselines were supersampled
        else Flatten.Fill(path, _s.Matrix, _raster);
        return ssaa ? RasterMode.Supersample : exact ? RasterMode.Exact : RasterMode.Simple;
    }

    /// <summary>
    /// Whether a path gets analytic coverage rather than supersampling: analytic unless the path
    /// is "complex" — n²·(mean length of its first 7 segments)² / diagonal²,
    /// per pixel of height, at least 0.25. Fewer than 8 points is never complex.
    /// </summary>
    private static bool UsesAnalytic(Path path, in Matrix3x2 m)
    {
        var pts = path.Points;
        if (pts.Length < 8) return true;
        Span<Vector2> first = stackalloc Vector2[8];
        for (int i = 0; i < 8; i++) first[i] = Vector2.Transform(pts[i], m);
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var p in pts)
        {
            var d = Vector2.Transform(p, m);
            x0 = MathF.Min(x0, d.X); x1 = MathF.Max(x1, d.X);
            y0 = MathF.Min(y0, d.Y); y1 = MathF.Max(y1, d.Y);
        }
        return PathComplexity(first, pts.Length, x1 - x0, y1 - y0) < 0.25f;
    }

    private static float PathComplexity(ReadOnlySpan<Vector2> first8, int n, float w, float h)
    {
        if (!(w > 0) || !(h > 0)) return -1;
        float sum = 0;
        for (int i = 1; i < 8; i++) sum += Vector2.Distance(first8[i - 1], first8[i]);
        float avg = sum / 7;
        float intersections = (float)n * n * avg * avg / (w * w + h * h);
        float complexity = intersections / h;
        return float.IsNaN(complexity) ? -1 : complexity;
    }

    /// <summary><see cref="UsesAnalytic"/> for a device-space rrect, as its 13-point path.</summary>
    private static bool UsesAnalyticRRect(Rect d, float rx, float ry)
    {
        Span<Vector2> p =
        [
            new(d.Left, d.Bottom - ry), new(d.Left, d.Top + ry), new(d.Left, d.Top), new(d.Left + rx, d.Top),
            new(d.Right - rx, d.Top), new(d.Right, d.Top), new(d.Right, d.Top + ry), new(d.Right, d.Bottom - ry),
        ];
        return PathComplexity(p, 13, d.Width, d.Height) < 0.25f;
    }

    private void FillRRectFast(in RRect rr, in Paint paint)
    {
        var d = MapRect(rr.Rect);
        float rx = rr.Rx * MathF.Abs(_s.Matrix.M11), ry = rr.Ry * MathF.Abs(_s.Matrix.M22);
        bool compat = Antialiasing == Antialiasing.BaselineCompatible;
        bool ssaa = compat && !UsesAnalyticRRect(d, rx, ry);
        _raster.Reset(_s.Clip, snapY: compat && !ssaa);
        AddRRectEdges(d, rx, ry, reverse: false);
        var op = RasterOp.Plain(FillRule.NonZero, ssaa ? RasterMode.SupersampleSimple : RasterMode.Simple);
        op.MidStart = (int)MathF.Ceiling(d.Top + ry);
        op.MidEnd = (int)MathF.Floor(d.Bottom - ry);
        op.SetSpan(d.Left, d.Right);
        Run(paint, ref op);
    }

    private void StrokeRRectFast(in RRect rr, float h, in Paint paint)
    {
        var d = MapRect(rr.Rect);
        float sx = MathF.Abs(_s.Matrix.M11), sy = MathF.Abs(_s.Matrix.M22);
        float rx = rr.Rx * sx, ry = rr.Ry * sy, hx = h * sx, hy = h * sy;
        var outer = d.Outset(hx, hy);
        var inner = d.Outset(-hx, -hy);
        float orx = rx + hx, ory = ry + hy;
        float irx = rx - hx, iry = ry - hy;
        if (irx <= 0 || iry <= 0) irx = iry = 0;
        bool hasInner = !inner.IsEmpty;
        // A stroked rrect is a path of many points, which is always supersampled.
        bool ssaa = Antialiasing == Antialiasing.BaselineCompatible;
        _raster.Reset(_s.Clip);
        AddRRectEdges(outer, orx, ory, reverse: false);
        var op = RasterOp.Plain(FillRule.NonZero, ssaa ? RasterMode.SupersampleSimple : RasterMode.Simple);
        if (hasInner)
        {
            AddRRectEdges(inner, irx, iry, reverse: true);
            op.MidStart = (int)MathF.Ceiling(MathF.Max(outer.Top + ory, inner.Top + iry));
            op.MidEnd = (int)MathF.Floor(MathF.Min(outer.Bottom - ory, inner.Bottom - iry));
            op.SetRing(outer.Left, outer.Right, inner.Left, inner.Right);
        }
        else
        {
            op.MidStart = (int)MathF.Ceiling(outer.Top + ory);
            op.MidEnd = (int)MathF.Floor(outer.Bottom - ory);
            op.SetSpan(outer.Left, outer.Right);
        }
        Run(paint, ref op);
    }

    // DrawBlurred lives in Canvas.Effects.cs.

    // ── Hairlines ──────────────────────────────────────────────────────────

    /// <summary>
    /// A stroke is a hairline when its
    /// width is 0, or when it is antialiased and the transform maps it to at
    /// most one device pixel each way — then <paramref name="coverage"/> is
    /// that width, which scales the paint's alpha.
    /// </summary>
    internal static bool TreatAsHairline(in Paint paint, in Matrix3x2 m, out float coverage)
    {
        coverage = 1f;
        if (paint.Style != PaintStyle.Stroke) return false;
        float w = paint.StrokeWidth;
        if (w == 0) return true;
        if (paint.Aliased) return false;
        float len0 = FastLen(m.M11 * w, m.M12 * w);
        float len1 = FastLen(m.M21 * w, m.M22 * w);
        if (len0 <= 1f && len1 <= 1f)
        {
            coverage = (len0 + len1) * 0.5f;
            return true;
        }
        return false;

        static float FastLen(float x, float y)
        {
            x = MathF.Abs(x);
            y = MathF.Abs(y);
            if (x < y) (x, y) = (y, x);
            return x + y * 0.5f;
        }
    }

    private HairlineBlitter MakeHairline(in Paint paint, float coverage)
    {
        uint argb = paint.Color;
        if (coverage < 1f)
        {
            // A thin hairline fades: alpha · ⌊coverage·256⌋ / 256.
            uint scale = (uint)(int)(coverage * 256);
            argb = (argb & 0x00FFFFFF) | (((argb >> 24) * scale >> 8) << 24);
        }
        var t = Target();
        var mask = _s.Mask;
        return new HairlineBlitter
        {
            Origin = t.Origin, Stride = t.Stride, Clip = _s.Clip,
            Mask = mask is null ? null : mask.Origin, MaskStride = mask?.Stride ?? 0,
            Pm = PmFor(argb),
        };
    }

    private void HairPath(Path path, in Paint paint, float coverage)
    {
        var b = MakeHairline(paint, coverage);
        if ((b.Pm >> 24) == 0) return;
        TouchLayer(DeviceBounds(path));
        Flatten.Hairline(path, _s.Matrix, ref b);
    }

    private void HairPolyline(ReadOnlySpan<Vector2> pts, in Paint paint, float coverage)
    {
        var b = MakeHairline(paint, coverage);
        if ((b.Pm >> 24) == 0) return;
        if (_s.Layer is not null)
        {
            var r = new Rect(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            foreach (var p in pts)
            {
                var d = Vector2.Transform(p, _s.Matrix);
                r = new Rect(MathF.Min(r.Left, d.X), MathF.Min(r.Top, d.Y), MathF.Max(r.Right, d.X), MathF.Max(r.Bottom, d.Y));
            }
            TouchLayer(r);
        }
        for (int i = 0; i + 1 < pts.Length; i++)
            b.Line(Vector2.Transform(pts[i], _s.Matrix), Vector2.Transform(pts[i + 1], _s.Matrix));
    }

    private void DrawHairDevice(Vector2 a, Vector2 b, in Paint paint, float coverage)
    {
        var bl = MakeHairline(paint, coverage);
        if ((bl.Pm >> 24) == 0) return;
        TouchLayer(new Rect(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y)));
        bl.Line(a, b);
    }

    /// <summary>A hairline reaches a pixel either side of its device bounds.</summary>
    private void TouchLayer(Rect device)
    {
        if (_s.Layer is not { } layer) return;
        var r = device.RoundOut();
        layer.Touch(new IRect(r.Left - 2, r.Top - 2, r.Right + 2, r.Bottom + 2).Intersect(_s.Clip));
    }

    // ── Coverage operations, generic over the sink ──────────────────────────

    private interface ICoverageOp
    {
        void Run<TSink>(Canvas c, ref TSink sink) where TSink : struct, ICoverageSink;

        /// <summary>The device pixels the op can write (conservative).</summary>
        IRect Bounds(Canvas c);
    }

    private void Run<TOp>(in Paint paint, ref TOp op) where TOp : struct, ICoverageOp
    {
        _s.Layer?.Touch(op.Bounds(this).Intersect(_s.Clip));
        var t = Target();
        var mask = _s.Mask;
        if (paint.Shader is null)
        {
            uint pm = PmFor(paint.Color);
            if (pm == 0) return;
            var sink = new SolidSink
            {
                Origin = t.Origin, Stride = t.Stride, Pm = pm,
                Mask = mask is null ? null : mask.Origin, MaskStride = mask?.Stride ?? 0,
            };
            op.Run(this, ref sink);
        }
        else
        {
            if (paint.Alpha == 0) return;
            paint.Shader.Begin(_s.Matrix);
            var sink = new ShaderSink
            {
                Origin = t.Origin, Stride = t.Stride, Shader = paint.Shader, PaintAlpha = paint.Alpha,
                SwapRB = _surface.Order == PixelOrder.Rgba8888, Scratch = _shadeScratch,
                Mask = mask is null ? null : mask.Origin, MaskStride = mask?.Stride ?? 0,
            };
            op.Run(this, ref sink);
        }
    }

    /// <summary>An axis-aligned device rect, or the frame between two, exact per edge.</summary>
    private readonly struct RectOp(Rect outer, Rect inner = default) : ICoverageOp
    {
        public void Run<TSink>(Canvas c, ref TSink sink) where TSink : struct, ICoverageSink
            => RectRows(ref sink, outer, inner, c._s.Clip, int.MinValue, int.MaxValue);

        public IRect Bounds(Canvas c) => outer.RoundOut();
    }

    /// <summary>
    /// The rasterizer's edges, except rows [MidStart, MidEnd), which are a full-
    /// height span (or the two sides of a ring) — an rrect's straight middle band.
    /// </summary>
    private struct RasterOp : ICoverageOp
    {
        public FillRule Rule;
        public RasterMode Mode;
        public int MidStart, MidEnd;
        public float O0, O1, I0, I1;

        public static RasterOp Plain(FillRule rule, RasterMode mode) => new() { Rule = rule, Mode = mode, MidStart = int.MaxValue, MidEnd = int.MaxValue };

        public void SetSpan(float x0, float x1) { O0 = x0; O1 = x1; I0 = I1 = 0; }

        public void SetRing(float outer0, float outer1, float inner0, float inner1) { O0 = outer0; O1 = outer1; I0 = inner0; I1 = inner1; }

        public readonly void Run<TSink>(Canvas c, ref TSink sink) where TSink : struct, ICoverageSink
        {
            var clip = c._s.Clip;
            int ms = Math.Max(MidStart, clip.Top), me = Math.Min(MidEnd, clip.Bottom);
            if (me <= ms)
            {
                Rows(c, ref sink, int.MinValue, int.MaxValue);
                return;
            }
            Rows(c, ref sink, int.MinValue, ms);
            if (Mode is RasterMode.Supersample or RasterMode.SupersampleSimple) SuperBandRows(ref sink, ms, me, O0, O1, I0, I1, clip);
            else RectRows(ref sink, new Rect(O0, ms, O1, me), I1 > I0 ? new Rect(I0, ms, I1, me) : default, clip, ms, me);
            Rows(c, ref sink, me, int.MaxValue);
        }

        public readonly IRect Bounds(Canvas c) => c._raster.Bounds;

        private readonly void Rows<TSink>(Canvas c, ref TSink sink, int from, int to) where TSink : struct, ICoverageSink
        {
            if (Mode == RasterMode.Supersample) c._raster.FillSupersampled(ref sink, Rule, from, to);
            else if (Mode == RasterMode.SupersampleSimple) c._raster.FillSupersampledSimple(ref sink, leftDir: -1, from, to);
            else c._raster.Fill(ref sink, Rule, Mode == RasterMode.Exact, from, to);
        }
    }

    /// <summary>
    /// The supersampler's coverage for rows whose only edges are vertical — an
    /// rrect's middle band: every sub-scanline sees the same quarter-pixel span
    /// ends, so one row's runs serve them all.
    /// </summary>
    private static void SuperBandRows<TSink>(ref TSink sink, int ms, int me, float o0, float o1, float i0, float i1, IRect clip)
        where TSink : struct, ICoverageSink
    {
        Span<int> px = stackalloc int[24];
        Span<int> dv = stackalloc int[24];
        int n = 0;
        int lo = clip.Left * 4, hi = clip.Right * 4;
        if (i1 > i0)
        {
            Span(px, dv, ref n, Q(o0), Q(i0), lo, hi);
            Span(px, dv, ref n, Q(i1), Q(o1), lo, hi);
        }
        else Span(px, dv, ref n, Q(o0), Q(o1), lo, hi);
        if (n == 0) return;
        // Sort the deltas by pixel and sweep them into runs.
        for (int i = 1; i < n; i++)
        {
            int p = px[i], d = dv[i], j = i - 1;
            while (j >= 0 && px[j] > p) { px[j + 1] = px[j]; dv[j + 1] = dv[j]; j--; }
            px[j + 1] = p; dv[j + 1] = d;
        }
        for (int y = ms; y < me; y++)
        {
            int sum = 0;
            for (int k = 0; k < n;)
            {
                int x = px[k];
                while (k < n && px[k] == x) sum += dv[k++];
                if (k == n) break;
                int a = Math.Min(sum, 255);
                if (a > 0) sink.Blit(y, x, px[k] - x, a);
            }
        }

        static int Q(float x) => (int)MathF.Floor(x * 4f + 0.5f);

        static void Span(Span<int> px, Span<int> dv, ref int n, int start, int stop, int lo, int hi)
        {
            start = Math.Max(start, lo);
            stop = Math.Min(stop, hi);
            if (stop <= start) return;
            // SkSuperBlitter::blitH on all four sub-scanlines at once: a quarter
            // is 16 per sub-scanline (64 here), a full pixel 64+64+64+63.
            int fb = start & 3, fe = stop & 3, p = start >> 2, cnt = (stop >> 2) - p - 1;
            if (cnt < 0) { Add(px, dv, ref n, p, 1, (fe - fb) * 64); return; }
            if (fb == 0) cnt += 1;
            else { Add(px, dv, ref n, p, 1, (4 - fb) * 64); p++; }
            if (cnt > 0) { Add(px, dv, ref n, p, cnt, 255); p += cnt; }
            if (fe != 0) Add(px, dv, ref n, p, 1, fe * 64);
        }

        static void Add(Span<int> px, Span<int> dv, ref int n, int at, int len, int v)
        {
            px[n] = at; dv[n++] = v;
            px[n] = at + len; dv[n++] = -v;
        }
    }

    private readonly struct MaskOp(CoverageMask mask) : ICoverageOp
    {
        public IRect Bounds(Canvas c) => mask.Bounds;

        public void Run<TSink>(Canvas c, ref TSink sink) where TSink : struct, ICoverageSink
        {
            var b = mask.Bounds.Intersect(c._s.Clip);
            if (b.IsEmpty) return;
            byte* origin = mask.Origin;
            for (int y = b.Top; y < b.Bottom; y++)
            {
                byte* row = origin + (long)y * mask.Stride;
                int x = b.Left;
                while (x < b.Right)
                {
                    byte a = row[x];
                    int run = 1;
                    while (x + run < b.Right && row[x + run] == a) run++;
                    if (a != 0) sink.Blit(y, x, run, a);
                    x += run;
                }
            }
        }
    }

    /// <summary>
    /// Exact coverage of an axis-aligned rect, minus an optional inner rect (a
    /// frame), rows limited to [rowStart, rowEnd). Rect coverage is separable —
    /// cx(x)·cy(y) — so each row is a few runs between the pixel boundaries
    /// where either rect's x coverage changes, each run of constant coverage.
    /// </summary>
    private static void RectRows<TSink>(ref TSink sink, Rect o, Rect i, IRect clip, int rowStart, int rowEnd) where TSink : struct, ICoverageSink
    {
        float cy0 = MathF.Max(o.Top, MathF.Max(clip.Top, rowStart)), cy1 = MathF.Min(o.Bottom, MathF.Min(clip.Bottom, rowEnd));
        float cx0 = MathF.Max(o.Left, clip.Left), cx1 = MathF.Min(o.Right, clip.Right);
        if (!(cx1 > cx0 && cy1 > cy0)) return;
        bool hasInner = !i.IsEmpty;
        int iy0 = (int)MathF.Floor(cy0), iy1 = (int)MathF.Ceiling(cy1);
        int xa = (int)MathF.Floor(cx0), xb = (int)MathF.Ceiling(cx1);

        if (!hasInner)
        {
            // A plain rect: left pixel, run, right pixel.
            float left = xa + 1 - cx0, right = cx1 - (xb - 1);
            for (int y = iy0; y < iy1; y++)
            {
                float rc = MathF.Min(y + 1, cy1) - MathF.Max(y, cy0);
                if (xb - xa == 1)
                {
                    Emit(ref sink, y, xa, 1, (cx1 - cx0) * rc);
                    continue;
                }
                int runStart = xa + 1, runEnd = xb - 1;
                if (left >= 1f) runStart = xa; else Emit(ref sink, y, xa, 1, left * rc);
                if (right >= 1f) runEnd = xb; else Emit(ref sink, y, xb - 1, 1, right * rc);
                if (runEnd > runStart) Emit(ref sink, y, runStart, runEnd - runStart, rc);
            }
            return;
        }

        Span<int> cuts = stackalloc int[8];
        int n = 0;
        cuts[n++] = xa + 1;
        cuts[n++] = xb - 1;
        cuts[n++] = xb;
        if (hasInner)
        {
            int ia = (int)MathF.Floor(i.Left), ib = (int)MathF.Ceiling(i.Right);
            cuts[n++] = ia; cuts[n++] = ia + 1; cuts[n++] = ib - 1; cuts[n++] = ib;
        }
        var cs = cuts[..n];
        for (int k = 1; k < n; k++)
        {
            int v = cs[k], j = k - 1;
            while (j >= 0 && cs[j] > v) { cs[j + 1] = cs[j]; j--; }
            cs[j + 1] = v;
        }

        // Rows with the same vertical coverages (all the side rows of a frame)
        // have the same runs: work them out once.
        Span<int> rx = stackalloc int[8], rl = stackalloc int[8], ra = stackalloc int[8];
        int rn = 0;
        float pco = -1, pci = -1;
        for (int y = iy0; y < iy1; y++)
        {
            float co = MathF.Min(y + 1, cy1) - MathF.Max(y, cy0);
            float ci = MathF.Max(0, MathF.Min(y + 1, i.Bottom) - MathF.Max(y, i.Top));
            if (co != pco || ci != pci)
            {
                rn = 0;
                int prev = xa;
                for (int k = 0; k < cs.Length; k++)
                {
                    int q = Math.Min(cs[k], xb);
                    if (q <= prev) continue;
                    float cov = co * Cover(prev, cx0, cx1);
                    if (ci > 0) cov -= ci * Cover(prev, i.Left, i.Right);
                    int a = PixelMath.CoverageToAlpha(cov);
                    if (a != 0) { rx[rn] = prev; rl[rn] = q - prev; ra[rn] = a; rn++; }
                    prev = q;
                }
                pco = co;
                pci = ci;
            }
            for (int k = 0; k < rn; k++) sink.Blit(y, rx[k], rl[k], ra[k]);
        }

        static float Cover(int px, float x0, float x1) => MathF.Max(0, MathF.Min(px + 1, x1) - MathF.Max(px, x0));

        static void Emit(ref TSink sink, int y, int x, int len, float cov)
        {
            int a = PixelMath.CoverageToAlpha(cov);
            if (a != 0) sink.Blit(y, x, len, a);
        }
    }

    // ── Geometry helpers ───────────────────────────────────────────────────

    private void AddRectEdges(Rect r, bool reverse)
    {
        Span<float> p = [r.Left, r.Top, r.Right, r.Top, r.Right, r.Bottom, r.Left, r.Bottom];
        _raster.AddPolygon(p, reverse);
    }

    // Unit quarter-arc tables, one per segment count, built once.
    private static readonly float[]?[] s_arcTables = new float[65][];

    private static float[] ArcTable(int n)
    {
        var t = Volatile.Read(ref s_arcTables[n]);
        if (t is not null) return t;
        t = new float[2 * (n + 1)];
        // Interior vertices sit outside the arc by as much as the chords' middles
        // fall inside it, so the polygon's area error averages out instead of
        // always losing the sagitta. The end points stay on the arc, where it
        // meets the straight sides.
        double balance = 2.0 / (1.0 + Math.Cos(Math.PI / 4 / n));
        for (int i = 0; i <= n; i++)
        {
            double a = Math.PI / 2 * i / n;
            double scale = i == 0 || i == n ? 1.0 : balance;
            t[2 * i] = (float)(Math.Cos(a) * scale);
            t[2 * i + 1] = (float)(Math.Sin(a) * scale);
        }
        Volatile.Write(ref s_arcTables[n], t);
        return t;
    }

    /// <summary>Chords per quarter ellipse for a sagitta under 0.05 px.</summary>
    private static int ArcSegments(float r)
    {
        const float tol = 0.025f;
        if (r <= tol) return 1;
        float theta = 2f * MathF.Acos(1f - tol / r);
        return Math.Clamp((int)MathF.Ceiling(MathF.PI / 2 / theta), 1, 64);
    }

    /// <summary>An rrect's outline in device space, its corners as chords of the exact ellipse.</summary>
    private void AddRRectEdges(Rect r, float rx, float ry, bool reverse)
    {
        if (rx <= 0 || ry <= 0) { AddRectEdges(r, reverse); return; }
        rx = MathF.Min(rx, r.Width * 0.5f);
        ry = MathF.Min(ry, r.Height * 0.5f);
        int n = ArcSegments(MathF.Max(rx, ry));
        var t = ArcTable(n);
        int count = 4 * (n + 1);
        Span<float> p = count * 2 <= 1040 ? stackalloc float[count * 2] : new float[count * 2];
        int k = 0;
        Corner(p, ref k, t, n, r.Right - rx, r.Top + ry, rx, ry, 3);
        Corner(p, ref k, t, n, r.Right - rx, r.Bottom - ry, rx, ry, 0);
        Corner(p, ref k, t, n, r.Left + rx, r.Bottom - ry, rx, ry, 1);
        Corner(p, ref k, t, n, r.Left + rx, r.Top + ry, rx, ry, 2);
        _raster.AddPolygon(p[..(k * 2)], reverse);

        static void Corner(Span<float> p, ref int k, float[] t, int n, float cx, float cy, float rx, float ry, int quadrant)
        {
            for (int i = 0; i <= n; i++)
            {
                float c = t[2 * i], s = t[2 * i + 1];
                float dx, dy;
                switch (quadrant)
                {
                    case 0: dx = c; dy = s; break;
                    case 1: dx = -s; dy = c; break;
                    case 2: dx = -c; dy = -s; break;
                    default: dx = s; dy = -c; break;
                }
                p[2 * k] = cx + dx * rx;
                p[2 * k + 1] = cy + dy * ry;
                k++;
            }
        }
    }

    private static bool IsAxisAligned(in Matrix3x2 m) => m.M12 == 0 && m.M21 == 0;

    private static bool IsIntegral(Rect r)
        => r.Left == MathF.Floor(r.Left) && r.Top == MathF.Floor(r.Top) && r.Right == MathF.Floor(r.Right) && r.Bottom == MathF.Floor(r.Bottom);

    /// <summary>A local rect's device bounds (exact for an axis-aligned transform).</summary>
    private Rect MapRect(Rect r)
    {
        var m = _s.Matrix;
        var a = Vector2.Transform(new Vector2(r.Left, r.Top), m);
        var b = Vector2.Transform(new Vector2(r.Right, r.Bottom), m);
        if (IsAxisAligned(m)) return new Rect(a.X, a.Y, b.X, b.Y).Sorted();
        var c = Vector2.Transform(new Vector2(r.Right, r.Top), m);
        var d = Vector2.Transform(new Vector2(r.Left, r.Bottom), m);
        return new Rect(
            MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)),
            MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)), MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y)));
    }

    private Rect DeviceBounds(Path path) => path.IsEmpty ? default : MapRect(path.Bounds);

    private uint PmFor(uint argb)
    {
        uint pm = PixelMath.Premul(argb);
        return _surface.Order == PixelOrder.Rgba8888 ? PixelMath.SwapRB(pm) : pm;
    }

    private readonly struct DrawTarget(uint* origin, int stride)
    {
        public readonly uint* Origin = origin;
        public readonly int Stride = stride;
    }

    private DrawTarget Target()
        => _s.Layer is { } l ? new DrawTarget(l.Origin, l.Bounds.Width) : new DrawTarget(_surface.Base, _surface.RowPixels);

    private CoverageMask RentMask(IRect bounds)
    {
        CoverageMask? m = null;
        int need = bounds.Width * bounds.Height;
        for (int i = _maskPool.Count - 1; i >= 0; i--)
        {
            if (_maskPool[i].Capacity >= need) { m = _maskPool[i]; _maskPool.RemoveAt(i); break; }
        }
        if (m is null && _maskPool.Count > 0) { m = _maskPool[^1]; _maskPool.RemoveAt(_maskPool.Count - 1); }
        m ??= new CoverageMask(need);
        m.Reset(bounds);
        return m;
    }

    private Layer RentLayer()
    {
        if (_layerPool.Count == 0) return new Layer();
        var l = _layerPool[^1];
        _layerPool.RemoveAt(_layerPool.Count - 1);
        return l;
    }

    /// <summary>
    /// A finished layer back onto what is below it, at its alpha, matrix and
    /// mode, through the clip in force after the restore, so an antialiased
    /// clip edge is applied again.
    /// </summary>
    private void Composite(Layer layer)
    {
        // Only what was drawn into the layer, unless a transparent pixel
        // composites to something: Src clears, and a matrix may add alpha.
        bool clearCounts = layer.Mode == LayerMode.Src || (layer.HasMatrix && layer.Matrix[19] > 0f);
        var b = (clearCounts ? layer.Bounds : layer.Dirty).Intersect(_s.Clip);
        if (b.IsEmpty || layer.Alpha == 0) return;
        _s.Layer?.Touch(b);
        var t = Target();
        var mask = _s.Mask;
        if (layer.Mode != LayerMode.SrcOver || layer.HasMatrix)
        {
            CompositeGeneral(layer, b, t, mask);
            return;
        }
        uint alpha = layer.Alpha;
        uint* src = layer.Origin;
        int ss = layer.Bounds.Width;
        for (int y = b.Top; y < b.Bottom; y++)
        {
            uint* s = src + (long)y * ss;
            uint* d = t.Origin + (long)y * t.Stride;
            byte* m = mask is null ? null : mask.Origin + (long)y * mask.Stride;
            for (int x = b.Left; x < b.Right; x++)
            {
                uint p = s[x];
                if (p == 0) continue;
                uint a = alpha;
                if (m != null)
                {
                    uint mv = m[x];
                    if (mv == 0) continue;
                    if (mv != 255) a = PixelMath.MulDiv255Round(a, mv);
                }
                if (a == 255) d[x] = (p >> 24) == 255 ? p : PixelMath.SrcOver(p, d[x]);
                else d[x] = PixelMath.BlendARGB32(p, d[x], a);
            }
        }
    }

    // A layer with a mode or a matrix: per pixel, the matrix, then the mode's
    // formula on premultiplied values, then the clip's coverage as a lerp
    // from what was there.
    private void CompositeGeneral(Layer layer, IRect b, DrawTarget t, CoverageMask? mask)
    {
        var dirty = layer.Dirty;
        var mode = layer.Mode;
        uint* src = layer.Origin;
        int ss = layer.Bounds.Width;
        for (int y = b.Top; y < b.Bottom; y++)
        {
            uint* s = src + (long)y * ss;
            uint* d = t.Origin + (long)y * t.Stride;
            byte* m = mask is null ? null : mask.Origin + (long)y * mask.Stride;
            bool rowDirty = y >= dirty.Top && y < dirty.Bottom;
            for (int x = b.Left; x < b.Right; x++)
            {
                uint cov = m == null ? 255u : m[x];
                if (cov == 0) continue;
                // Outside what was drawn the layer is transparent, uncleared.
                uint p = rowDirty && x >= dirty.Left && x < dirty.Right ? s[x] : 0;
                if (layer.HasMatrix) p = ApplyColorMatrix(layer.Matrix, p);
                uint r = BlendPixel(mode, p, d[x]);
                d[x] = cov == 255 ? r : Lerp(d[x], r, cov);
            }
        }
    }

    /// <summary>A premultiplied pixel through a 4×5 matrix on its straight values.</summary>
    private static uint ApplyColorMatrix(float[] mx, uint p)
    {
        float a = (p >> 24) / 255f;
        float r = 0, g = 0, bl = 0;
        if (a > 0)
        {
            float inv = 1f / (a * 255f);
            r = ((p >> 16) & 0xFF) * inv;
            g = ((p >> 8) & 0xFF) * inv;
            bl = (p & 0xFF) * inv;
        }
        float or = Math.Clamp(mx[0] * r + mx[1] * g + mx[2] * bl + mx[3] * a + mx[4], 0f, 1f);
        float og = Math.Clamp(mx[5] * r + mx[6] * g + mx[7] * bl + mx[8] * a + mx[9], 0f, 1f);
        float ob = Math.Clamp(mx[10] * r + mx[11] * g + mx[12] * bl + mx[13] * a + mx[14], 0f, 1f);
        float oa = Math.Clamp(mx[15] * r + mx[16] * g + mx[17] * bl + mx[18] * a + mx[19], 0f, 1f);
        float k = oa * 255f;
        return ((uint)(k + 0.5f) << 24) | ((uint)(or * k + 0.5f) << 16) | ((uint)(og * k + 0.5f) << 8) | (uint)(ob * k + 0.5f);
    }

    /// <summary>Premultiplied <paramref name="s"/> onto <paramref name="d"/> by the mode's formula, as Skia's.</summary>
    private static uint BlendPixel(LayerMode mode, uint s, uint d)
    {
        switch (mode)
        {
            case LayerMode.Src: return s;
            case LayerMode.SrcOver: return PixelMath.SrcOverColor(s, d);
        }
        uint sa = s >> 24, da = d >> 24;
        uint outA = mode == LayerMode.Plus ? Math.Min(255, sa + da) : sa + da - PixelMath.MulDiv255Round(sa, da);
        uint o = outA << 24;
        for (int sh = 0; sh <= 16; sh += 8)
        {
            uint sc = (s >> sh) & 0xFF, dc = (d >> sh) & 0xFF;
            uint c;
            switch (mode)
            {
                case LayerMode.Plus:
                    c = Math.Min(255, sc + dc);
                    break;
                case LayerMode.Screen:
                    c = sc + dc - PixelMath.MulDiv255Round(sc, dc);
                    break;
                case LayerMode.Multiply:
                    // s·d + s·(1−da) + d·(1−sa), in 255ths.
                    c = Math.Min(255, (sc * dc + sc * (255 - da) + dc * (255 - sa) + 127) / 255);
                    break;
                default: // Overlay
                {
                    int isc = (int)sc, idc = (int)dc, isa = (int)sa, ida = (int)da;
                    int core = 2 * idc <= ida
                        ? 2 * isc * idc
                        : isa * ida - 2 * (ida - idc) * (isa - isc);
                    int v = (core + isc * (255 - ida) + idc * (255 - isa) + 127) / 255;
                    c = (uint)Math.Clamp(v, 0, 255);
                    break;
                }
            }
            o |= Math.Min(c, outA) << sh;
        }
        return o;
    }

    /// <summary>Each channel from <paramref name="a"/> towards <paramref name="b"/> by t/255.</summary>
    private static uint Lerp(uint a, uint b, uint t)
    {
        uint o = 0;
        for (int sh = 0; sh <= 24; sh += 8)
        {
            int ac = (int)((a >> sh) & 0xFF), bc = (int)((b >> sh) & 0xFF);
            int v = ac + ((bc - ac) * (int)t + (bc >= ac ? 127 : -127)) / 255;
            o |= (uint)v << sh;
        }
        return o;
    }
}

/// <summary>How a layer composites back: Skia's modes of the same names, premultiplied.</summary>
public enum LayerMode : byte { SrcOver, Multiply, Plus, Screen, Overlay, Src }
