using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace Bibui.Core;

/// <summary>
/// A greedy word-wrap layout — which chars land on which line — for one
/// (text, maxWidth, style, scale), addressed by a 128-bit content hash instead
/// of by the text itself.
///
/// The hash is the better key here, not merely a cheaper one. The text is the
/// caller's memory, which changes under an edit, so a key holding it would have
/// to copy it, and would then compare by value on every lookup — a second full
/// pass over the text on top of the hash the dictionary already computes.
/// Hashing pays one pass and retains nothing but 16 bytes. Deterministic,
/// unlike string.GetHashCode, which is seeded per process and so could not be
/// asserted on in a test. A collision returns the wrong lines, so the width is
/// the safety margin: 128 bits puts the birthday bound around 2^64 distinct
/// strings, against the thousands a screenful of UI produces.
///
/// Scale is a field of its own rather than being folded into the size, because
/// the wrap decision is `physicalWidth / scale > maxWidth` — a *logical*
/// comparison against a physical measurement. Two different (size, scale) pairs
/// with the same product therefore break lines differently, and a key carrying
/// only the product would hand one of them the other's answer.
/// </summary>
internal readonly record struct WrapKey(UInt128 Hash)
{
    public static WrapKey For(ReadOnlySpan<char> text, float maxWidth, ulong style, float scale, TextWrap wrap)
        => new(XxHash128.HashToUInt128(MemoryMarshal.AsBytes(text), LayoutSeed(maxWidth, style, scale, wrap)));

    // The style's key (face and physical size), and float *bit patterns*, not the
    // floats themselves: a key has to be an exact function of its inputs, and
    // -0.0 and NaN do not compare the way that needs.
    private static long LayoutSeed(float maxWidth, ulong style, float scale, TextWrap wrap)
    {
        Span<long> bits =
        [
            (long)style,
            BitConverter.SingleToInt32Bits(maxWidth),
            BitConverter.SingleToInt32Bits(scale),
            (int)wrap,
        ];
        return (long)XxHash64.HashToUInt64(MemoryMarshal.AsBytes(bits));
    }
}
