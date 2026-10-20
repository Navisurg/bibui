using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bibui.Core;

namespace Bibui.Raster.Managed.Backend.Damage;

// The running hash a draw's signature is folded into. 64 bits, because a
// collision is a stale pixel nobody asked for: a changed draw that hashes like
// the draw it replaced is taken as unchanged. Only *changed* draws can collide
// (an unchanged one hashes the same inputs), so at a few changed draws a frame
// the odds are about 2^-64 per draw per frame.
//
// Floats go in by bit pattern: -0 and 0 are different draws as far as anyone
// can prove, and a signature must be an exact function of its inputs.
internal static class Hash64
{
    internal const ulong Seed = 0x9E3779B97F4A7C15UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong h, ulong v)
    {
        h ^= v * 0xC2B2AE3D27D4EB4FUL;
        h = BitOperations.RotateLeft(h, 31);
        return h * 0x9E3779B185EBCA87UL + 0x27D4EB2F165667C5UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong h, float v) => Mix(h, (ulong)BitConverter.SingleToUInt32Bits(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong h, float a, float b) =>
        Mix(h, ((ulong)BitConverter.SingleToUInt32Bits(a) << 32) | BitConverter.SingleToUInt32Bits(b));

    internal static ulong Mix(ulong h, in RectF r) => Mix(Mix(h, r.Left, r.Top), r.Right, r.Bottom);

    internal static ulong Mix(ulong h, in Matrix3x2 m) =>
        Mix(Mix(Mix(h, m.M11, m.M12), m.M21, m.M22), m.M31, m.M32);

    internal static ulong Mix(ulong h, string? s) =>
        s is null ? Mix(h, 0x5EEDUL) : Mix(Mix(h, (ulong)s.Length), Bytes(MemoryMarshal.AsBytes(s.AsSpan())));

    internal static ulong Mix<T>(ulong h, ReadOnlySpan<T> span) where T : unmanaged =>
        Mix(Mix(h, (ulong)span.Length), Bytes(MemoryMarshal.AsBytes(span)));

    internal static ulong Bytes(ReadOnlySpan<byte> bytes) => XxHash3.HashToUInt64(bytes);
}
