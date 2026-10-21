using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace Bibui.Core;

/// <summary>
/// Names one animated value: a widget's id and which of its properties is
/// moving. Two widgets that share an id share the animation, which is the same
/// consequence a duplicate id already has everywhere else.
/// </summary>
public readonly record struct AnimKey(UInt128 Hash)
{
    public static AnimKey For(string? id, string property) =>
        new(XxHash128.HashToUInt128(
            MemoryMarshal.AsBytes((id ?? "").AsSpan()),
            (long)XxHash64.HashToUInt64(MemoryMarshal.AsBytes(property.AsSpan()))));
}
