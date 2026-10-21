using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace Bibui.Core;

/// <summary>
/// Font metrics for one (physical size, family, weight). Colour is not in the
/// key because it cannot move a baseline, the same reason measurement asks for
/// its paint in black.
/// </summary>
internal readonly record struct MetricsKey(UInt128 Hash)
{
    public static MetricsKey For(float sizePhysical, string? family, int weight)
        => new(XxHash128.HashToUInt128(
            MemoryMarshal.AsBytes((family ?? "").AsSpan()), Seed(sizePhysical, weight)));

    // Float bit patterns, not the floats: a key has to be an exact function of
    // its inputs, and -0.0 and NaN do not compare the way that needs.
    private static long Seed(float sizePhysical, int weight)
    {
        Span<int> bits = [BitConverter.SingleToInt32Bits(sizePhysical), weight];
        return (long)XxHash64.HashToUInt64(MemoryMarshal.AsBytes(bits));
    }

    /// <summary>The whole font as one seed, for a key that hashes text under it.</summary>
    internal static long Seed(float sizePhysical, string? family, int weight)
        => (long)XxHash64.HashToUInt64(MemoryMarshal.AsBytes((family ?? "").AsSpan()), Seed(sizePhysical, weight));
}

/// <summary>
/// Measured text width for one (text, physical size, family, weight),
/// hash-keyed for the reason <see cref="RunsKey"/> gives.
/// </summary>
internal readonly record struct MeasureKey(UInt128 Hash)
{
    public static MeasureKey For(ReadOnlySpan<char> text, float sizePhysical, string? family, int weight)
        => new(XxHash128.HashToUInt128(MemoryMarshal.AsBytes(text), MetricsKey.Seed(sizePhysical, family, weight)));
}

/// <summary>
/// Typeface runs for one (text, family, weight), addressed by a 128-bit
/// content hash instead of by the text itself.
///
/// The hash is the better key here, not merely a cheaper one. The text is the
/// caller's memory, which changes under an edit, so a key holding it would have
/// to copy it, and would then compare by value on every lookup — a second full
/// pass over the text on top of the hash the dictionary already computes.
/// Hashing pays one pass and retains nothing but 16 bytes.
///
/// A collision returns the wrong runs, so the width is the safety margin: 128
/// bits puts the birthday bound around 2^64 distinct strings, against the
/// thousands a screenful of UI actually produces.
/// </summary>
internal readonly record struct RunsKey(UInt128 Hash)
{
    /// <summary>
    /// XxHash128 over the bytes of the text's chars, seeded by the font. The font is
    /// folded into the seed rather than concatenated into the hashed bytes so
    /// there is no intermediate buffer to build and no ambiguity to guard
    /// against — ("ab", "c") and ("a", "bc") land on different seeds, not on
    /// the same byte sequence. Deterministic, unlike string.GetHashCode, which
    /// is seeded per process and so could not be asserted on in a test.
    /// </summary>
    public static RunsKey For(ReadOnlySpan<char> text, string? family, int weight) =>
        new(XxHash128.HashToUInt128(MemoryMarshal.AsBytes(text), FontSeed(family, weight)));

    // (family, weight) collapses to one 64-bit seed. There are a handful of
    // these per app against thousands of strings, so the cost sits on the side
    // that barely varies.
    private static long FontSeed(string? family, int weight) =>
        (long)XxHash64.HashToUInt64(MemoryMarshal.AsBytes((family ?? "").AsSpan()), weight);
}

/// <summary>
/// One run of text shaped in one face at one physical size: XxHash128 over the
/// chars, seeded by the face and the size, for the reasons <see cref="RunsKey"/>
/// gives.
/// </summary>
internal readonly record struct ShapeKey(UInt128 Hash)
{
    public static ShapeKey For(ReadOnlySpan<char> text, FontFace face, float sizePhysical) =>
        new(XxHash128.HashToUInt128(MemoryMarshal.AsBytes(text),
            (long)face.Id << 32 | (uint)BitConverter.SingleToInt32Bits(sizePhysical)));
}

/// <summary>
/// A greedy word-wrap layout — which chars land on which line — for one
/// (text, maxWidth, font, scale). Hash-keyed for the same reason as
/// <see cref="RunsKey"/>.
///
/// Scale is a field of its own rather than being folded into the size, because
/// the wrap decision is `physicalWidth / scale > maxWidth` — a *logical*
/// comparison against a physical measurement. Two different (size, scale) pairs
/// with the same product therefore break lines differently, and a key carrying
/// only the product would hand one of them the other's answer.
/// </summary>
internal readonly record struct WrapKey(UInt128 Hash)
{
    public static WrapKey For(
        ReadOnlySpan<char> text, float maxWidth, float sizePhysical, float scale, string? family, int weight,
        TextWrap wrap)
        => new(XxHash128.HashToUInt128(
            MemoryMarshal.AsBytes(text),
            LayoutSeed(maxWidth, sizePhysical, scale, family, weight, wrap)));

    // Float *bit patterns*, not the floats themselves: a key has to be an exact
    // function of its inputs, and -0.0 and NaN do not compare the way that needs.
    private static long LayoutSeed(
        float maxWidth, float sizePhysical, float scale, string? family, int weight, TextWrap wrap)
    {
        Span<int> bits =
        [
            BitConverter.SingleToInt32Bits(maxWidth),
            BitConverter.SingleToInt32Bits(sizePhysical),
            BitConverter.SingleToInt32Bits(scale),
            weight,
            (int)wrap,
        ];
        long seed = (long)XxHash64.HashToUInt64(MemoryMarshal.AsBytes(bits));
        return (long)XxHash64.HashToUInt64(MemoryMarshal.AsBytes((family ?? "").AsSpan()), seed);
    }
}
