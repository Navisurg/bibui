using System.Numerics;
using Bibui.Raster;

namespace Bibui.Renderer.Damage;

/// <summary>
/// What a damage-tracked frame knows about a path without asking the
/// backend — a hash of the verbs that built it and bounds that contain every
/// point it can cover before stroking. <see cref="Mutable"/> marks a path that
/// is rebuilt in place (the scratch path), so a recorded frame copies it rather
/// than keep a reference that the next use rewrites.
/// </summary>
internal readonly record struct PathSignature(ulong Hash, RectF Bounds, bool Mutable);

/// <summary>
/// A path that builds a backend path and signs it as it goes: every verb is
/// forwarded to <see cref="Inner"/> unchanged and folded into a hash, and the
/// bounds grow to hold every point and control point (a curve stays inside its
/// control hull, an arc inside its ellipse). <see cref="Commit"/> stamps the
/// signature on the inner path and hands that back, so nothing downstream ever
/// sees this wrapper — a backend draws its own path, as it always has.
/// </summary>
internal sealed class SignaturePath(RasterPath inner) : RasterPath
{
    public RasterPath Inner { get; } = inner;

    private ulong _hash = Hash64.Seed;
    private float _l = float.PositiveInfinity, _t = float.PositiveInfinity;
    private float _r = float.NegativeInfinity, _b = float.NegativeInfinity;
    private Vector2 _last, _start;
    private bool _unknown;

    private void Extend(float x, float y)
    {
        if (x < _l) _l = x;
        if (y < _t) _t = y;
        if (x > _r) _r = x;
        if (y > _b) _b = y;
    }

    private void Extend(in RectF r)
    {
        Extend(r.Left, r.Top);
        Extend(r.Right, r.Bottom);
    }

    public override void MoveTo(float x, float y)
    {
        Inner.MoveTo(x, y);
        _hash = Hash64.Mix(Hash64.Mix(_hash, 1UL), x, y);
        Extend(x, y);
        _last = _start = new(x, y);
    }

    public override void LineTo(float x, float y)
    {
        Inner.LineTo(x, y);
        _hash = Hash64.Mix(Hash64.Mix(_hash, 2UL), x, y);
        Extend(x, y);
        _last = new(x, y);
    }

    public override void QuadTo(float x1, float y1, float x2, float y2)
    {
        Inner.QuadTo(x1, y1, x2, y2);
        _hash = Hash64.Mix(Hash64.Mix(Hash64.Mix(_hash, 3UL), x1, y1), x2, y2);
        Extend(x1, y1);
        Extend(x2, y2);
        _last = new(x2, y2);
    }

    public override void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        Inner.CubicTo(x1, y1, x2, y2, x3, y3);
        _hash = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Mix(_hash, 4UL), x1, y1), x2, y2), x3, y3);
        Extend(x1, y1);
        Extend(x2, y2);
        Extend(x3, y3);
        _last = new(x3, y3);
    }

    public override void ArcTo(float rx, float ry, float xAxisRotate, bool largeArc, bool sweep, float x, float y)
    {
        Inner.ArcTo(rx, ry, xAxisRotate, largeArc, sweep, x, y);
        _hash = Hash64.Mix(Hash64.Mix(Hash64.Mix(Hash64.Mix(_hash, 5UL), rx, ry), xAxisRotate,
            (largeArc ? 1f : 0f) + (sweep ? 2f : 0f)), x, y);
        // The arc lies on its ellipse, so the ellipse's box holds it: find the
        // centre the SVG way (endpoint to centre parameterisation, radii scaled
        // up when they are too small to join the ends) and take the rotated
        // ellipse's extent around it.
        ExtendArc(_last, new Vector2(x, y), MathF.Abs(rx), MathF.Abs(ry), xAxisRotate, largeArc, sweep);
        Extend(x, y);
        _last = new(x, y);
    }

    private void ExtendArc(Vector2 p0, Vector2 p1, float rx, float ry, float rotateDeg, bool largeArc, bool sweep)
    {
        if (rx == 0f || ry == 0f || p0 == p1) { Extend(p0.X, p0.Y); return; }   // a line, or nothing
        float phi = rotateDeg * MathF.PI / 180f;
        float c = MathF.Cos(phi), s = MathF.Sin(phi);
        float dx = (p0.X - p1.X) / 2f, dy = (p0.Y - p1.Y) / 2f;
        float x1 = c * dx + s * dy, y1 = -s * dx + c * dy;
        float lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1f) { float k = MathF.Sqrt(lambda); rx *= k; ry *= k; }
        float num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        float den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        float coef = den <= 0f ? 0f : MathF.Sqrt(MathF.Max(0f, num / den)) * (largeArc == sweep ? -1f : 1f);
        float cx1 = coef * rx * y1 / ry, cy1 = -coef * ry * x1 / rx;
        float cx = c * cx1 - s * cy1 + (p0.X + p1.X) / 2f;
        float cy = s * cx1 + c * cy1 + (p0.Y + p1.Y) / 2f;
        float ex = MathF.Sqrt(rx * rx * c * c + ry * ry * s * s);
        float ey = MathF.Sqrt(rx * rx * s * s + ry * ry * c * c);
        // A hair over, for the float round trip the backend makes of the same sums.
        ex = ex * 1.001f + 0.01f;
        ey = ey * 1.001f + 0.01f;
        if (float.IsFinite(cx) && float.IsFinite(cy) && float.IsFinite(ex) && float.IsFinite(ey))
        {
            Extend(cx - ex, cy - ey);
            Extend(cx + ex, cy + ey);
        }
        else
        {
            float reach = MathF.Max(MathF.Max(rx, ry) * 2f, Vector2.Distance(p0, p1));
            Extend(p0.X - reach, p0.Y - reach);
            Extend(p0.X + reach, p0.Y + reach);
        }
        Extend(p0.X, p0.Y);
    }

    public override void AddArc(RectF oval, float startAngle, float sweepAngle)
    {
        Inner.AddArc(oval, startAngle, sweepAngle);
        _hash = Hash64.Mix(Hash64.Mix(Hash64.Mix(_hash, 6UL), oval), startAngle, sweepAngle);
        Extend(oval);
    }

    public override void Close()
    {
        Inner.Close();
        _hash = Hash64.Mix(_hash, 7UL);
        _last = _start;
    }

    public override void AddPath(RasterPath source, in Matrix3x2 transform)
    {
        // A signed source contributes its signature; an unsigned one makes this
        // path unsigned too, since nothing here can see what it holds.
        PathSignature? sig = source is SignaturePath sp ? sp.Current() : source.Signature;
        Inner.AddPath(source is SignaturePath s ? s.Inner : source, transform);
        if (sig is not { } g) { _unknown = true; return; }
        _hash = Hash64.Mix(Hash64.Mix(Hash64.Mix(_hash, 8UL), g.Hash), transform);
        var b = g.Bounds;
        Span<Vector2> corners = [new(b.Left, b.Top), new(b.Right, b.Top), new(b.Left, b.Bottom), new(b.Right, b.Bottom)];
        foreach (var c in corners)
        {
            var p = Vector2.Transform(c, transform);
            Extend(p.X, p.Y);
        }
    }

    public override void Rewind()
    {
        Inner.Rewind();
        _hash = Hash64.Seed;
        _l = _t = float.PositiveInfinity;
        _r = _b = float.NegativeInfinity;
        _last = _start = default;
        _unknown = false;
    }

    public override bool IsEmpty => Inner.IsEmpty;

    /// <summary>The signature of what has been built so far, or null when a part of it is unknown.</summary>
    public PathSignature? Current(bool mutable = false) =>
        _unknown ? null
        : new PathSignature(_hash, _l > _r ? new RectF(0f, 0f, 0f, 0f) : new RectF(_l, _t, _r, _b), mutable);

    /// <summary>Stamps the signature on <see cref="Inner"/> and returns it, ready to draw.</summary>
    public RasterPath Commit(bool mutable)
    {
        Inner.Signature = Current(mutable);
        return Inner;
    }
}
