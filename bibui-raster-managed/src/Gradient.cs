using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed;

/// <summary>Which way a <see cref="GradientShader"/> runs.</summary>
public enum GradientKind
{
    /// <summary>Along the line from the start point to the end point.</summary>
    Linear = 0,
    /// <summary>Outwards from a centre to a radius.</summary>
    Radial = 1,
}

/// <summary>
/// A linear or radial gradient with any number of stops, clamped at its ends,
/// computed per pixel in float:
///
/// <list type="bullet">
/// <item>Stops are normalised: a missing
/// stop at 0 or 1 repeats the end colour, and positions are pinned to be
/// non-decreasing in [0, 1]. Equal positions are a hard stop.</item>
/// <item>t is sampled at pixel centres through the inverse transform, then
/// clamped; colours are interpolated <em>unpremultiplied</em> and premultiplied
/// afterwards.</item>
/// <item>Each channel is rounded to 8 bits. No dithering: bibui never asks
/// for it.</item>
/// <item>A degenerate gradient — start and end within 1/32768 px, or a radius
/// that small — is the last colour.</item>
/// </list>
///
/// <para>This reproduces Skia 2.88's raster-pipeline gradient to the rounding
/// (<c>SkGradientShaderBase</c>'s stop normalisation, unpremultiplied
/// interpolation, the store's rounding, no dither, the degenerate case under
/// clamp): the fixture baselines are drawn by the Skia backend, so the managed backend matches its pixels bit for bit to be verified against them.</para>
///
/// <para>Each span is shaded <see cref="Vector{T}.Count"/> pixels at a time.
/// Immutable once built, so one instance can serve every draw of the same
/// gradient.</para>
/// </summary>
public sealed class GradientShader : Shader
{
    private const float DegenerateThreshold = 1f / (1 << 15);

    // Interval k covers [Ts[k], Ts[k+1]); interval 0 is everything before the
    // first stop and the last one everything after the last, both constant.
    // colour = t·F + B per channel, unpremultiplied, in 0..1.
    private readonly float[] _ts;
    private readonly Vector4[] _f;
    private readonly Vector4[] _b;
    private readonly bool _opaque;
    private readonly bool _twoStop;
    private readonly uint _solid;   // premultiplied, when degenerate
    private readonly bool _degenerate;

    // Device pixel centre → t (linear) or → unit-circle coordinates (radial).
    private float _ax, _ay, _ac, _bx, _by, _bc;

    public GradientKind Kind { get; }
    public Vector2 Start { get; }
    public Vector2 End { get; }
    public float Radius { get; }

    /// <summary>A linear gradient from <paramref name="start"/> (t = 0) to <paramref name="end"/> (t = 1).</summary>
    public static GradientShader Linear(Vector2 start, Vector2 end, ReadOnlySpan<uint> argb, ReadOnlySpan<float> positions = default)
        => new(GradientKind.Linear, start, end, 0f, argb, positions);

    /// <summary>A radial gradient: t = 0 at <paramref name="centre"/>, 1 at <paramref name="radius"/>.</summary>
    public static GradientShader Radial(Vector2 centre, float radius, ReadOnlySpan<uint> argb, ReadOnlySpan<float> positions = default)
        => new(GradientKind.Radial, centre, centre, radius, argb, positions);

    /// <param name="argb">Straight-alpha <c>0xAARRGGBB</c> colours, at least one.</param>
    /// <param name="positions">One position per colour, or empty to spread them evenly.</param>
    public GradientShader(GradientKind kind, Vector2 start, Vector2 end, float radius, ReadOnlySpan<uint> argb, ReadOnlySpan<float> positions)
    {
        if (argb.IsEmpty) throw new ArgumentException("A gradient needs at least one colour.", nameof(argb));
        if (!positions.IsEmpty && positions.Length != argb.Length)
            throw new ArgumentException("One position per colour, or none.", nameof(positions));
        Kind = kind;
        Start = start;
        End = end;
        Radius = radius;

        bool opaque = true;
        foreach (uint c in argb) opaque &= (c >> 24) == 255;
        _opaque = opaque;

        _degenerate = argb.Length == 1
            || (kind == GradientKind.Linear ? Vector2.Distance(start, end) <= DegenerateThreshold : !(MathF.Abs(radius) > DegenerateThreshold));
        if (_degenerate)
        {
            // One colour is a colour shader; a degenerate clamped gradient is its last colour.
            _solid = PixelMath.Premul(argb[^1]);
            _ts = [];
            _f = _b = [];
            return;
        }

        // SkGradientShaderBase: positions 0 and 1 are forced, a missing end stop
        // repeats the end colour, and each position is pinned to [previous, 1].
        int n = argb.Length;
        var pos = new List<float>(n + 2);
        var col = new List<Vector4>(n + 2);
        if (positions.IsEmpty)
        {
            for (int i = 0; i < n; i++)
            {
                pos.Add(n == 1 ? 0 : (float)i / (n - 1));
                col.Add(Unpack(argb[i]));
            }
        }
        else
        {
            bool needsFirst = positions[0] != 0, needsLast = positions[n - 1] != 1;
            if (needsFirst) { pos.Add(0); col.Add(Unpack(argb[0])); }
            float prev = 0;
            for (int i = 0; i < n; i++)
            {
                float p = i == 0 && !needsFirst ? 0 : Math.Clamp(positions[i], prev, 1f);
                if (float.IsNaN(p)) p = prev;
                pos.Add(prev = p);
                col.Add(Unpack(argb[i]));
            }
            if (needsLast) { pos.Add(1); col.Add(Unpack(argb[n - 1])); }
        }

        // The raster pipeline's gradient context: a constant before the first
        // stop, one linear piece per non-empty interval, a constant after.
        var ts = new List<float> { float.NegativeInfinity };
        var f = new List<Vector4> { Vector4.Zero };
        var b = new List<Vector4> { col[0] };
        for (int i = 0; i + 1 < pos.Count; i++)
        {
            float tl = pos[i], tr = pos[i + 1];
            if (!(tl < tr)) continue;
            var fi = (col[i + 1] - col[i]) / (tr - tl);
            ts.Add(tl);
            f.Add(fi);
            b.Add(col[i] - fi * tl);
        }
        ts.Add(pos[^1]);
        f.Add(Vector4.Zero);
        b.Add(col[^1]);
        _ts = [.. ts];
        _f = [.. f];
        _b = [.. b];
        // Two stops at 0 and 1: one piece, and t clamped to [0, 1] stands in for the constants.
        _twoStop = _ts.Length == 3 && _ts[1] == 0f && _ts[2] == 1f;
    }

    private static Vector4 Unpack(uint argb)
        => new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, (argb >> 24) / 255f);

    public override bool IsOpaque => _degenerate ? (_solid >> 24) == 255 : _opaque;

    /// <summary>The interval boundaries (for tests): −∞, then each piece's start, then the last stop.</summary>
    internal ReadOnlySpan<float> Boundaries => _ts;

    public override void Begin(in Matrix3x2 localToDevice)
    {
        if (_degenerate) return;
        if (!Matrix3x2.Invert(localToDevice, out var inv)) inv = default;
        // Local → gradient space, then compose with device → local.
        Matrix3x2 toUnit;
        if (Kind == GradientKind.Linear)
        {
            // p ↦ dot(p − start, d) / |d|²: t along x.
            var d = End - Start;
            float s = 1f / d.LengthSquared();
            toUnit = new Matrix3x2(d.X * s, -d.Y * s, d.Y * s, d.X * s, 0, 0);
            toUnit.Translation = new Vector2(-Vector2.Dot(Start, d) * s, (Start.X * d.Y - Start.Y * d.X) * s);
        }
        else
        {
            float s = 1f / Radius;
            toUnit = new Matrix3x2(s, 0, 0, s, -Start.X * s, -Start.Y * s);
        }
        var m = inv * toUnit;
        // u = M11·x + M21·y + M31, v = M12·x + M22·y + M32.
        _ax = m.M11; _ay = m.M21; _ac = m.M31;
        _bx = m.M12; _by = m.M22; _bc = m.M32;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override void ShadeSpan(int x, int y, Span<uint> dst)
    {
        if (_degenerate) { dst.Fill(_solid); return; }
        float py = y + 0.5f;
        float rowA = _ay * py + _ac, rowB = _by * py + _bc;
        if (Kind == GradientKind.Linear && _ax == 0f)
        {
            // t does not change along the row (a vertical gradient): one colour.
            dst.Fill(Colour(rowA));
            return;
        }
        int i = 0;
        int w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && dst.Length >= w)
        {
            var lanes = LaneOffsets();
            var ax = new Vector<float>(_ax);
            var ra = new Vector<float>(rowA);
            var bx = new Vector<float>(_bx);
            var rb = new Vector<float>(rowB);
            for (; i <= dst.Length - w; i += w)
            {
                var px = lanes + new Vector<float>(x + i);
                Vector<float> t;
                if (Kind == GradientKind.Linear) t = px * ax + ra;
                else
                {
                    var u = px * ax + ra;
                    var v = px * bx + rb;
                    t = Vector.SquareRoot(u * u + v * v);
                }
                Colour(t).CopyTo(dst.Slice(i, w));
            }
        }
        for (; i < dst.Length; i++)
        {
            float px = x + i + 0.5f;
            float t;
            if (Kind == GradientKind.Linear) t = _ax * px + rowA;
            else
            {
                float u = _ax * px + rowA, v = _bx * px + rowB;
                t = MathF.Sqrt(u * u + v * v);
            }
            dst[i] = Colour(t);
        }
    }

    private static Vector<float>? s_lanes;

    private static Vector<float> LaneOffsets()
    {
        if (s_lanes is { } l) return l;
        Span<float> o = stackalloc float[Vector<float>.Count];
        for (int k = 0; k < o.Length; k++) o[k] = k + 0.5f;
        var v = new Vector<float>(o);
        s_lanes = v;
        return v;
    }

    /// <summary>The premultiplied colour at <paramref name="t"/>.</summary>
    internal uint Colour(float t)
    {
        if (_degenerate) return _solid;
        Vector4 f, b;
        if (_twoStop)
        {
            t = Math.Clamp(t, 0f, 1f);
            if (float.IsNaN(t)) t = 0;
            f = _f[1];
            b = _b[1];
        }
        else
        {
            int k = 0;
            for (int j = 1; j < _ts.Length; j++) if (t >= _ts[j]) k = j;
            f = _f[k];
            b = _b[k];
        }
        var c = f * t + b;
        float a = Math.Clamp(c.W, 0f, 1f);
        float r = Math.Clamp(c.X, 0f, 1f), g = Math.Clamp(c.Y, 0f, 1f), bl = Math.Clamp(c.Z, 0f, 1f);
        if (!_opaque) { r *= a; g *= a; bl *= a; }
        return (ToByte(a) << 24) | (ToByte(r) << 16) | (ToByte(g) << 8) | ToByte(bl);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint ToByte(float v) => (uint)(int)(v * 255f + 0.5f);
    }

    /// <summary>Premultiplied colours for a vector of t, packed <c>0xAARRGGBB</c>.</summary>
    private Vector<uint> Colour(Vector<float> t)
    {
        Vector<float> fr, fg, fb, fa, br, bg, bb, ba;
        if (_twoStop)
        {
            t = Vector.Min(Vector.Max(t, Vector<float>.Zero), Vector<float>.One);
            var f = _f[1];
            var b = _b[1];
            fr = new(f.X); fg = new(f.Y); fb = new(f.Z); fa = new(f.W);
            br = new(b.X); bg = new(b.Y); bb = new(b.Z); ba = new(b.W);
        }
        else
        {
            var f = _f[0];
            var b = _b[0];
            fr = new(f.X); fg = new(f.Y); fb = new(f.Z); fa = new(f.W);
            br = new(b.X); bg = new(b.Y); bb = new(b.Z); ba = new(b.W);
            for (int j = 1; j < _ts.Length; j++)
            {
                var m = Vector.GreaterThanOrEqual(t, new Vector<float>(_ts[j]));
                if (m == Vector<int>.Zero) break;
                f = _f[j];
                b = _b[j];
                fr = Vector.ConditionalSelect(m, new Vector<float>(f.X), fr);
                fg = Vector.ConditionalSelect(m, new Vector<float>(f.Y), fg);
                fb = Vector.ConditionalSelect(m, new Vector<float>(f.Z), fb);
                fa = Vector.ConditionalSelect(m, new Vector<float>(f.W), fa);
                br = Vector.ConditionalSelect(m, new Vector<float>(b.X), br);
                bg = Vector.ConditionalSelect(m, new Vector<float>(b.Y), bg);
                bb = Vector.ConditionalSelect(m, new Vector<float>(b.Z), bb);
                ba = Vector.ConditionalSelect(m, new Vector<float>(b.W), ba);
            }
        }
        var zero = Vector<float>.Zero;
        var one = Vector<float>.One;
        var a = Vector.Min(Vector.Max(fa * t + ba, zero), one);
        var r = Vector.Min(Vector.Max(fr * t + br, zero), one);
        var g = Vector.Min(Vector.Max(fg * t + bg, zero), one);
        var bl = Vector.Min(Vector.Max(fb * t + bb, zero), one);
        if (!_opaque) { r *= a; g *= a; bl *= a; }
        var k = new Vector<float>(255f);
        var h = new Vector<float>(0.5f);
        var ai = Vector.AsVectorUInt32(Vector.ConvertToInt32(a * k + h));
        var ri = Vector.AsVectorUInt32(Vector.ConvertToInt32(r * k + h));
        var gi = Vector.AsVectorUInt32(Vector.ConvertToInt32(g * k + h));
        var bi = Vector.AsVectorUInt32(Vector.ConvertToInt32(bl * k + h));
        return Vector.ShiftLeft(ai, 24) | Vector.ShiftLeft(ri, 16) | Vector.ShiftLeft(gi, 8) | bi;
    }
}
