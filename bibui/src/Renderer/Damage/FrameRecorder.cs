using System.Numerics;
using Bibui.Raster;

namespace Bibui.Renderer.Damage;

/// <summary>
/// The canvas a damage-tracked frame is painted onto. Nothing reaches
/// pixels here: every call is appended to a <see cref="FrameRecord"/>, hashed
/// into the signature of the box being painted, and given the device bounds it
/// can touch. <see cref="FrameRecord.Replay"/> issues the same calls, in the same
/// order, to a real canvas afterwards.
///
/// <para>It keeps the canvas state a real one would — transform, a conservative
/// device clip, the opacity layers in force — because a draw's pixels depend on
/// it, so each draw's hash includes it, and because the renderer reads it back
/// (<see cref="TotalMatrix"/>, for overlays).</para>
///
/// <para>The paint walk marks boxes with <see cref="BeginBox"/> and
/// <see cref="EndBox"/> — the only thing the renderer does differently for a
/// recorded frame.</para>
/// </summary>
internal sealed class FrameRecorder(IRasterBackend backend) : IRecordingCanvas
{
    private FrameRecord _r = null!;

    // A frame known to be painted whole is painted as it is recorded: every
    // call goes on to this canvas too, and nothing is replayed afterwards.
    private IRasterCanvas? _tee;

    private struct State
    {
        public Matrix3x2 M;
        public float CL, CT, CR, CB;   // device clip, conservative (rounded out)
        public ulong Layers;
        public int LayerOp;            // the SaveLayer op this level opened, or -1
    }

    private State _s;
    private State[] _stack = new State[32];
    private int _depth;
    private ulong _stateHash;
    private bool _stateDirty = true;

    private int[] _boxStack = new int[64];
    private int _boxDepth;

    // Draw bounds still open per layer level, to give a SaveLayer the bounds of
    // what it holds when it is restored.
    private PixelRect[] _layerBounds = new PixelRect[32];

    /// <summary>Starts recording a frame of <paramref name="width"/> × <paramref name="height"/> physical px into <paramref name="record"/>.</summary>
    internal void Begin(FrameRecord record, int width, int height, IRasterCanvas? tee)
    {
        _r = record;
        _r.Reset(width, height, backend);
        _r.Teed = tee is not null;
        _tee = tee;
        _s = new State { M = Matrix3x2.Identity, CL = 0, CT = 0, CR = width, CB = height, Layers = 0, LayerOp = -1 };
        _depth = 0;
        _layerBounds[0] = default;
        _stateDirty = true;
        _boxDepth = 0;

        // The frame itself is a box: the root of the tree, overlays, and anything
        // drawn outside every box (the clear) hang off it.
        int root = _r.NewBox();
        ref var b = ref _r.Boxes[root];
        b.Key = Hash64.Seed;
        InitBox(ref b, parent: -1);
        _r.BoxIndex[b.Key] = root;
        _boxStack[_boxDepth++] = root;
    }

    /// <summary>Ends the frame: balances any Save left open and closes every box.</summary>
    internal void End()
    {
        while (_depth > 0) Restore();
        while (_boxDepth > 0) EndBox();
    }

    private static void InitBox(ref BoxRec b, int parent)
    {
        b.Parent = parent;
        b.FirstChild = b.LastChild = b.NextSibling = -1;
        b.Pre = b.Tail = b.Inter = b.Children = Hash64.Seed;
        b.Seg = Hash64.Seed;
        b.L = b.T = float.PositiveInfinity;
        b.R = b.B = float.NegativeInfinity;
    }

    // ── Boxes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A node starts painting. Its key is its id, or else its place among its
    /// parent's children and its type, either way under its parent's key — so
    /// the same key in two frames is the same box in the same place in the tree.
    /// </summary>
    internal void BeginBox(string? id, Type type)
    {
        int parent = _boxStack[_boxDepth - 1];
        ref var p = ref _r.Boxes[parent];
        ulong local = id is null
            ? Hash64.Mix(Hash64.Mix(0xA11UL, (ulong)p.ChildCounter), (ulong)type.GetHashCode())
            : Hash64.Mix(0x1DUL, id);
        p.ChildCounter++;
        BeginKeyed(Hash64.Mix(p.Key, local), parent);
    }

    /// <summary>A box for something that is not a node — an overlay flushed at the end of the frame.</summary>
    internal void BeginPseudoBox(ulong local)
    {
        int parent = _boxStack[_boxDepth - 1];
        _r.Boxes[parent].ChildCounter++;
        BeginKeyed(Hash64.Mix(_r.Boxes[parent].Key, Hash64.Mix(0x0FEUL, local)), parent);
    }

    private void BeginKeyed(ulong key, int parent)
    {
        // Two boxes may share an id; the second is told apart by its order.
        while (_r.BoxIndex.ContainsKey(key)) key = Hash64.Mix(key, 0xD0BUL);

        int index = _r.NewBox();
        ref var p = ref _r.Boxes[parent];
        if (!p.HasChild)
        {
            p.Pre = p.Seg;
            p.HasChild = true;
            p.FirstChild = index;
        }
        else
        {
            if (p.SegHasOps) p.Interleaved = true;
            p.Inter = Hash64.Mix(Hash64.Mix(p.Inter, p.Seg), key);
            _r.Boxes[p.LastChild].NextSibling = index;
        }
        p.LastChild = index;
        p.Children = Hash64.Mix(p.Children, key);
        p.Seg = Hash64.Seed;
        p.SegHasOps = false;

        ref var b = ref _r.Boxes[index];
        b.Key = key;
        InitBox(ref b, parent);
        _r.BoxIndex[key] = index;

        if (_boxDepth == _boxStack.Length) Array.Resize(ref _boxStack, _boxDepth * 2);
        _boxStack[_boxDepth++] = index;
    }

    /// <summary>The node that last began has finished painting, children and all.</summary>
    internal void EndBox()
    {
        int index = _boxStack[--_boxDepth];
        ref var b = ref _r.Boxes[index];
        if (b.HasChild) b.Tail = b.Seg;
        else b.Pre = b.Seg;
        b.Own = b.L > b.R
            ? default
            : new PixelRect((int)MathF.Floor(b.L), (int)MathF.Floor(b.T), (int)MathF.Ceiling(b.R), (int)MathF.Ceiling(b.B));
        b.Subtree = FrameRecord.Union(b.Subtree, b.Own);
        if (b.Parent >= 0)
        {
            ref var p = ref _r.Boxes[b.Parent];
            p.Subtree = FrameRecord.Union(p.Subtree, b.Subtree);
        }
    }

    // ── State ────────────────────────────────────────────────────────────────

    public Matrix3x2 TotalMatrix => _s.M;

    public void Clear(Rgba color)
    {
        _tee?.Clear(color);
        _r.Background = color;
        ref var op = ref NewOp(OpKind.Clear);
        op.Color = color;
        var all = new PixelRect(0, 0, _r.Width, _r.Height);
        // Covers everything and cuts nothing: any clip lies in its interior.
        Draw(ref op, Hash64.Mix(0xC1EAUL, (ulong)color.Argb), all, all, ignoreClip: true);
        op.Covers = true;   // a clear writes every pixel, whatever its alpha
    }

    public void Save()
    {
        _tee?.Save();
        NewOp(OpKind.Save);
        Push(-1);
    }

    public void Restore()
    {
        // An extra restore is ignored, as on a real canvas — and not recorded,
        // so a replay cannot pop the clip it repaints through.
        _tee?.Restore();
        if (_depth == 0) return;
        NewOp(OpKind.Restore);
        int layerOp = _s.LayerOp;
        var inner = _layerBounds[_depth];
        _s = _stack[--_depth];
        _stateDirty = true;
        if (layerOp >= 0)
        {
            ref var lop = ref _r.Ops[layerOp];
            lop.Match = _r.OpCount - 1;
            lop.Bounds = inner;
        }
        // What was drawn inside counts toward the level that holds it.
        _layerBounds[_depth] = FrameRecord.Union(_layerBounds[_depth], inner);
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

    public void Translate(float dx, float dy)
    {
        _tee?.Translate(dx, dy);
        ref var op = ref NewOp(OpKind.Translate);
        op.A = dx; op.B = dy;
        _s.M = Matrix3x2.CreateTranslation(dx, dy) * _s.M;
        _stateDirty = true;
    }

    public void Scale(float sx, float sy)
    {
        _tee?.Scale(sx, sy);
        ref var op = ref NewOp(OpKind.Scale);
        op.A = sx; op.B = sy;
        _s.M = Matrix3x2.CreateScale(sx, sy) * _s.M;
        _stateDirty = true;
    }

    public void SetMatrix(in Matrix3x2 matrix)
    {
        _tee?.SetMatrix(matrix);
        ref var op = ref NewOp(OpKind.SetMatrix);
        op.Matrix = matrix;
        _s.M = matrix;
        _stateDirty = true;
    }

    public void ClipRect(RectF rect)
    {
        _tee?.ClipRect(rect);
        ref var op = ref NewOp(OpKind.ClipRect);
        op.Rect = rect;
        var d = DeviceBounds(rect);
        // An aliased clip lands on whole pixels: rounding out keeps it conservative.
        _s.CL = MathF.Max(_s.CL, MathF.Floor(d.Left));
        _s.CT = MathF.Max(_s.CT, MathF.Floor(d.Top));
        _s.CR = MathF.Min(_s.CR, MathF.Ceiling(d.Right));
        _s.CB = MathF.Min(_s.CB, MathF.Ceiling(d.Bottom));
        _stateDirty = true;
    }

    public void SaveLayerAlpha(byte alpha)
    {
        _tee?.SaveLayerAlpha(alpha);
        ref var op = ref NewOp(OpKind.SaveLayer);
        op.Alpha = alpha;
        op.Match = -1;
        Push(_r.OpCount - 1);
        _s.Layers = Hash64.Mix(Hash64.Mix(_s.Layers, (ulong)alpha), (ulong)_depth);
        _stateDirty = true;
    }

    // ── Shapes ───────────────────────────────────────────────────────────────

    public void DrawRoundRect(RectF rect, float rx, float ry, in RasterPaint paint)
    {
        _tee?.DrawRoundRect(rect, rx, ry, paint);
        ref var op = ref NewOp(OpKind.RoundRect);
        op.Rect = rect; op.A = rx; op.B = ry; op.Paint = paint;

        bool stroke = paint.Spec.Style != PaintStyle.Fill;
        float sw = StrokeHalf(paint);
        var bounds = Inflate(rect, sw);
        // Where a clip may lie without crossing an edge of this shape: a fill's
        // flat interior, clear of the corners; a stroke-only ring's hole.
        // A shadow's coverage is flat three sigma in from its edge (measured
        // exact under a clip there on both backends).
        // The region is the shape pulled in — by a pixel of fringe, a stroke's
        // inner edge (which a stroke-and-fill draws inside its fill) by its whole
        // width, a blur by three sigma — with its corners pulled in by as much.
        PixelRect safe = default;
        RectF shape = default;
        float shapeRadius = 0f;
        if (paint.Dash is null && AxisAligned)
        {
            var d = DeviceBounds(rect);
            float sx = MathF.Abs(_s.M.M11), sy = MathF.Abs(_s.M.M22);
            float inset = 1f + (stroke ? paint.Spec.StrokeWidth * MathF.Max(sx, sy) : 0f)
                          + (paint.MaskBlurSigma > 0f ? (3f * paint.MaskBlurSigma + 1f) * MathF.Max(1f, MathF.Max(sx, sy)) : 0f);
            // A corner that is not a circle in device space is taken as the
            // circle of its larger radius, which holds less.
            float radius = MathF.Min(MathF.Max(rx * sx, ry * sy), MathF.Min(d.Right - d.Left, d.Bottom - d.Top) / 2f);
            shape = new RectF(d.Left + inset, d.Top + inset, d.Right - inset, d.Bottom - inset);
            shapeRadius = MathF.Max(0f, radius - inset);
            if (shape.Right > shape.Left && shape.Bottom > shape.Top)
            {
                var b = new PixelRect((int)MathF.Ceiling(shape.Left), (int)MathF.Ceiling(shape.Top),
                    (int)MathF.Floor(shape.Right), (int)MathF.Floor(shape.Bottom));
                if (b.Width > 0 && b.Height > 0) safe = b;
            }
            // A corner the inset swallowed whole leaves a square interior, held
            // by the rect test alone; a corner bigger than the inset is a curve.
            if (radius > inset) shapeRadius = radius - inset;
        }
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x22UL, rect), rx, ry), paint);
        Draw(ref op, h, Bounds(bounds, paint), safe);
        op.SafeShape = shape;
        op.SafeRadius = safe.Width > 0 ? shapeRadius : 0f;
        // An opaque fill covers its interior whatever lay under it: a repaint
        // wholly inside it need not look at, or replay, anything drawn before.
        op.Covers = !stroke && paint.MaskBlurSigma <= 0f && paint.ImageBlurSigma <= 0f && _s.Layers == 0
                    && paint.Spec.Color.A == 255 && OpaqueShader(paint.Shader) && op.Safe.Width > 0
                    // and only where the clip it was drawn under lets it (tracked rounded out, so a pixel in)
                    && FrameRecord.Contains(new PixelRect((int)_s.CL + 1, (int)_s.CT + 1, (int)_s.CR - 1, (int)_s.CB - 1), op.Safe);
    }

    public void DrawLine(float x0, float y0, float x1, float y1, in RasterPaint paint)
    {
        _tee?.DrawLine(x0, y0, x1, y1, paint);
        ref var op = ref NewOp(OpKind.Line);
        op.A = x0; op.B = y0; op.C = x1; op.D = y1; op.Paint = paint;
        var r = new RectF(MathF.Min(x0, x1), MathF.Min(y0, y1), MathF.Max(x0, x1), MathF.Max(y0, y1));
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x23UL, x0, y0), x1, y1), paint);
        Draw(ref op, h, Bounds(Inflate(r, StrokeHalf(paint) * 1.5f), paint), default);
    }

    public void DrawPath(RasterPath path, in RasterPaint paint)
    {
        _tee?.DrawPath(path, paint);
        ref var op = ref NewOp(OpKind.Path);
        op.Paint = paint;
        if (path.Signature is { } sig)
        {
            op.Ref = sig.Mutable && _tee is null ? _r.CopyPath(path) : path;
            // A miter join reaches up to the miter limit (4) half-widths out; caps and other joins sqrt(2).
            float reach = StrokeHalf(paint) * (paint.Spec.StrokeJoin == StrokeJoin.Miter ? 4f : 1.5f);
            ulong h = Hash64.Mix(Hash64.Mix(0x24UL, sig.Hash), paint);
            Draw(ref op, h, Bounds(Inflate(sig.Bounds, reach), paint), default);
        }
        else
        {
            // Not built by bibui: no content to sign. Drawn, and the frame is unsigned.
            op.Ref = _tee is null ? _r.CopyPath(path) : path;
            _r.Unsigned = true;
            Draw(ref op, Hash64.Mix(0x25UL, (ulong)_r.OpCount), ClipBounds(), default);
        }
    }

    public void DrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, in RasterPaint paint)
    {
        _tee?.DrawPoints(mode, points, paint);
        ref var op = ref NewOp(OpKind.Points);
        op.Mode = mode; op.Paint = paint;
        op.Start = _r.Store(points);
        op.Count = points.Length;
        float l = float.PositiveInfinity, t = float.PositiveInfinity, r = float.NegativeInfinity, b = float.NegativeInfinity;
        foreach (var p in points)
        {
            l = MathF.Min(l, p.X); t = MathF.Min(t, p.Y);
            r = MathF.Max(r, p.X); b = MathF.Max(b, p.Y);
        }
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x26UL, (ulong)mode), points), paint);
        var bounds = points.IsEmpty ? default : Bounds(Inflate(new RectF(l, t, r, b), StrokeHalf(paint) * 1.5f), paint);
        Draw(ref op, h, bounds, default);
    }

    // ── Images ───────────────────────────────────────────────────────────────

    public void DrawImage(RasterImage image, RectF dest, in RasterPaint paint)
    {
        _tee?.DrawImage(image, dest, paint);
        ref var op = ref NewOp(OpKind.Image);
        op.Ref = image; op.Rect = dest; op.Paint = paint;
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x27UL, ImageIds.Of(image)), dest), paint);
        Draw(ref op, h, Bounds(Inflate(dest, 1f), paint), default);
    }

    public void DrawPixels(ReadOnlySpan<byte> rgba, int width, int height, RectF dest, in RasterPaint paint)
    {
        _tee?.DrawPixels(rgba, width, height, dest, paint);
        ref var op = ref NewOp(OpKind.Pixels);
        op.Start = _tee is null ? _r.Store(rgba) : 0;   // kept only to be replayed
        op.Count = rgba.Length;
        op.W = width; op.H = height; op.Rect = dest; op.Paint = paint;
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Mix(0x28UL, Hash64.Bytes(rgba)), (ulong)width << 32 | (uint)height), dest), paint);
        Draw(ref op, h, Bounds(Inflate(dest, 1f), paint), default);
    }

    public void DrawBackdropBlur(PixelRect region, RectF clip, float sigma)
    {
        _tee?.DrawBackdropBlur(region, clip, sigma);
        ref var op = ref NewOp(OpKind.BackdropBlur);
        op.Region = region; op.Rect = clip; op.A = sigma;
        // It writes inside clip and reads all of region, so a damage rect that
        // touches either must take in both: the pixels it reads must be the
        // frame's own, repainted up to this point, not an older frame's.
        var write = Clipped(DeviceBounds(Inflate(clip, 1f)));
        var bounds = FrameRecord.Union(write, region);
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x29UL, clip), sigma), ((ulong)(uint)region.Left << 32) | (uint)region.Top);
        h = Hash64.Mix(h, ((ulong)(uint)region.Right << 32) | (uint)region.Bottom);
        Draw(ref op, h, bounds, default, ignoreClip: true);
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    public float DrawText(string text, float x, float baselineY, in RasterFont font, Rgba color)
    {
        ref var op = ref NewOp(OpKind.Text);
        op.Ref = text; op.A = x; op.B = baselineY; op.Font = font; op.Color = color;
        float advance = _tee is null ? Advance(font, text) : _tee.DrawText(text, x, baselineY, font, color);
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Mix(0x2AUL, text), x, baselineY), (ulong)(uint)font.Typeface.Id << 32 | color.Argb), font.Size);
        Draw(ref op, h, Bounds(TextBox(x, x + advance, baselineY, font), default), TextSafe(font));
        return advance;
    }

    public float DrawText(ReadOnlySpan<byte> utf8, float x, float baselineY, in RasterFont font, Rgba color)
    {
        float drawn = _tee?.DrawText(utf8, x, baselineY, font, color) ?? 0f;
        ref var op = ref NewOp(OpKind.TextUtf8);
        // The bytes are the caller's and may be reused once this returns: kept to be replayed.
        op.Start = _tee is null ? _r.Store(utf8) : 0;
        op.Count = utf8.Length;
        op.A = x; op.B = baselineY; op.Font = font; op.Color = color;
        ulong content = Hash64.Bytes(utf8);
        float advance = _tee is null ? Advance(font, content, utf8) : drawn;
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Mix(0x2DUL, (ulong)utf8.Length), content), x, baselineY),
            (ulong)(uint)font.Typeface.Id << 32 | color.Argb), font.Size);
        Draw(ref op, h, Bounds(TextBox(x, x + advance, baselineY, font), default), TextSafe(font));
        return advance;
    }

    public void DrawGlyphRun(ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions,
        float x, float baselineY, in RasterFont font, Rgba color)
    {
        _tee?.DrawGlyphRun(glyphs, positions, x, baselineY, font, color);
        ref var op = ref NewOp(OpKind.GlyphRun);
        op.Start = _r.Store(glyphs);
        op.Start2 = _r.Store(positions);
        op.Count = glyphs.Length;
        op.A = x; op.B = baselineY; op.Font = font; op.Color = color;
        float l = 0f, r = 0f;
        foreach (var p in positions) { l = MathF.Min(l, p.X); r = MathF.Max(r, p.X); }
        float t = 0f, b = 0f;
        foreach (var p in positions) { t = MathF.Min(t, p.Y); b = MathF.Max(b, p.Y); }
        ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x2BUL, glyphs), positions), x, baselineY);
        h = Hash64.Mix(Hash64.Mix(h, (ulong)(uint)font.Typeface.Id << 32 | color.Argb), font.Size);
        var box = TextBox(x + l, x + r + font.Size, baselineY, font);
        box = new RectF(box.Left, box.Top + t, box.Right, box.Bottom + b);
        Draw(ref op, h, Bounds(box, default), TextSafe(font));
    }

    // A run's advance is what DrawText would have returned, which a recorded
    // frame must hand back without drawing. Measuring shapes the run, and the
    // replay shapes it again to draw it, so the same runs frame after frame are
    // answered from here: two frames' worth, the older half dropped as it fills.
    private Dictionary<(int, float, string), float> _advances = [], _olderAdvances = [];

    // UTF-8 runs are keyed by a 64-bit hash of their bytes: a collision would
    // hand back another run's advance, at odds of 2^-64 per pair of runs.
    private Dictionary<(int, float, ulong, int), float> _utf8Advances = [], _olderUtf8Advances = [];

    private float Advance(in RasterFont font, ulong content, ReadOnlySpan<byte> utf8)
    {
        var key = (font.Typeface.Id, font.Size, content, utf8.Length);
        if (_utf8Advances.TryGetValue(key, out float a)) return a;
        if (!_olderUtf8Advances.TryGetValue(key, out a)) a = backend.MeasureText(font, utf8);
        if (_utf8Advances.Count >= 8192)
        {
            (_olderUtf8Advances, _utf8Advances) = (_utf8Advances, _olderUtf8Advances);
            _utf8Advances.Clear();
        }
        _utf8Advances[key] = a;
        return a;
    }

    private float Advance(in RasterFont font, string text)
    {
        var key = (font.Typeface.Id, font.Size, text);
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

    // ── Deferred ─────────────────────────────────────────────────────────────

    public void Defer(Action<IRasterCanvas> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (_tee is not null) draw(_tee);
        ref var op = ref NewOp(OpKind.Deferred);
        op.Ref = draw;
        _r.Unsigned = true;
        Draw(ref op, Hash64.Mix(0x2CUL, (ulong)_r.OpCount), ClipBounds(), default);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private ref RecOp NewOp(OpKind kind)
    {
        ref var op = ref _r.NewOp();
        op.Kind = kind;
        return ref op;
    }

    /// <summary>Signs a draw into the box being painted and gives it its bounds.</summary>
    private void Draw(ref RecOp op, ulong contentHash, PixelRect device, PixelRect safe, bool ignoreClip = false)
    {
        op.Draws = true;
        op.Bounds = ignoreClip ? device : Clipped(device);
        op.Safe = safe;

        if (_stateDirty)
        {
            _stateHash = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Seed, _s.M), _s.CL, _s.CT), _s.CR, _s.CB);
            _stateHash = Hash64.Mix(_stateHash, _s.Layers);
            _stateDirty = false;
        }

        ref var b = ref _r.Boxes[_boxStack[_boxDepth - 1]];
        b.Seg = Hash64.Mix(Hash64.Mix(b.Seg, _stateHash), contentHash);
        b.SegHasOps = true;
        var ob = op.Bounds;
        if (ob.Width > 0 && ob.Height > 0)
        {
            b.L = MathF.Min(b.L, ob.Left); b.T = MathF.Min(b.T, ob.Top);
            b.R = MathF.Max(b.R, ob.Right); b.B = MathF.Max(b.B, ob.Bottom);
            _layerBounds[_depth] = FrameRecord.Union(_layerBounds[_depth], ob);
        }
    }

    private static bool OpaqueShader(RasterShader? shader)
    {
        if (shader is null) return true;
        foreach (var c in shader.Colors) if (c.A != 255) return false;
        return true;
    }

    private bool AxisAligned => _s.M.M12 == 0f && _s.M.M21 == 0f;

    private float MatrixScale => MathF.Max(
        MathF.Sqrt(_s.M.M11 * _s.M.M11 + _s.M.M12 * _s.M.M12),
        MathF.Sqrt(_s.M.M21 * _s.M.M21 + _s.M.M22 * _s.M.M22));

    /// <summary>Half a stroke's width, local units — how far it reaches past its geometry — a hairline's half pixel at least; 0 for a fill.</summary>
    private static float StrokeHalf(in RasterPaint paint) =>
        paint.Spec.Style == PaintStyle.Fill ? 0f : MathF.Max(1f, paint.Spec.StrokeWidth) / 2f;

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

    // Glyphs are blitted from masks, which a clip cuts exactly on both backends
    // (measured): text never needs the damage grown around it. Not so for
    // text big enough to be drawn as paths.
    private static readonly PixelRect Anywhere = new(-(1 << 28), -(1 << 28), 1 << 28, 1 << 28);

    private PixelRect TextSafe(in RasterFont font) => font.Size * MatrixScale <= 96f ? Anywhere : default;

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
        // geometry touches (measured on both backends), so half a pixel
        // and rounding out is conservative. A blur spreads 3 sigma.
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
