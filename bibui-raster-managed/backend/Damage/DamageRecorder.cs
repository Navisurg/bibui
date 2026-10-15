using System.Numerics;
using System.Runtime.InteropServices;

namespace Bibui.Raster.Managed.Backend.Damage;

/// <summary>
/// Records a damage-tracked frame: every canvas call is appended to a
/// <see cref="DamageFrame"/>, each draw signed by a hash of everything that
/// decides its pixels and given the device bounds it can touch.
///
/// <para>It keeps the canvas state a real one would — transform, a conservative
/// device clip, the opacity layers in force — because a draw's pixels depend on
/// it, so each draw's hash includes it, and because the painter reads the
/// transform back (<see cref="TotalMatrix"/>).</para>
///
/// <para>A frame already known to be painted whole is <see cref="Tee"/>d: the
/// canvas draws every call as well as recording it, and the record keeps only
/// what the next frame is compared on, not what a replay would need.</para>
/// </summary>
internal sealed class DamageRecorder(ManagedRasterBackend backend)
{
    private DamageFrame _f = null!;

    /// <summary>The canvas draws each call as it is recorded; nothing is replayed.</summary>
    internal bool Tee { get; private set; }

    private struct State
    {
        public Matrix3x2 M;
        public float CL, CT, CR, CB;   // device clip, conservative (rounded out)
        public ulong Shape;            // the rounded clips in force, signed; 0 for none
        public ulong Layers;
        public int LayerOp;            // the SaveLayer op this level opened, or -1
    }

    private State _s;
    private State[] _stack = new State[32];
    private int _depth;
    private ulong _stateHash;
    private bool _stateDirty = true;

    // Draw bounds still open per layer level, to give a SaveLayer the bounds of
    // what it holds when it is restored.
    private PixelRect[] _layerBounds = new PixelRect[32];

    // Paths recorded this frame whose content a replay still needs, each with
    // the first op that holds it. A path that changes before the replay is
    // copied first (Snapshot), and the ops that held it take the copy.
    private readonly Dictionary<ManagedPath, int> _held = new(ReferenceEqualityComparer.Instance);

    /// <summary>Starts recording a frame of <paramref name="width"/> × <paramref name="height"/> physical px into <paramref name="frame"/>.</summary>
    internal void Begin(DamageFrame frame, int width, int height, bool tee)
    {
        _f = frame;
        _f.Reset(width, height, tee);
        Tee = tee;
        _s = new State { M = Matrix3x2.Identity, CL = 0, CT = 0, CR = width, CB = height, Layers = 0, LayerOp = -1 };
        _depth = 0;
        _layerBounds[0] = default;
        _stateDirty = true;
    }

    /// <summary>Ends the frame: balances any Save left open and lists its draws.</summary>
    internal void End()
    {
        while (_depth > 0) Restore();
        _f.Finish();
    }

    /// <summary>Lets go of the paths the frame held, once it has been replayed.</summary>
    internal void Release()
    {
        foreach (var p in _held.Keys) p.Holder = null;
        _held.Clear();
    }

    /// <summary><paramref name="path"/> is about to change: the ops recorded with it take a copy of what it holds now.</summary>
    internal void Snapshot(ManagedPath path)
    {
        if (!_held.Remove(path, out int from)) return;
        var copy = _f.CopyPath(path);
        for (int k = from; k < _f.OpCount; k++)
            if (ReferenceEquals(_f.Ops[k].Ref, path)) _f.Ops[k].Ref = copy;
    }

    // ── State ────────────────────────────────────────────────────────────────

    internal Matrix3x2 TotalMatrix => _s.M;

    internal void Clear(Rgba color)
    {
        _f.Background = color;
        ref var op = ref _f.NewOp(OpKind.Clear);
        op.Color = color;
        var all = new PixelRect(0, 0, _f.Width, _f.Height);
        Draw(ref op, Hash64.Mix(0xC1EAUL, (ulong)color.Argb), all, ignoreClip: true);
        op.Covered = all;   // a clear writes every pixel, whatever its alpha
    }

    internal void Save()
    {
        _f.NewOp(OpKind.Save);
        Push(-1);
    }

    internal void Restore()
    {
        // An extra restore is ignored, as on a real canvas, and not recorded,
        // so a replay cannot pop the clip it repaints through.
        if (_depth == 0) return;
        _f.NewOp(OpKind.Restore);
        int layerOp = _s.LayerOp;
        var inner = _layerBounds[_depth];
        _s = _stack[--_depth];
        _stateDirty = true;
        if (layerOp >= 0)
        {
            ref var lop = ref _f.Ops[layerOp];
            lop.Match = _f.OpCount - 1;
            lop.Bounds = inner;
        }
        // What was drawn inside counts toward the level that holds it.
        _layerBounds[_depth] = DamageFrame.Union(_layerBounds[_depth], inner);
    }

    private void Push(int layerOp)
    {
        if (_depth + 1 >= _stack.Length)
        {
            Array.Resize(ref _stack, _stack.Length * 2);
            Array.Resize(ref _layerBounds, _layerBounds.Length * 2);
        }
        _stack[_depth++] = _s;
        _s.LayerOp = layerOp;
        _layerBounds[_depth] = default;
    }

    internal void Translate(float dx, float dy)
    {
        ref var op = ref _f.NewOp(OpKind.Translate);
        op.A = dx; op.B = dy;
        _s.M = Matrix3x2.CreateTranslation(dx, dy) * _s.M;
        _stateDirty = true;
    }

    internal void Scale(float sx, float sy)
    {
        ref var op = ref _f.NewOp(OpKind.Scale);
        op.A = sx; op.B = sy;
        _s.M = Matrix3x2.CreateScale(sx, sy) * _s.M;
        _stateDirty = true;
    }

    internal void SetMatrix(in Matrix3x2 matrix)
    {
        ref var op = ref _f.NewOp(OpKind.SetMatrix);
        op.Matrix = matrix;
        _s.M = matrix;
        _stateDirty = true;
    }

    internal void ClipRect(RectF rect)
    {
        ref var op = ref _f.NewOp(OpKind.ClipRect);
        op.Rect = rect;
        var d = DeviceBounds(rect);
        // An aliased clip lands on whole pixels: rounding out keeps it conservative.
        _s.CL = MathF.Max(_s.CL, MathF.Floor(d.Left));
        _s.CT = MathF.Max(_s.CT, MathF.Floor(d.Top));
        _s.CR = MathF.Min(_s.CR, MathF.Ceiling(d.Right));
        _s.CB = MathF.Min(_s.CB, MathF.Ceiling(d.Bottom));
        _stateDirty = true;
    }

    /// <summary>
    /// A rounded clip: its bounds narrow the clip as a rect's would, and its
    /// shape is signed into the state, so a draw under a different shape is a
    /// different draw.
    /// </summary>
    internal void ClipRoundRect(RectF rect, CornerRadii radii)
    {
        ref var op = ref _f.NewOp(OpKind.ClipRoundRect);
        op.Rect = rect;
        op.A = radii.TopLeft; op.B = radii.TopRight; op.C = radii.BottomRight; op.D = radii.BottomLeft;
        var d = DeviceBounds(rect);
        _s.CL = MathF.Max(_s.CL, MathF.Floor(d.Left));
        _s.CT = MathF.Max(_s.CT, MathF.Floor(d.Top));
        _s.CR = MathF.Min(_s.CR, MathF.Ceiling(d.Right));
        _s.CB = MathF.Min(_s.CB, MathF.Ceiling(d.Bottom));
        ulong shape = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Mix(0x2CUL, _s.M), rect), op.A, op.B), op.C, op.D);
        _s.Shape = Hash64.Mix(_s.Shape == 0 ? Hash64.Seed : _s.Shape, shape) | 1UL;
        _stateDirty = true;
    }

    internal void SaveLayerAlpha(byte alpha)
    {
        ref var op = ref _f.NewOp(OpKind.SaveLayer);
        op.Alpha = alpha;
        op.Match = -1;
        Push(_f.OpCount - 1);
        _s.Layers = Hash64.Mix(Hash64.Mix(_s.Layers, (ulong)alpha), (ulong)_depth);
        _stateDirty = true;
    }

    // ── Shapes ───────────────────────────────────────────────────────────────

    internal void DrawRoundRect(RectF rect, float rx, float ry, in RasterPaint paint)
    {
        ref var op = ref _f.NewOp(OpKind.RoundRect);
        op.Rect = rect; op.A = rx; op.B = ry; op.Paint = paint;
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x22UL, rect), rx, ry), paint);
        Draw(ref op, h, Bounds(Inflate(rect, StrokeHalf(paint)), paint));
        op.Covered = Covered(rect, rx, ry, paint);
    }

    /// <summary>
    /// The device pixels an opaque fill writes whatever lay under them: the
    /// rect pulled in a pixel past its anti-aliased edge and past its corners,
    /// inside the clip it ran under. Empty for anything that blends: a stroke,
    /// a blur, a translucent colour or gradient, or a fill inside an opacity layer.
    /// </summary>
    private PixelRect Covered(RectF rect, float rx, float ry, in RasterPaint paint)
    {
        // Under a rounded clip the corners it cuts are not written: nothing is surely covered.
        if (paint.Spec.Style != Bibui.PaintStyle.Fill || paint.MaskBlurSigma > 0f || paint.ImageBlurSigma > 0f
            || _s.Layers != 0 || _s.Shape != 0 || paint.Spec.Color.A != 255 || !AxisAligned) return default;
        if (paint.Shader is { } sh)
            foreach (var c in sh.Colors) if (c.A != 255) return default;
        var d = DeviceBounds(rect);
        float inset = 1f + MathF.Max(rx * MathF.Abs(_s.M.M11), ry * MathF.Abs(_s.M.M22));
        // The clip is tracked rounded out, so a pixel in from it is surely inside it.
        var r = new PixelRect(
            (int)MathF.Ceiling(MathF.Max(d.Left + inset, _s.CL + 1f)), (int)MathF.Ceiling(MathF.Max(d.Top + inset, _s.CT + 1f)),
            (int)MathF.Floor(MathF.Min(d.Right - inset, _s.CR - 1f)), (int)MathF.Floor(MathF.Min(d.Bottom - inset, _s.CB - 1f)));
        return r.Width > 0 && r.Height > 0 ? r : default;
    }

    internal void DrawLine(float x0, float y0, float x1, float y1, in RasterPaint paint)
    {
        ref var op = ref _f.NewOp(OpKind.Line);
        op.A = x0; op.B = y0; op.C = x1; op.D = y1; op.Paint = paint;
        var r = new RectF(MathF.Min(x0, x1), MathF.Min(y0, y1), MathF.Max(x0, x1), MathF.Max(y0, y1));
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x23UL, x0, y0), x1, y1), paint);
        Draw(ref op, h, Bounds(Inflate(r, StrokeHalf(paint) * 1.5f), paint));
    }

    internal void DrawPath(ManagedPath path, in RasterPaint paint)
    {
        int index = _f.OpCount;
        ref var op = ref _f.NewOp(OpKind.Path);
        op.Paint = paint;
        op.Ref = path;
        if (!Tee && _held.TryAdd(path, index)) path.Holder = this;
        var b = path.Native.Bounds;
        // A miter join reaches up to the miter limit (4) half-widths out; caps and other joins sqrt(2).
        float reach = StrokeHalf(paint) * (paint.Spec.StrokeJoin == StrokeJoin.Miter ? 4f : 1.5f);
        ulong h = Hash64.Mix(Hash64.Mix(0x24UL, path.ContentHash()), paint);
        Draw(ref op, h, path.Native.IsEmpty ? default : Bounds(Inflate(new RectF(b.Left, b.Top, b.Right, b.Bottom), reach), paint));
    }

    internal void DrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, in RasterPaint paint)
    {
        ref var op = ref _f.NewOp(OpKind.Points);
        op.Mode = mode; op.Paint = paint;
        op.Start = Tee ? 0 : _f.Store(points);
        op.Count = points.Length;
        float l = float.PositiveInfinity, t = float.PositiveInfinity, r = float.NegativeInfinity, b = float.NegativeInfinity;
        foreach (var p in points)
        {
            l = MathF.Min(l, p.X); t = MathF.Min(t, p.Y);
            r = MathF.Max(r, p.X); b = MathF.Max(b, p.Y);
        }
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x26UL, (ulong)mode), points), paint);
        var bounds = points.IsEmpty ? default : Bounds(Inflate(new RectF(l, t, r, b), StrokeHalf(paint) * 1.5f), paint);
        Draw(ref op, h, bounds);
    }

    // ── Images ───────────────────────────────────────────────────────────────

    internal void DrawImage(RasterImage image, RectF dest, in RasterPaint paint)
    {
        ref var op = ref _f.NewOp(OpKind.Image);
        op.Ref = image; op.Rect = dest; op.Paint = paint;
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x27UL, ImageIds.Of(image)), dest), paint);
        Draw(ref op, h, Bounds(Inflate(dest, 1f), paint));
    }

    internal void DrawPixels(ReadOnlySpan<byte> rgba, int width, int height, RectF dest, in RasterPaint paint)
    {
        ref var op = ref _f.NewOp(OpKind.Pixels);
        op.Start = Tee ? 0 : _f.Store(rgba);   // kept only to be replayed
        op.Count = rgba.Length;
        op.W = width; op.H = height; op.Rect = dest; op.Paint = paint;
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Mix(0x28UL, Hash64.Bytes(rgba)), (ulong)width << 32 | (uint)height), dest), paint);
        Draw(ref op, h, Bounds(Inflate(dest, 1f), paint));
    }

    internal void DrawBackdropBlur(PixelRect region, RectF clip, float sigma)
    {
        ref var op = ref _f.NewOp(OpKind.BackdropBlur);
        op.Region = region; op.Rect = clip; op.A = sigma;
        // It writes inside clip and reads all of region: its bounds are both,
        // and DamageFrame.Settle makes damage that touches them take them in.
        var write = Clipped(DeviceBounds(Inflate(clip, 1f)));
        var bounds = DamageFrame.Union(write, region);
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x29UL, clip), sigma), ((ulong)(uint)region.Left << 32) | (uint)region.Top);
        h = Hash64.Mix(h, ((ulong)(uint)region.Right << 32) | (uint)region.Bottom);
        Draw(ref op, h, bounds, ignoreClip: true);
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    /// <summary>Records a run; <paramref name="drawn"/> is its advance when the canvas drew it (a teed frame), else it is measured.</summary>
    internal float DrawText(ReadOnlySpan<char> text, float x, float baselineY, in RasterFont font, Rgba color, float? drawn)
    {
        ref var op = ref _f.NewOp(OpKind.Text);
        // The chars are the caller's and may be reused once this returns: kept to be replayed.
        op.Start = Tee ? 0 : _f.Store(text);
        op.Count = text.Length;
        op.A = x; op.B = baselineY; op.Font = font; op.Color = color;
        ulong content = Hash64.Bytes(MemoryMarshal.AsBytes(text));
        float advance = drawn ?? Advance(font, content, text);
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Mix(0x2DUL, (ulong)text.Length), content), x, baselineY),
            (ulong)(uint)font.Typeface.Id << 32 | color.Argb), font.Size);
        Draw(ref op, h, Bounds(TextBox(x, x + advance, baselineY, font), default));
        return advance;
    }

    internal void DrawGlyphRun(ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions,
        float x, float baselineY, in RasterFont font, Rgba color)
    {
        ref var op = ref _f.NewOp(OpKind.GlyphRun);
        op.Start = Tee ? 0 : _f.Store(glyphs);
        op.Start2 = Tee ? 0 : _f.Store(positions);
        op.Count = glyphs.Length;
        op.A = x; op.B = baselineY; op.Font = font; op.Color = color;
        float l = 0f, r = 0f, t = 0f, b = 0f;
        foreach (var p in positions)
        {
            l = MathF.Min(l, p.X); r = MathF.Max(r, p.X);
            t = MathF.Min(t, p.Y); b = MathF.Max(b, p.Y);
        }
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x2BUL, glyphs), positions), x, baselineY);
        h = Hash64.Mix(Hash64.Mix(h, (ulong)(uint)font.Typeface.Id << 32 | color.Argb), font.Size);
        var box = TextBox(x + l, x + r + font.Size, baselineY, font);
        box = new RectF(box.Left, box.Top + t, box.Right, box.Bottom + b);
        Draw(ref op, h, Bounds(box, default));
    }

    // A run's advance is what DrawText would have returned, which a recorded
    // frame must hand back without drawing. Measuring shapes the run, and the
    // replay shapes it again to draw it, so the same runs frame after frame are
    // answered from here: two frames' worth, the older half dropped as it fills.
    // Runs are keyed by a 64-bit hash of their chars: a collision would hand
    // back another run's advance, at odds of 2^-64 per pair of runs.
    private Dictionary<(int, float, ulong, int), float> _advances = [], _olderAdvances = [];

    private float Advance(in RasterFont font, ulong content, ReadOnlySpan<char> text)
    {
        var key = (font.Typeface.Id, font.Size, content, text.Length);
        if (_advances.TryGetValue(key, out float a)) return a;
        if (!_olderAdvances.TryGetValue(key, out a)) a = backend.MeasureText(font, text);
        if (_advances.Count >= 8192)
        {
            (_olderAdvances, _advances) = (_advances, _olderAdvances);
            _advances.Clear();
        }
        _advances[key] = a;
        return a;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>Signs a draw under the current state and gives it its bounds.</summary>
    private void Draw(ref DamageOp op, ulong contentHash, PixelRect device, bool ignoreClip = false)
    {
        op.Draws = true;
        op.Bounds = ignoreClip ? device : Clipped(device);

        if (_stateDirty)
        {
            _stateHash = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Seed, _s.M), _s.CL, _s.CT), _s.CR, _s.CB);
            _stateHash = Hash64.Mix(Hash64.Mix(_stateHash, _s.Layers), _s.Shape);
            _stateDirty = false;
        }
        op.Hash = Hash64.Mix(_stateHash, contentHash);

        var ob = op.Bounds;
        if (ob.Width > 0 && ob.Height > 0) _layerBounds[_depth] = DamageFrame.Union(_layerBounds[_depth], ob);
    }

    private bool AxisAligned => _s.M.M12 == 0f && _s.M.M21 == 0f;

    private float MatrixScale => MathF.Max(
        MathF.Sqrt(_s.M.M11 * _s.M.M11 + _s.M.M12 * _s.M.M12),
        MathF.Sqrt(_s.M.M21 * _s.M.M21 + _s.M.M22 * _s.M.M22));

    /// <summary>Half a stroke's width, local units — how far it reaches past its geometry — a hairline's half pixel at least; 0 for a fill.</summary>
    private static float StrokeHalf(in RasterPaint paint) =>
        paint.Spec.Style == Bibui.PaintStyle.Fill ? 0f : MathF.Max(1f, paint.Spec.StrokeWidth) / 2f;

    private static RectF Inflate(RectF r, float d) => new(
        MathF.Min(r.Left, r.Right) - d, MathF.Min(r.Top, r.Bottom) - d,
        MathF.Max(r.Left, r.Right) + d, MathF.Max(r.Top, r.Bottom) + d);

    // A run of text's ink: the face's ascent and descent, and room past both
    // ends and above and below for what overhangs them (italics, a wide 'f',
    // stacked accents). Metrics are cached per face and size for the recorder's life.
    private readonly Dictionary<(int, float), TextMetrics> _metrics = [];

    private RectF TextBox(float left, float right, float baseline, in RasterFont font)
    {
        var key = (font.Typeface.Id, font.Size);
        if (!_metrics.TryGetValue(key, out var m)) _metrics[key] = m = backend.GetMetrics(font);
        float pad = font.Size * 0.25f + 1f;
        return new(left - pad, baseline + MathF.Min(m.Ascent, -font.Size) - pad, right + pad, baseline + MathF.Max(m.Descent, font.Size * 0.3f) + pad);
    }

    private RectF DeviceBounds(RectF local)
    {
        var m = _s.M;
        if (AxisAligned)
        {
            float x0 = local.Left * m.M11 + m.M31, x1 = local.Right * m.M11 + m.M31;
            float y0 = local.Top * m.M22 + m.M32, y1 = local.Bottom * m.M22 + m.M32;
            return new RectF(MathF.Min(x0, x1), MathF.Min(y0, y1), MathF.Max(x0, x1), MathF.Max(y0, y1));
        }
        var a = Vector2.Transform(new Vector2(local.Left, local.Top), m);
        var b = Vector2.Transform(new Vector2(local.Right, local.Top), m);
        var c = Vector2.Transform(new Vector2(local.Left, local.Bottom), m);
        var d = Vector2.Transform(new Vector2(local.Right, local.Bottom), m);
        return new RectF(
            MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)),
            MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)), MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y)));
    }

    /// <summary>
    /// Device pixels a draw of <paramref name="local"/> may touch: mapped
    /// through the transform, widened for a blur in the paint and for the
    /// anti-aliased fringe, rounded out.
    /// </summary>
    private PixelRect Bounds(RectF local, in RasterPaint paint)
    {
        var d = DeviceBounds(local);
        float blur = MathF.Max(paint.MaskBlurSigma, paint.ImageBlurSigma);
        // Half a pixel of anti-aliased fringe: ink lands only in pixels its
        // geometry touches, so half a pixel and rounding out is conservative.
        // A blur spreads 3 sigma.
        float grow = 0.5f + (blur > 0f ? (3f * blur + 1f) * MathF.Max(1f, MatrixScale) : 0f);
        if (float.IsNaN(d.Left) || float.IsInfinity(d.Left) || float.IsNaN(d.Right) || float.IsInfinity(d.Right))
            return ClipBounds();
        return new PixelRect(
            (int)MathF.Floor(MathF.Max(-1e6f, d.Left - grow)), (int)MathF.Floor(MathF.Max(-1e6f, d.Top - grow)),
            (int)MathF.Ceiling(MathF.Min(1e6f, d.Right + grow)), (int)MathF.Ceiling(MathF.Min(1e6f, d.Bottom + grow)));
    }

    private PixelRect ClipBounds() => new((int)_s.CL, (int)_s.CT, (int)_s.CR, (int)_s.CB);

    private PixelRect Clipped(PixelRect r) => Clipped(new RectF(r.Left, r.Top, r.Right, r.Bottom));

    private PixelRect Clipped(RectF d)
    {
        var r = new PixelRect(
            (int)MathF.Floor(MathF.Max(d.Left, _s.CL)), (int)MathF.Floor(MathF.Max(d.Top, _s.CT)),
            (int)MathF.Ceiling(MathF.Min(d.Right, _s.CR)), (int)MathF.Ceiling(MathF.Min(d.Bottom, _s.CB)));
        return r.Width > 0 && r.Height > 0 ? r : default;
    }
}

/// <summary>
/// An identity per image for the life of the process. An image is immutable, so
/// the same instance is the same pixels; a new one is a new number even where
/// the runtime hands out an address that a dead one had.
/// </summary>
internal static class ImageIds
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RasterImage, object> s_ids = new();
    private static long s_next;

    internal static ulong Of(RasterImage image) =>
        (ulong)(long)s_ids.GetValue(image, static _ => Interlocked.Increment(ref s_next));
}
