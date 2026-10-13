using System.Numerics;
using Bibui.Raster;
using Bibui.Renderer.Damage;

namespace Bibui;

/// <summary>
/// One frame, recorded instead of drawn — every raster call it made, in
/// order, and a paint signature for every box that made them. Two records say
/// where their frames differ (<see cref="DamageSince"/>) without either being
/// drawn, and a record draws itself onto a surface that still holds an earlier
/// frame, repainting only the rects it is given (<see cref="Replay"/>).
///
/// <para>Filled by <see cref="BibuiRenderer.Record"/>. A record is reused from
/// frame to frame (its arrays are kept), so a host keeps two and alternates:
/// the previous frame to diff against, and the one being recorded.</para>
///
/// <para><b>What a signature is.</b> Every draw is hashed with everything that
/// decides its pixels: its arguments, its paint (colour, stroke, shader, blur,
/// dash), its text and font, its path's content, and the canvas state it runs
/// under — transform, clip, enclosing opacity layers. Draws are attributed to the
/// innermost box being painted, so a box's signature covers the pixels it draws
/// itself: the resolved theme colours, its hot and active blend, its caret. A
/// change that moves nothing — an app-state colour, a text edit, an eased
/// <c>Animate</c> value — changes the draw, and so the signature, and is found
/// without anyone saying it happened.</para>
///
/// <para><b>Why the pixels are exact.</b> A pixel outside the damage is covered
/// by the same draws, in the same order, in both frames: a box whose own draws
/// changed is damaged over all of them (old and new), and one whose children
/// were reordered is damaged over its whole subtree. Inside the damage the frame
/// is cleared and replayed, through one integer rect clip at a time. Three raster
/// facts shape that clip (measured on Skia; the managed backend is exact under
/// any clip): a clip edge through an anti-aliased edge changes its coverage, so
/// each rect is grown until it cuts no draw it touches — it holds the draw whole,
/// lies wholly in its flat interior, or the cut is painted over by a later
/// opaque fill; a multi-rect clip changes coverage even where it cuts nothing,
/// so a list of rects is replayed one rect at a time, never as a region; and an
/// opacity layer rasterises relative to its own origin, so the rect clips each
/// draw rather than the frame, and a layer is allocated over the clip it had in
/// the whole paint. A backdrop blur reads what is under it, so a rect that
/// touches one takes in all it reads.</para>
/// </summary>
public sealed class FrameRecord
{
    // ── Ops ──────────────────────────────────────────────────────────────────
    internal RecOp[] Ops = new RecOp[512];
    internal int OpCount;

    // Side storage for spans an op carried (glyph runs, points, pixels), so an
    // op stays a fixed-size struct and nothing is allocated per frame once warm.
    internal ushort[] Glyphs = new ushort[256];
    internal int GlyphCount;
    internal Vector2[] Vecs = new Vector2[256];
    internal int VecCount;
    internal byte[] Bytes = [];
    internal int ByteCount;

    // Copies of paths that will not hold still until the replay (the scratch
    // path, paths bibui did not build). Backend paths, rewound and reused.
    internal readonly List<RasterPath> PathCopies = [];
    internal int PathCopyCount;

    // ── Boxes ────────────────────────────────────────────────────────────────
    internal BoxRec[] Boxes = new BoxRec[256];
    internal int BoxCount;
    internal readonly Dictionary<ulong, int> BoxIndex = [];

    /// <summary>Physical width of the surface the frame was recorded for.</summary>
    public int Width { get; internal set; }

    /// <summary>Physical height of the surface the frame was recorded for.</summary>
    public int Height { get; internal set; }

    /// <summary>The colour the frame starts from (the theme's background).</summary>
    public Rgba Background { get; internal set; }

    /// <summary>
    /// Whether the frame drew something it could not sign — a path bibui did
    /// not build, a draw deferred to the real canvas (a Skia custom draw). Such
    /// a frame has no exact damage, against the frame before or after it.
    /// </summary>
    public bool Unsigned { get; internal set; }

    /// <summary>How many raster calls the frame recorded.</summary>
    internal int OpCountTotal => OpCount;

    internal IRasterBackend? Backend;

    /// <summary>The frame was painted as it was recorded (it was known to be whole), and is not replayed.</summary>
    internal bool Teed;

    internal void Reset(int width, int height, IRasterBackend backend)
    {
        // Drop references the last frame held so nothing outlives its frame here.
        for (int i = 0; i < OpCount; i++) Ops[i].Ref = null;
        OpCount = 0;
        GlyphCount = 0;
        VecCount = 0;
        ByteCount = 0;
        PathCopyCount = 0;
        BoxCount = 0;
        BoxIndex.Clear();
        Width = width;
        Height = height;
        Background = default;
        Unsigned = false;
        if (!ReferenceEquals(Backend, backend))
        {
            foreach (var p in PathCopies) p.Dispose();
            PathCopies.Clear();
            Backend = backend;
        }
    }

    internal ref RecOp NewOp()
    {
        if (OpCount == Ops.Length) Array.Resize(ref Ops, Ops.Length * 2);
        ref var op = ref Ops[OpCount++];
        op = default;
        return ref op;
    }

    internal int NewBox()
    {
        if (BoxCount == Boxes.Length) Array.Resize(ref Boxes, Boxes.Length * 2);
        Boxes[BoxCount] = default;
        return BoxCount++;
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

    /// <summary>A backend path holding what <paramref name="source"/> holds now.</summary>
    internal RasterPath CopyPath(RasterPath source)
    {
        if (PathCopyCount == PathCopies.Count) PathCopies.Add(Backend!.CreatePath());
        var copy = PathCopies[PathCopyCount++];
        copy.Rewind();
        copy.AddPath(source, Matrix3x2.Identity);
        return copy;
    }

    // ── Damage ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends to <paramref name="into"/> the rects (physical px) outside which
    /// this frame's pixels equal <paramref name="previous"/>'s. Empty when the
    /// two frames are the same picture. Returns false when no such answer can be
    /// given — a different surface size, or a frame that is
    /// <see cref="Unsigned"/> — and the frame must be painted whole.
    ///
    /// <para>The rects are not yet fit to clip by: <see cref="Settle"/> them first.</para>
    /// </summary>
    internal bool DamageSince(FrameRecord previous, List<PixelRect> into)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(into);
        if (ReferenceEquals(previous, this)) throw new ArgumentException("A record cannot be diffed against itself.", nameof(previous));
        if (Unsigned || previous.Unsigned) return false;
        if (Width != previous.Width || Height != previous.Height) return false;

        for (int i = 0; i < BoxCount; i++)
        {
            ref var b = ref Boxes[i];
            if (!previous.BoxIndex.TryGetValue(b.Key, out int j))
            {
                Add(into, b.Own);   // new: what it draws now
                continue;
            }
            ref var o = ref previous.Boxes[j];
            bool ownSame = b.Pre == o.Pre && b.Tail == o.Tail && b.Interleaved == o.Interleaved
                           && (!b.Interleaved || b.Inter == o.Inter);
            if (!ownSame)
            {
                Add(into, o.Own);
                Add(into, b.Own);
            }
            if (b.Children != o.Children
                && (b.Interleaved || o.Interleaved || !CommonChildrenInOrder(previous, j, i)))
            {
                // The order its children paint in moved: anything under it may
                // now land above or below something else.
                Add(into, o.Subtree);
                Add(into, b.Subtree);
            }
        }
        for (int j = 0; j < previous.BoxCount; j++)
        {
            ref var o = ref previous.Boxes[j];
            if (!BoxIndex.ContainsKey(o.Key)) Add(into, o.Own);   // gone: what it drew
        }
        Merge(into, settle: false);
        return true;

        void Add(List<PixelRect> list, PixelRect r)
        {
            r = Clamp(r);
            if (r.Width > 0 && r.Height > 0) list.Add(r);
        }
    }

    // Children both frames share, in the order each paints them. Equal means
    // that only additions and removals happened, and those are damaged as boxes
    // of their own; the rest still paint in the same order relative to each
    // other and to their parent's draws (which come all before or all after).
    private bool CommonChildrenInOrder(FrameRecord previous, int oldIndex, int newIndex)
    {
        int a = previous.Boxes[oldIndex].FirstChild;
        int b = Boxes[newIndex].FirstChild;
        while (true)
        {
            while (a >= 0 && !BoxIndex.ContainsKey(previous.Boxes[a].Key)) a = previous.Boxes[a].NextSibling;
            while (b >= 0 && !previous.BoxIndex.ContainsKey(Boxes[b].Key)) b = Boxes[b].NextSibling;
            if (a < 0 || b < 0) return a < 0 && b < 0;
            if (previous.Boxes[a].Key != Boxes[b].Key) return false;
            a = previous.Boxes[a].NextSibling;
            b = Boxes[b].NextSibling;
        }
    }

    /// <summary>
    /// Makes <paramref name="rects"/> safe to repaint this frame through, one
    /// at a time: each grows until it cuts no draw it touches (a rect holds a
    /// draw whole, or lies in its flat interior), takes in whatever a backdrop
    /// blur it touches reads, and rects that then overlap are merged. At most
    /// <paramref name="maxRects"/> remain; past that the closest are merged.
    /// </summary>
    public void Settle(List<PixelRect> rects, int maxRects = 8)
    {
        ArgumentNullException.ThrowIfNull(rects);
        Merge(rects, settle: false);   // overlaps first, so nothing is grown twice
        for (int i = 0; i < rects.Count; i++) rects[i] = Grow(rects[i]);
        Merge(rects, settle: true, maxRects);
    }

    private static readonly bool s_trace = Environment.GetEnvironmentVariable("BIBUI_DAMAGE_TRACE") == "1";

    private PixelRect Grow(PixelRect r)
    {
        bool grew;
        do
        {
            grew = false;
            for (int k = CoveredUpTo(r); k < OpCount; k++)
            {
                ref var op = ref Ops[k];
                if (!op.Draws) continue;
                var b = op.Bounds;
                if (!Intersects(b, r) || Contains(r, b)) continue;
                if (InSafe(ref op, r)) continue;
                if (HiddenLater(k, Intersect(b, r))) continue;
                if (s_trace) Console.WriteLine($"[grow] op {k} {op.Kind} bounds ({b.Left},{b.Top})-({b.Right},{b.Bottom}) safe ({op.Safe.Left},{op.Safe.Top})-({op.Safe.Right},{op.Safe.Bottom}) cuts ({r.Left},{r.Top})-({r.Right},{r.Bottom})");
                r = Union(r, b);
                grew = true;
            }
        }
        while (grew);
        return Clamp(r);
    }

    /// <summary>
    /// Whether every pixel op <paramref name="k"/> puts in <paramref name="part"/>
    /// is painted over by an opaque fill after it, and nothing in between reads
    /// them (a backdrop blur). Then a clip that cuts op k changes no pixel that
    /// shows: a shadow cut where the card it lies under covers it.
    /// </summary>
    private bool HiddenLater(int k, PixelRect part)
    {
        for (int j = k + 1; j < OpCount; j++)
        {
            ref var op = ref Ops[j];
            if (op.Kind == OpKind.BackdropBlur && Intersects(op.Region, part)) return false;
            if (op.Covers && InSafe(ref op, part)) return true;
        }
        return false;
    }

    internal static PixelRect Intersect(PixelRect a, PixelRect b) => new(
        Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));

    /// <summary>
    /// The last op that writes every pixel of <paramref name="r"/> whatever lay
    /// under it — an opaque fill whose flat interior holds r, else the clear.
    /// Nothing drawn before it shows in r, so nothing before it is replayed into
    /// r or needs the clip kept off its edges.
    /// </summary>
    private int CoveredUpTo(PixelRect r)
    {
        for (int k = OpCount - 1; k > 0; k--)
        {
            ref var op = ref Ops[k];
            if (op.Covers && InSafe(ref op, r)) return k;
        }
        return 0;
    }

    /// <summary>
    /// Whether <paramref name="r"/> lies wholly in the op's safe region: its
    /// bounding rect, or, for a rounded rect, the rounded rect itself — convex,
    /// so it holds r exactly when it holds r's four corners.
    /// </summary>
    private static bool InSafe(ref RecOp op, PixelRect r)
    {
        if (!Contains(op.Safe, r)) return false;
        if (op.SafeRadius <= 0f) return true;
        return InRoundRect(op.SafeShape, op.SafeRadius, r.Left, r.Top) && InRoundRect(op.SafeShape, op.SafeRadius, r.Right, r.Top)
            && InRoundRect(op.SafeShape, op.SafeRadius, r.Left, r.Bottom) && InRoundRect(op.SafeShape, op.SafeRadius, r.Right, r.Bottom);

        static bool InRoundRect(RectF s, float rad, float x, float y)
        {
            if (x < s.Left || x > s.Right || y < s.Top || y > s.Bottom) return false;
            rad = MathF.Min(rad, MathF.Min(s.Right - s.Left, s.Bottom - s.Top) / 2f);
            float cx = Math.Clamp(x, s.Left + rad, s.Right - rad);
            float cy = Math.Clamp(y, s.Top + rad, s.Bottom - rad);
            float dx = x - cx, dy = y - cy;
            return dx * dx + dy * dy <= rad * rad;
        }
    }

    // Past this many rects (a scroll moves every box in a view) the merge below,
    // quadratic per pass, costs more than it saves: they are taken as the one
    // rect that bounds them all.
    private const int MaxMergeRects = 32;

    private void Merge(List<PixelRect> rects, bool settle, int maxRects = int.MaxValue)
    {
        rects.RemoveAll(r => r.Width <= 0 || r.Height <= 0);
        if (rects.Count > MaxMergeRects)
        {
            var all = rects[0];
            foreach (var r in rects) all = Union(all, r);
            rects.Clear();
            rects.Add(settle ? Grow(all) : all);
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
                var u = Union(rects[i], rects[j]);
                rects[i] = settle ? Grow(u) : u;
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
                var u = Union(rects[bi], rects[bj]);
                rects[bi] = settle ? Grow(u) : u;
                rects.RemoveAt(bj);
                merged = true;
            }
        }
    }

    // ── Replay ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether <see cref="Replay"/> can repaint part of a surface: the frame is
    /// signed and starts from an opaque colour, which a clipped fill puts back
    /// exactly (a translucent one would land on the old frame instead).
    /// </summary>
    internal bool CanRepaintPartially => !Unsigned && Background.A == 255;

    /// <summary>
    /// Draws the frame onto <paramref name="target"/>. With
    /// <paramref name="rects"/> null, the whole frame, as a direct paint would.
    /// Otherwise the target must hold a frame that equals this one outside the
    /// rects — an earlier frame, with the damage since then <see cref="Settle"/>d —
    /// and each rect is cleared and repainted on its own, through one clip.
    /// </summary>
    public void Replay(IRasterSurface target, IReadOnlyList<PixelRect>? rects)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Teed) throw new InvalidOperationException("This frame was painted as it was recorded; it keeps nothing to replay.");
        var c = target.Canvas;
        if (rects is null)
        {
            for (int k = 0; k < OpCount; k++) Exec(c, ref Ops[k], k);
            return;
        }
        if (!CanRepaintPartially) throw new InvalidOperationException("This frame can only be painted whole.");

        var clear = new RasterPaint(new PaintSpec { Color = Background, Style = PaintStyle.Fill, IsAntialias = false });
        foreach (var r in rects)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            var rf = new RectF(r.Left, r.Top, r.Right, r.Bottom);
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
                if (!op.Draws) { Exec(c, ref op, k); continue; }
                if (k < from || !Intersects(op.Bounds, r)) continue;
                // The rect clips each draw, not the frame: an opacity layer must be
                // allocated over the clip it had in the whole paint, because Skia
                // rasterises inside a layer relative to the layer's origin and a
                // different origin rounds differently (measured). Content
                // the clip keeps out of a layer leaves it transparent there, and a
                // transparent layer composites to exactly what was under it.
                var m = c.TotalMatrix;
                c.Save();
                c.SetMatrix(Matrix3x2.Identity);
                c.ClipRect(rf);
                c.SetMatrix(m);
                Exec(c, ref op, k);
                c.Restore();
            }
        }
    }

    private void Exec(IRasterCanvas c, ref RecOp op, int index)
    {
        switch (op.Kind)
        {
            case OpKind.Clear: c.Clear(op.Color); break;
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
            case OpKind.Text: c.DrawText((string)op.Ref!, op.A, op.B, op.Font, op.Color); break;
            case OpKind.TextUtf8: c.DrawText(Bytes.AsSpan(op.Start, op.Count), op.A, op.B, op.Font, op.Color); break;
            case OpKind.GlyphRun:
                c.DrawGlyphRun(Glyphs.AsSpan(op.Start, op.Count), Vecs.AsSpan(op.Start2, op.Count), op.A, op.B, op.Font, op.Color);
                break;
            case OpKind.Deferred: ((Action<IRasterCanvas>)op.Ref!)(c); break;
            default: throw new InvalidOperationException($"op {index}: unknown kind {op.Kind}");
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

    /// <summary>The summed area of <paramref name="rects"/> — an upper bound on the pixels they cover.</summary>
    public static long AreaOf(IReadOnlyList<PixelRect> rects)
    {
        long a = 0;
        foreach (var r in rects) a += Area(r);
        return a;
    }
}
