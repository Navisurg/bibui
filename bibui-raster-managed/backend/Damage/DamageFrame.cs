using System.Numerics;
using Bibui.Draw;

namespace Bibui.Raster.Managed.Backend.Damage;

/// <summary>What one op of a frame's list touches, and how it is signed. A fixed struct in one array, reused frame to frame.</summary>
internal struct DamageOp
{
    public bool Draws;          // touches pixels (and is subject to the damage clip)
    public int Match;           // PushOpacity: the index of its Pop
    public PixelRect Bounds;    // device px every pixel it may touch lies in (PushOpacity: its contents')
    public ulong Hash;          // a draw: its content under the state it ran in
    public PixelRect Covered;   // device px it writes whatever lay under them (an opaque fill's interior, the clear); empty for none
}

/// <summary>
/// One frame's signature: for every op of its <see cref="DrawList"/>, whether it
/// draws, a hash of everything that decides its pixels — its arguments, paint,
/// text, path content, and the state it runs under (transform, clip, enclosing
/// opacity layers) — and the device bounds it can touch. Two frames say where
/// their pixels can differ (<see cref="DamageSince"/>) without either being
/// drawn, and a frame redraws its list onto a surface that still holds an
/// earlier one, only inside the rects it is given (<see cref="Replay"/>).
///
/// <para><b>Why the pixels are exact.</b> A pixel is the result of the draws
/// that touch it, in order. The two frames' draws are matched by hash, keeping
/// their order (any order-preserving matching will do); every draw left
/// unmatched, old or new, damages all it may touch. So a pixel outside the
/// damage is touched by the same draws, in the same order, in both frames, and
/// is the same pixel. A backdrop blur reads around what it writes, so damage
/// that touches one takes in all it reads and writes. Inside the damage the
/// frame is cleared and redrawn through one rect clip at a time; the managed
/// rasterizer's coverage is the same under any clip, so no rect has to be grown
/// to keep its edges off anti-aliased ones.</para>
/// </summary>
internal sealed class DamageFrame
{
    internal DamageOp[] Ops = new DamageOp[512];
    internal int OpCount;

    // The draws among the ops, in order: what two frames are matched on.
    internal int[] Draws = new int[256];
    internal int DrawCount;

    internal int Width, Height;
    internal Rgba Background;

    // ── Signing ──────────────────────────────────────────────────────────────

    private struct State
    {
        public Matrix3x2 M;
        public float CL, CT, CR, CB;   // device clip, conservative (rounded out)
        public ulong Layers;
        public int LayerOp;            // the PushOpacity this level opened, or -1
    }

    private State _s;
    private State[] _stack = new State[32];
    private int _depth;
    private ulong _stateHash;
    private bool _stateDirty;

    // Draw bounds still open per layer level, to give a PushOpacity the bounds
    // of what it holds when it is popped.
    private PixelRect[] _layerBounds = new PixelRect[32];

    // A run of text's metrics, per face and size, for the frame's life and after.
    private readonly Dictionary<(int, float), TextMetrics> _metrics = [];

    /// <summary>
    /// Signs every op of <paramref name="list"/>, a frame of
    /// <paramref name="width"/> × <paramref name="height"/> physical px drawn
    /// by <paramref name="renderer"/>, whose resolved paths and texture versions
    /// the signature reads.
    /// </summary>
    internal void Sign(ManagedRenderer renderer, DrawList list, int width, int height)
    {
        var ops = list.Ops;
        if (Ops.Length < ops.Length) Ops = new DamageOp[Math.Max(ops.Length, Ops.Length * 2)];
        OpCount = ops.Length;
        Width = width;
        Height = height;
        Background = default;
        _s = new State { M = Matrix3x2.Identity, CL = 0, CT = 0, CR = width, CB = height, Layers = 0, LayerOp = -1 };
        _depth = 0;
        _layerBounds[0] = default;
        _stateDirty = true;

        for (int k = 0; k < ops.Length; k++)
        {
            ref readonly var op = ref ops[k];
            ref var d = ref Ops[k];
            d = default;
            switch (op.Kind)
            {
                case DrawOpKind.Clear:
                {
                    Background = op.Color;
                    var all = new PixelRect(0, 0, width, height);
                    Draw(ref d, Hash64.Mix(0xC1EAUL, (ulong)op.Color.Argb), all, ignoreClip: true);
                    d.Covered = all;   // a clear writes every pixel, whatever its alpha
                    break;
                }
                case DrawOpKind.PushClip:
                {
                    Push(-1);
                    var b = DeviceBounds(op.Rect);
                    // An aliased clip lands on whole pixels: rounding out keeps it conservative.
                    _s.CL = MathF.Max(_s.CL, MathF.Floor(b.Left));
                    _s.CT = MathF.Max(_s.CT, MathF.Floor(b.Top));
                    _s.CR = MathF.Min(_s.CR, MathF.Ceiling(b.Right));
                    _s.CB = MathF.Min(_s.CB, MathF.Ceiling(b.Bottom));
                    _stateDirty = true;
                    break;
                }
                case DrawOpKind.PushTransform:
                    Push(-1);
                    _s.M = op.Matrix * _s.M;
                    _stateDirty = true;
                    break;
                case DrawOpKind.PushOpacity:
                    d.Match = -1;
                    Push(k);
                    _s.Layers = Hash64.Mix(Hash64.Mix(_s.Layers, (ulong)op.Alpha), (ulong)_depth);
                    _stateDirty = true;
                    break;
                case DrawOpKind.Pop:
                    Pop(k);
                    break;
                case DrawOpKind.Rect:
                {
                    ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x22UL, op.Rect), op.A, op.B), PaintHash(op, list));
                    Draw(ref d, h, Bounds(Inflate(op.Rect, StrokeHalf(op)), op));
                    d.Covered = Covered(op, list);
                    break;
                }
                case DrawOpKind.Line:
                {
                    var r = new RectF(MathF.Min(op.A, op.C), MathF.Min(op.B, op.D), MathF.Max(op.A, op.C), MathF.Max(op.B, op.D));
                    ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x23UL, op.A, op.B), op.C, op.D), PaintHash(op, list));
                    Draw(ref d, h, Bounds(Inflate(r, StrokeHalf(op) * 1.5f), op));
                    break;
                }
                case DrawOpKind.Path:
                {
                    var (path, content) = renderer.PathOf(k);
                    var b = path.Bounds;
                    // A miter join reaches up to the miter limit (4) half-widths out; caps and other joins sqrt(2).
                    float reach = StrokeHalf(op) * (op.Paint.StrokeJoin == StrokeJoin.Miter ? 4f : 1.5f);
                    ulong h = Hash64.Mix(Hash64.Mix(0x24UL, content), PaintHash(op, list));
                    Draw(ref d, h, path.IsEmpty ? default : Bounds(Inflate(new RectF(b.Left, b.Top, b.Right, b.Bottom), reach), op));
                    break;
                }
                case DrawOpKind.Points:
                {
                    var points = list.Points.Slice(op.Start, op.Count);
                    float l = float.PositiveInfinity, t = float.PositiveInfinity, r = float.NegativeInfinity, b = float.NegativeInfinity;
                    foreach (var p in points)
                    {
                        l = MathF.Min(l, p.X); t = MathF.Min(t, p.Y);
                        r = MathF.Max(r, p.X); b = MathF.Max(b, p.Y);
                    }
                    ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x26UL, (ulong)op.Mode), points), PaintHash(op, list));
                    Draw(ref d, h, points.IsEmpty ? default : Bounds(Inflate(new RectF(l, t, r, b), StrokeHalf(op) * 1.5f), op));
                    break;
                }
                case DrawOpKind.Texture:
                {
                    // Signed by its handle and its content version: the handle
                    // stays the same when its pixels are updated.
                    ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x27UL, op.Texture.Id), op.Texture.Generation), renderer.TextureVersion(op.Texture));
                    h = Hash64.Mix(Hash64.Mix(h, op.Rect), PaintHash(op, list));
                    Draw(ref d, h, Bounds(Inflate(op.Rect, 1f), op));
                    break;
                }
                case DrawOpKind.BackdropBlur:
                {
                    // It writes inside its clip and reads all of its region: its
                    // bounds are both, and Settle makes damage that touches them
                    // take them in.
                    var write = Clipped(DeviceBounds(Inflate(op.Rect, 1f)));
                    var bounds = Union(write, op.Region);
                    var g = op.Region;
                    ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x29UL, op.Rect), op.A), ((ulong)(uint)g.Left << 32) | (uint)g.Top);
                    h = Hash64.Mix(h, ((ulong)(uint)g.Right << 32) | (uint)g.Bottom);
                    Draw(ref d, h, bounds, ignoreClip: true);
                    break;
                }
                case DrawOpKind.Glyphs:
                {
                    var face = list.FaceAt(op.Resource);
                    var glyphs = list.Glyphs.Slice(op.Start, op.Count);
                    var positions = list.Points.Slice(op.Start2, op.Count);
                    float x = op.A, baselineY = op.B, sizePx = op.C;
                    float l = 0f, r = 0f, t = 0f, b = 0f;
                    foreach (var p in positions)
                    {
                        l = MathF.Min(l, p.X); r = MathF.Max(r, p.X);
                        t = MathF.Min(t, p.Y); b = MathF.Max(b, p.Y);
                    }
                    ulong h = Hash64.Mix(Hash64.Mix(Hash64.Mix(0x2BUL, glyphs), positions), x, baselineY);
                    h = Hash64.Mix(Hash64.Mix(h, (ulong)(uint)face.Id << 32 | op.Color.Argb), sizePx);
                    var box = TextBox(x + l, x + r + sizePx, baselineY, face, sizePx);
                    box = new RectF(box.Left, box.Top + t, box.Right, box.Bottom + b);
                    Draw(ref d, h, Bounds(box, blur: 0f));
                    break;
                }
            }
        }

        DrawCount = 0;
        for (int k = 0; k < OpCount; k++)
        {
            if (!Ops[k].Draws) continue;
            if (DrawCount == Draws.Length) Array.Resize(ref Draws, Draws.Length * 2);
            Draws[DrawCount++] = k;
        }
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

    private void Pop(int k)
    {
        if (_depth == 0) return;
        int layerOp = _s.LayerOp;
        var inner = _layerBounds[_depth];
        _s = _stack[--_depth];
        _stateDirty = true;
        if (layerOp >= 0)
        {
            ref var lop = ref Ops[layerOp];
            lop.Match = k;
            lop.Bounds = inner;
        }
        // What was drawn inside counts toward the level that holds it.
        _layerBounds[_depth] = Union(_layerBounds[_depth], inner);
    }

    /// <summary>Signs a draw under the current state and gives it its bounds.</summary>
    private void Draw(ref DamageOp d, ulong contentHash, PixelRect device, bool ignoreClip = false)
    {
        d.Draws = true;
        d.Bounds = ignoreClip ? device : Clipped(device);

        if (_stateDirty)
        {
            _stateHash = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Seed, _s.M), _s.CL, _s.CT), _s.CR, _s.CB);
            _stateHash = Hash64.Mix(_stateHash, _s.Layers);
            _stateDirty = false;
        }
        d.Hash = Hash64.Mix(_stateHash, contentHash);

        var b = d.Bounds;
        if (b.Width > 0 && b.Height > 0) _layerBounds[_depth] = Union(_layerBounds[_depth], b);
    }

    /// <summary>Every field of an op's paint, its gradient by content.</summary>
    private static ulong PaintHash(in DrawOp op, DrawList list)
    {
        var s = op.Paint;
        ulong h = Hash64.Mix(Hash64.Seed, ((ulong)s.Color.Argb << 32) | ((ulong)(byte)s.Style << 24) | ((ulong)(byte)s.StrokeCap << 16)
                   | ((ulong)(byte)s.StrokeJoin << 8) | ((ulong)(byte)s.FilterQuality << 1) | (s.IsAntialias ? 1UL : 0UL));
        h = Hash64.Mix(h, s.StrokeWidth, op.MaskBlurSigma);
        h = Hash64.Mix(h, op.ImageBlurSigma);
        if (op.Shading.Present)
        {
            var g = op.Shading;
            var key = CacheKey.OfGradient(g.Type == GradientType.Radial ? 1 : 0, g.Start, g.End, g.Radius,
                list.Colors.Slice(g.ColorStart, g.ColorCount), list.Floats.Slice(g.StopStart, g.StopCount));
            h = Hash64.Mix(Hash64.Mix(h, (ulong)key.Hash), (ulong)(key.Hash >> 64));
        }
        if (op.Dashed) h = Hash64.Mix(Hash64.Mix(h, op.Dash.On, op.Dash.Off), op.Dash.Phase);
        return h;
    }

    /// <summary>
    /// The device pixels an opaque fill writes whatever lay under them: the
    /// rect pulled in a pixel past its anti-aliased edge and past its corners,
    /// inside the clip it ran under. Empty for anything that blends: a stroke,
    /// a blur, a translucent colour or gradient, or a fill inside an opacity layer.
    /// </summary>
    private PixelRect Covered(in DrawOp op, DrawList list)
    {
        if (op.Paint.Style != Bibui.PaintStyle.Fill || op.MaskBlurSigma > 0f || op.ImageBlurSigma > 0f
            || _s.Layers != 0 || op.Paint.Color.A != 255 || !AxisAligned) return default;
        if (op.Shading.Present)
            foreach (var c in list.Colors.Slice(op.Shading.ColorStart, op.Shading.ColorCount)) if (c.A != 255) return default;
        var d = DeviceBounds(op.Rect);
        float inset = 1f + MathF.Max(op.A * MathF.Abs(_s.M.M11), op.B * MathF.Abs(_s.M.M22));
        // The clip is tracked rounded out, so a pixel in from it is surely inside it.
        var r = new PixelRect(
            (int)MathF.Ceiling(MathF.Max(d.Left + inset, _s.CL + 1f)), (int)MathF.Ceiling(MathF.Max(d.Top + inset, _s.CT + 1f)),
            (int)MathF.Floor(MathF.Min(d.Right - inset, _s.CR - 1f)), (int)MathF.Floor(MathF.Min(d.Bottom - inset, _s.CB - 1f)));
        return r.Width > 0 && r.Height > 0 ? r : default;
    }

    private bool AxisAligned => _s.M.M12 == 0f && _s.M.M21 == 0f;

    private float MatrixScale => MathF.Max(
        MathF.Sqrt(_s.M.M11 * _s.M.M11 + _s.M.M12 * _s.M.M12),
        MathF.Sqrt(_s.M.M21 * _s.M.M21 + _s.M.M22 * _s.M.M22));

    /// <summary>Half a stroke's width, local units — how far it reaches past its geometry — a hairline's half pixel at least; 0 for a fill.</summary>
    private static float StrokeHalf(in DrawOp op) =>
        op.Paint.Style == Bibui.PaintStyle.Fill ? 0f : MathF.Max(1f, op.Paint.StrokeWidth) / 2f;

    private static RectF Inflate(RectF r, float d) => new(
        MathF.Min(r.Left, r.Right) - d, MathF.Min(r.Top, r.Bottom) - d,
        MathF.Max(r.Left, r.Right) + d, MathF.Max(r.Top, r.Bottom) + d);

    // A run of text's ink: the face's ascent and descent, and room past both
    // ends and above and below for what overhangs them (italics, a wide 'f',
    // stacked accents).
    private RectF TextBox(float left, float right, float baseline, FontFace face, float sizePx)
    {
        var key = (face.Id, sizePx);
        if (!_metrics.TryGetValue(key, out var m))
        {
            var fm = ManagedRenderer.Strike(face, sizePx).Metrics;
            _metrics[key] = m = new TextMetrics(fm.Ascent, fm.Descent, fm.Leading);
        }
        float pad = sizePx * 0.25f + 1f;
        return new(left - pad, baseline + MathF.Min(m.Ascent, -sizePx) - pad, right + pad, baseline + MathF.Max(m.Descent, sizePx * 0.3f) + pad);
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

    private PixelRect Bounds(RectF local, in DrawOp op) => Bounds(local, MathF.Max(op.MaskBlurSigma, op.ImageBlurSigma));

    /// <summary>
    /// Device pixels a draw of <paramref name="local"/> may touch: mapped
    /// through the transform, widened for a blur and for the anti-aliased
    /// fringe, rounded out.
    /// </summary>
    private PixelRect Bounds(RectF local, float blur)
    {
        var d = DeviceBounds(local);
        // Half a pixel of anti-aliased fringe: ink lands only in pixels its
        // geometry touches, so half a pixel and rounding out is conservative.
        // A blur spreads 3 sigma.
        float grow = 0.5f + (blur > 0f ? (3f * blur + 1f) * MathF.Max(1f, MatrixScale) : 0f);
        if (float.IsNaN(d.Left) || float.IsInfinity(d.Left) || float.IsNaN(d.Right) || float.IsInfinity(d.Right))
            return new PixelRect((int)_s.CL, (int)_s.CT, (int)_s.CR, (int)_s.CB);
        return new PixelRect(
            (int)MathF.Floor(MathF.Max(-1e6f, d.Left - grow)), (int)MathF.Floor(MathF.Max(-1e6f, d.Top - grow)),
            (int)MathF.Ceiling(MathF.Min(1e6f, d.Right + grow)), (int)MathF.Ceiling(MathF.Min(1e6f, d.Bottom + grow)));
    }

    private PixelRect Clipped(PixelRect r) => Clipped(new RectF(r.Left, r.Top, r.Right, r.Bottom));

    private PixelRect Clipped(RectF d)
    {
        var r = new PixelRect(
            (int)MathF.Floor(MathF.Max(d.Left, _s.CL)), (int)MathF.Floor(MathF.Max(d.Top, _s.CT)),
            (int)MathF.Ceiling(MathF.Min(d.Right, _s.CR)), (int)MathF.Ceiling(MathF.Min(d.Bottom, _s.CB)));
        return r.Width > 0 && r.Height > 0 ? r : default;
    }

    // ── Damage ───────────────────────────────────────────────────────────────

    // Matching scratch, kept so a frame's diff allocates nothing once warm.
    private readonly Dictionary<ulong, int> _first = [];
    private int[] _next = [];
    private int[] _pairNew = [], _pairOld = [], _tails = [], _prevInLis = [];
    private bool[] _newMatched = [], _oldMatched = [];
    private readonly HashSet<PixelRect> _seen = [];

    /// <summary>
    /// Appends to <paramref name="into"/> the rects (physical px) outside which
    /// this frame's pixels equal <paramref name="previous"/>'s: every draw of
    /// either frame that the other has no match for, in order. Empty when the
    /// two frames are the same picture. The frames must be the same size.
    /// </summary>
    internal void DamageSince(DamageFrame previous, List<PixelRect> into)
    {
        int nN = DrawCount, nO = previous.DrawCount;

        // Most frames differ in a few draws: the same prefix and suffix are matched first.
        int pre = 0;
        while (pre < nN && pre < nO && Ops[Draws[pre]].Hash == previous.Ops[previous.Draws[pre]].Hash) pre++;
        int suf = 0;
        while (suf < nN - pre && suf < nO - pre
               && Ops[Draws[nN - 1 - suf]].Hash == previous.Ops[previous.Draws[nO - 1 - suf]].Hash) suf++;
        int nEnd = nN - suf, oEnd = nO - suf;
        if (pre == nEnd && pre == oEnd) return;

        // Pair the k-th draw of a hash in the new middle with the k-th in the old one.
        Grow(ref _next, nO);
        _first.Clear();
        for (int j = oEnd - 1; j >= pre; j--)
        {
            ulong h = previous.Ops[previous.Draws[j]].Hash;
            _next[j] = _first.TryGetValue(h, out int f) ? f : -1;
            _first[h] = j;
        }
        Grow(ref _pairNew, nEnd - pre);
        Grow(ref _pairOld, nEnd - pre);
        int pairs = 0;
        for (int i = pre; i < nEnd; i++)
        {
            ulong h = Ops[Draws[i]].Hash;
            if (!_first.TryGetValue(h, out int j) || j < 0) continue;
            _first[h] = _next[j];
            _pairNew[pairs] = i;
            _pairOld[pairs] = j;
            pairs++;
        }

        // Of those pairs, the longest run whose old positions increase keeps
        // both frames' order: those draws match, the rest do not.
        Grow(ref _newMatched, nN);
        Grow(ref _oldMatched, nO);
        Array.Clear(_newMatched, pre, nEnd - pre);
        Array.Clear(_oldMatched, pre, oEnd - pre);
        Grow(ref _tails, pairs);
        Grow(ref _prevInLis, pairs);
        int len = 0;
        for (int p = 0; p < pairs; p++)
        {
            int lo = 0, hi = len;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_pairOld[_tails[mid]] < _pairOld[p]) lo = mid + 1;
                else hi = mid;
            }
            _prevInLis[p] = lo > 0 ? _tails[lo - 1] : -1;
            _tails[lo] = p;
            if (lo == len) len++;
        }
        for (int p = len > 0 ? _tails[len - 1] : -1; p >= 0; p = _prevInLis[p])
        {
            _newMatched[_pairNew[p]] = true;
            _oldMatched[_pairOld[p]] = true;
        }

        for (int i = pre; i < nEnd; i++)
            if (!_newMatched[i]) Add(into, Ops[Draws[i]].Bounds);
        for (int j = pre; j < oEnd; j++)
            if (!_oldMatched[j]) Add(into, previous.Ops[previous.Draws[j]].Bounds);

        // A box's draws come one after another and mostly nest (a fill, its
        // border, its text): one that the rect before it holds, or that holds
        // it, is folded in here, so a changed box is about one rect, not one a draw.
        void Add(List<PixelRect> list, PixelRect r)
        {
            r = Clamp(r);
            if (r.Width <= 0 || r.Height <= 0) return;
            if (list.Count > 0)
            {
                var last = list[^1];
                if (Contains(last, r)) return;
                if (Contains(r, last)) { list[^1] = r; return; }
            }
            list.Add(r);
        }
    }

    private static void Grow<T>(ref T[] array, int size)
    {
        if (array.Length < size) array = new T[Math.Max(size, array.Length * 2)];
    }

    /// <summary>
    /// Makes <paramref name="rects"/> fit to repaint this frame through, one at
    /// a time: a rect that touches a backdrop blur takes in all the blur reads
    /// and writes, and rects that overlap are merged. At most
    /// <paramref name="maxRects"/> remain; past that the closest are merged.
    /// </summary>
    internal void Settle(DrawList list, List<PixelRect> rects, int maxRects = 8)
    {
        for (int i = 0; i < rects.Count; i++) rects[i] = Clamp(rects[i]);
        // The damage of several frames repeats itself (a hover fading out is the same box every frame).
        if (rects.Count > 1)
        {
            _seen.Clear();
            for (int i = rects.Count - 1; i >= 0; i--)
                if (!_seen.Add(rects[i])) rects.RemoveAt(i);
        }
        Merge(rects, int.MaxValue);
        var ops = list.Ops;
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int k = 0; k < OpCount; k++)
            {
                if (ops[k].Kind != DrawOpKind.BackdropBlur) continue;
                var b = Clamp(Ops[k].Bounds);
                if (b.Width <= 0 || b.Height <= 0) continue;
                int n = rects.Count;
                for (int i = 0; i < n; i++)
                {
                    if (!Intersects(rects[i], b) || Contains(rects[i], b)) continue;
                    rects.Add(b);
                    grew = true;
                    break;
                }
            }
            if (grew) Merge(rects, int.MaxValue);
        }
        Merge(rects, maxRects);
    }

    // Past this many rects (a scroll moves every draw in a view) the merge below,
    // quadratic per pass, costs more than it saves: they are taken as the one
    // rect that bounds them all.
    private const int MaxMergeRects = 32;

    private static void Merge(List<PixelRect> rects, int maxRects)
    {
        rects.RemoveAll(r => r.Width <= 0 || r.Height <= 0);
        if (rects.Count > MaxMergeRects)
        {
            var all = rects[0];
            foreach (var r in rects) all = Union(all, r);
            rects.Clear();
            rects.Add(all);
            return;
        }
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < rects.Count && !merged; i++)
            for (int j = i + 1; j < rects.Count && !merged; j++)
            {
                if (!Intersects(rects[i], rects[j]) && !Contains(rects[i], rects[j]) && !Contains(rects[j], rects[i])) continue;
                rects[i] = Union(rects[i], rects[j]);
                rects.RemoveAt(j);
                merged = true;
            }
            if (!merged && rects.Count > maxRects)
            {
                // Too many: merge the pair whose union wastes the least.
                long best = long.MaxValue; int bi = 0, bj = 1;
                for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                {
                    long waste = Area(Union(rects[i], rects[j])) - Area(rects[i]) - Area(rects[j]);
                    if (waste < best) { best = waste; bi = i; bj = j; }
                }
                rects[bi] = Union(rects[bi], rects[bj]);
                rects.RemoveAt(bj);
                merged = true;
            }
        }
    }

    // ── Replay ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws <paramref name="list"/>, the frame this signs, onto
    /// <paramref name="c"/>, which must hold a frame that equals this one outside
    /// <paramref name="rects"/>: each rect is cleared to the (opaque) background
    /// and repainted on its own.
    /// </summary>
    internal void Replay(ManagedRenderer renderer, DrawList list, Canvas c, IReadOnlyList<PixelRect> rects)
    {
        var ops = list.Ops;
        var clear = new Paint { Color = Background.Argb, Style = PaintStyle.Fill, Aliased = true };
        foreach (var r in rects)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            var rect = new Rect(r.Left, r.Top, r.Right, r.Bottom);
            // Nothing drawn before the last op that covers all of r shows in r.
            int from = CoveredUpTo(r);
            if (from == 0)
            {
                c.Save();
                c.ClipRect(rect);
                c.DrawRoundRect(rect.Sorted(), 0f, 0f, clear);
                c.Restore();
            }
            for (int k = 0; k < OpCount; k++)
            {
                ref var op = ref Ops[k];
                switch (ops[k].Kind)
                {
                    case DrawOpKind.Clear:
                        continue;
                    case DrawOpKind.PushOpacity when k < from || !Intersects(op.Bounds, r):
                        k = op.Match;   // the whole layer is covered or lands outside: skip to its Pop, and past it
                        continue;
                }
                if (!op.Draws) { renderer.Exec(list, k, c); continue; }
                if (k < from || !Intersects(op.Bounds, r)) continue;
                // The rect clips each draw, not the frame, so an opacity layer is
                // allocated over the clip it had in the whole paint. Content the
                // clip keeps out of a layer leaves it transparent there, and a
                // transparent layer composites to exactly what was under it.
                var m = c.Matrix;
                c.Save();
                c.SetMatrix(Matrix3x2.Identity);
                c.ClipRect(rect);
                c.SetMatrix(m);
                renderer.Exec(list, k, c);
                c.Restore();
            }
        }
    }

    /// <summary>
    /// The last op that writes every pixel of <paramref name="r"/> whatever lay
    /// under it, else 0 (the clear). Nothing drawn before it shows in r, so
    /// nothing before it is replayed into r.
    /// </summary>
    private int CoveredUpTo(PixelRect r)
    {
        for (int k = OpCount - 1; k > 0; k--)
            if (Contains(Ops[k].Covered, r)) return k;
        return 0;
    }

    // ── Rect helpers ─────────────────────────────────────────────────────────

    internal PixelRect Clamp(PixelRect r) => new(
        Math.Max(0, r.Left), Math.Max(0, r.Top), Math.Min(Width, r.Right), Math.Min(Height, r.Bottom));

    internal static bool Intersects(PixelRect a, PixelRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom
        && a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0;

    /// <summary>Whether <paramref name="outer"/> holds all of <paramref name="inner"/> (an empty outer holds nothing).</summary>
    internal static bool Contains(PixelRect outer, PixelRect inner) =>
        outer.Width > 0 && outer.Height > 0
        && outer.Left <= inner.Left && outer.Top <= inner.Top && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    internal static PixelRect Union(PixelRect a, PixelRect b)
    {
        if (a.Width <= 0 || a.Height <= 0) return b;
        if (b.Width <= 0 || b.Height <= 0) return a;
        return new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
    }

    internal static long Area(PixelRect r) => r.Width <= 0 || r.Height <= 0 ? 0 : (long)r.Width * r.Height;

    internal static long AreaOf(IReadOnlyList<PixelRect> rects)
    {
        long a = 0;
        foreach (var r in rects) a += Area(r);
        return a;
    }
}
