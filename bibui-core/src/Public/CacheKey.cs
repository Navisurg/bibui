using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace Bibui.Core;

/// <summary>
/// Content hash of the *resolved* parameters that determine a render resource —
/// colour after hover and theme resolution, stroke width after scale, sigma
/// rather than blur radius. Declared style is the input; this is the output.
///
/// One key type across the renderer's path cache and a raster backend's own caches.
/// The tables are typed by value, so a paint and a path can never collide with
/// each other.
/// </summary>
public readonly record struct CacheKey(UInt128 Hash)
{
    /// <param name="sub">
    /// A sub-object this one carries (a shader on a paint, a filter on a paint).
    /// Folded into the seed so the composite is addressed without owning it twice.
    /// </param>
    public static CacheKey Of(scoped ReadOnlySpan<int> parts, CacheKey sub = default)
    {
        var hash = XxHash128.HashToUInt128(MemoryMarshal.AsBytes(parts));
        if (sub == default)
        {
            return new(hash);
        }

        // Both 128-bit values are re-hashed together rather than folded through
        // xxhash's seed: the seed is 64 bits, so a child passed that way would
        // contribute only half its identity and halve the collision margin.
        Span<int> combined = stackalloc int[8];
        Write(hash, combined);
        Write(sub.Hash, combined[4..]);
        return new(XxHash128.HashToUInt128(MemoryMarshal.AsBytes(combined)));
    }

    /// <summary>All 128 bits of <paramref name="h"/> as four ints.</summary>
    private static void Write(UInt128 h, Span<int> dst)
    {
        dst[0] = (int)(uint)(h >> 96);
        dst[1] = (int)(uint)(h >> 64);
        dst[2] = (int)(uint)(h >> 32);
        dst[3] = (int)(uint)h;
    }

    /// <summary>A string parameter (icon name, svg path data) as a sub-key.</summary>
    public static CacheKey OfString(string? s) =>
        new(XxHash128.HashToUInt128(MemoryMarshal.AsBytes((s ?? "").AsSpan())));

    // Floats go in by bit pattern: a key must be an exact function of its
    // inputs, and -0.0 == 0.0 is not how identity should behave.
    public static int Bits(float v) => BitConverter.SingleToInt32Bits(v);

    /// <summary>Every field of a PaintSpec, including its sub-object keys.</summary>
    public static CacheKey OfSpec(
        in PaintSpec s, CacheKey shader, CacheKey mask, CacheKey imageFilter, CacheKey pathEffect)
    {
        Span<int> parts = stackalloc int[7 + 4 * 4];
        parts[0] = (int)s.Color.Argb;
        parts[1] = (int)s.Style;
        parts[2] = Bits(s.StrokeWidth);
        parts[3] = (int)s.StrokeCap;
        parts[4] = (int)s.StrokeJoin;
        parts[5] = s.IsAntialias ? 1 : 0;
        parts[6] = (int)s.FilterQuality;
        Write(shader.Hash, parts[7..]);
        Write(mask.Hash, parts[11..]);
        Write(imageFilter.Hash, parts[15..]);
        Write(pathEffect.Hash, parts[19..]);
        return Of(parts);
    }

    /// <summary>A gradient's key: its kind (0 linear, 1 radial), ends or centre and radius, colours and stops.</summary>
    public static CacheKey OfGradient(
        int kind, System.Numerics.Vector2 a, System.Numerics.Vector2 b, float radius,
        scoped ReadOnlySpan<Rgba> colors, scoped ReadOnlySpan<float> stops)
    {
        int n = 6 + colors.Length + stops.Length;
        Span<int> parts = n <= 64 ? stackalloc int[n] : new int[n];
        parts[0] = kind;
        parts[1] = Bits(a.X); parts[2] = Bits(a.Y);
        parts[3] = Bits(b.X); parts[4] = Bits(b.Y);
        parts[5] = Bits(radius);
        for (int i = 0; i < colors.Length; i++) parts[6 + i] = (int)colors[i].Argb;
        for (int i = 0; i < stops.Length; i++) parts[6 + colors.Length + i] = Bits(stops[i]);
        return Of(parts);
    }
}
