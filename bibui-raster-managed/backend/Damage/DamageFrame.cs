using System.Numerics;

namespace Bibui.Raster.Managed.Backend.Damage;

internal enum OpKind : byte
{
    Clear, Save, Restore, Translate, Scale, SetMatrix, ClipRect, SaveLayer,
    RoundRect, Line, Path, Points, Image, Pixels, BackdropBlur, Glyphs,
}

/// <summary>One recorded canvas call. Fat on purpose: a fixed struct in one array, reused frame to frame.</summary>
internal struct DamageOp
{
    public OpKind Kind;
    public bool Draws;          // touches pixels (and is subject to the damage clip)
    public byte Alpha;          // SaveLayer
    public PointMode Mode;      // Points
    public RectF Rect;          // rrect / image dest / clip / backdrop clip
    public float A, B, C, D;    // radii, line ends, text origin, translate, scale, sigma
    public RasterPaint Paint;
    public object? Ref;         // path, image
    public FontFace? Face;      // Glyphs
    public float Size;          // Glyphs: px per em
    public Rgba Color;
    public int Start, Count, W, H;
    public int Start2;          // GlyphRun: where its positions start in Vecs
    public PixelRect Region;    // backdrop read region
    public Matrix3x2 Matrix;    // SetMatrix
    public int Match;           // SaveLayer: index of its Restore
    public PixelRect Bounds;    // device px every pixel it may touch lies in (SaveLayer: its contents')
    public ulong Hash;          // a draw: its content under the canvas state it ran in
    public PixelRect Covered;   // device px it writes whatever lay under them (an opaque fill's interior, the clear); empty for none
}

/// <summary>
/// One frame, recorded instead of drawn: every canvas call it made, in order,
/// each draw signed by a hash of everything that decides its pixels — its
/// arguments, paint, text, path content, and the canvas state it runs under
/// (transform, clip, enclosing opacity layers). Two frames say where their
/// pixels can differ (<see cref="DamageSince"/>) without either being drawn,
/// and a frame draws itself onto a surface that still holds an earlier one,
/// repainting only the rects it is given (<see cref="Replay"/>).
///
/// <para><b>Why the pixels are exact.</b> A pixel is the result of the draws
/// that touch it, in order. The two frames' draws are matched by hash, keeping
/// their order (any order-preserving matching will do); every draw left
/// unmatched, old or new, damages all it may touch. So a pixel outside the
/// damage is touched by the same draws, in the same order, in both frames, and
/// is the same pixel. A backdrop blur reads around what it writes, so damage
/// that touches one takes in all it reads and writes. Inside the damage the
/// frame is cleared and replayed through one rect clip at a time; the managed
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

    // Side storage for spans an op carried (glyphs and their positions, points, pixels),
    // so an op stays a fixed-size struct and nothing is allocated once warm.
    internal ushort[] Glyphs = new ushort[256];
    internal int GlyphCount;
    internal Vector2[] Vecs = new Vector2[256];
    internal int VecCount;
    internal byte[] Bytes = [];
    internal int ByteCount;

    // Copies of paths that changed before the frame was replayed (the scratch
    // path, rewound and rebuilt for the next draw). Reused frame to frame.
    private readonly List<ManagedPath> _pathCopies = [];
    private int _pathCopyCount;

    internal int Width, Height;
    internal Rgba Background;

    /// <summary>The frame was drawn as it was recorded (it was known to be whole); it keeps nothing to replay.</summary>
    internal bool Teed;

    internal void Reset(int width, int height, bool teed)
    {
        // Drop references the last frame held so nothing outlives its frame here.
        for (int i = 0; i < OpCount; i++) (Ops[i].Ref, Ops[i].Face) = (null, null);
        OpCount = DrawCount = GlyphCount = VecCount = ByteCount = _pathCopyCount = 0;
        Width = width;
        Height = height;
        Background = default;
        Teed = teed;
    }

    internal ref DamageOp NewOp(OpKind kind)
    {
        if (OpCount == Ops.Length) Array.Resize(ref Ops, Ops.Length * 2);
        ref var op = ref Ops[OpCount++];
        op = default;
        op.Kind = kind;
        return ref op;
    }

    /// <summary>Lists the draws, once the frame is complete.</summary>
    internal void Finish()
    {
        DrawCount = 0;
        for (int i = 0; i < OpCount; i++)
        {
            if (!Ops[i].Draws) continue;
            if (DrawCount == Draws.Length) Array.Resize(ref Draws, Draws.Length * 2);
            Draws[DrawCount++] = i;
        }
    }

    internal int Store(ReadOnlySpan<ushort> glyphs)
    {
        Ensure(ref Glyphs, GlyphCount + glyphs.Length);
        glyphs.CopyTo(Glyphs.AsSpan(GlyphCount));
        int at = GlyphCount;
        GlyphCount += glyphs.Length;
        return at;
    }

    internal int Store(ReadOnlySpan<Vector2> vecs)
    {
        Ensure(ref Vecs, VecCount + vecs.Length);
        vecs.CopyTo(Vecs.AsSpan(VecCount));
        int at = VecCount;
        VecCount += vecs.Length;
        return at;
    }

    internal int Store(ReadOnlySpan<byte> bytes)
    {
        Ensure(ref Bytes, ByteCount + bytes.Length);
        bytes.CopyTo(Bytes.AsSpan(ByteCount));
        int at = ByteCount;
        ByteCount += bytes.Length;
        return at;
    }

    private static void Ensure<T>(ref T[] array, int size)
    {
        if (array.Length >= size) return;
        Array.Resize(ref array, Math.Max(size, array.Length * 2));
    }

    /// <summary>A path holding what <paramref name="source"/> holds now.</summary>
    internal ManagedPath CopyPath(ManagedPath source)
    {
        if (_pathCopyCount == _pathCopies.Count) _pathCopies.Add(new ManagedPath());
        var copy = _pathCopies[_pathCopyCount++];
        copy.Native.Rewind();
        copy.Native.AddPath(source.Native, Matrix3x2.Identity);
        copy.Native.FillRule = source.Native.FillRule;
        return copy;
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
    internal void Settle(List<PixelRect> rects, int maxRects = 8)
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
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int k = 0; k < OpCount; k++)
            {
                ref var op = ref Ops[k];
                if (op.Kind != OpKind.BackdropBlur) continue;
                var b = Clamp(op.Bounds);
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
    /// Draws the frame onto <paramref name="c"/>, which must not be recording.
    /// With <paramref name="rects"/> null, the whole frame. Otherwise the canvas
    /// must hold a frame that equals this one outside the rects, and each rect
    /// is cleared to the (opaque) background and repainted on its own.
    /// </summary>
    internal void Replay(ManagedRasterCanvas c, IReadOnlyList<PixelRect>? rects)
    {
        if (Teed) throw new InvalidOperationException("This frame was drawn as it was recorded; it keeps nothing to replay.");
        if (rects is null)
        {
            for (int k = 0; k < OpCount; k++) Exec(c, ref Ops[k]);
            return;
        }

        var clear = new RasterPaint(new PaintSpec { Color = Background, Style = Bibui.PaintStyle.Fill, IsAntialias = false });
        foreach (var r in rects)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            var rf = new RectF(r.Left, r.Top, r.Right, r.Bottom);
            // Nothing drawn before the last op that covers all of r shows in r.
            int from = CoveredUpTo(r);
            if (from == 0)
            {
                c.Save();
                c.ClipRect(rf);
                c.DrawRoundRect(rf, 0f, 0f, clear);
                c.Restore();
            }
            for (int k = 0; k < OpCount; k++)
            {
                ref var op = ref Ops[k];
                switch (op.Kind)
                {
                    case OpKind.Clear:
                        continue;
                    case OpKind.SaveLayer when k < from || !Intersects(op.Bounds, r):
                        k = op.Match;   // the whole layer is covered or lands outside: skip to its Restore, and past it
                        continue;
                }
                if (!op.Draws) { Exec(c, ref op); continue; }
                if (k < from || !Intersects(op.Bounds, r)) continue;
                // The rect clips each draw, not the frame, so an opacity layer is
                // allocated over the clip it had in the whole paint. Content the
                // clip keeps out of a layer leaves it transparent there, and a
                // transparent layer composites to exactly what was under it.
                var m = c.TotalMatrix;
                c.Save();
                c.SetMatrix(Matrix3x2.Identity);
                c.ClipRect(rf);
                c.SetMatrix(m);
                Exec(c, ref op);
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

    private void Exec(ManagedRasterCanvas c, ref DamageOp op)
    {
        switch (op.Kind)
        {
            case OpKind.Clear: c.ClearDirect(op.Color); break;
            case OpKind.Save: c.Save(); break;
            case OpKind.Restore: c.Restore(); break;
            case OpKind.Translate: c.Translate(op.A, op.B); break;
            case OpKind.Scale: c.Scale(op.A, op.B); break;
            case OpKind.SetMatrix: c.SetMatrix(op.Matrix); break;
            case OpKind.ClipRect: c.ClipRect(op.Rect); break;
            case OpKind.SaveLayer: c.SaveLayerAlpha(op.Alpha); break;
            case OpKind.RoundRect: c.DrawRoundRect(op.Rect, op.A, op.B, op.Paint); break;
            case OpKind.Line: c.DrawLine(op.A, op.B, op.C, op.D, op.Paint); break;
            case OpKind.Path: c.DrawPath((RasterPath)op.Ref!, op.Paint); break;
            case OpKind.Points: c.DrawPoints(op.Mode, Vecs.AsSpan(op.Start, op.Count), op.Paint); break;
            case OpKind.Image: c.DrawImage((RasterImage)op.Ref!, op.Rect, op.Paint); break;
            case OpKind.Pixels: c.DrawPixels(Bytes.AsSpan(op.Start, op.Count), op.W, op.H, op.Rect, op.Paint); break;
            case OpKind.BackdropBlur: c.DrawBackdropBlur(op.Region, op.Rect, op.A); break;
            case OpKind.Glyphs:
                c.DrawGlyphs(op.Face!, op.Size, Glyphs.AsSpan(op.Start, op.Count), Vecs.AsSpan(op.Start2, op.Count), op.A, op.B, op.Color);
                break;
            default: throw new InvalidOperationException($"unknown op kind {op.Kind}");
        }
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
