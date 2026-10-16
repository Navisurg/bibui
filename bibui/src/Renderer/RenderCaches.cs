using System.IO.Hashing;
using System.Runtime.InteropServices;
using Bibui.Raster;

namespace Bibui.Renderer;

/// <summary>
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

/// <summary>
/// Where an animated value is, where it is heading, and where it goes next. A
/// class, not a struct, because the cache hands values back by value and this
/// one is written in place.
///
/// Double-buffered on purpose. Current is read but never written during a
/// frame, so a mechanism computing Next from it is idempotent — asked twice in
/// one frame it writes the same answer twice. A value really can be asked
/// twice: layout and paint share geometry helpers like ButtonContentWidth,
/// whose whole job is to hand both of them the same number, and an
/// accumulating "Current += …" would hand them different ones.
///
/// Target and Epsilon are here so the end-of-frame sweep can tell whether
/// anything is still moving, rather than each mechanism reporting it. A ramp
/// has no destination, so Sawtooth parks PositiveInfinity in Target: the
/// ordinary predicate then answers "still going" forever, with no second field
/// meaning "never done".
/// </summary>
internal sealed class AnimCell
{
    public float Current;
    public float Next;

    public float Target;
    public float Epsilon;
}

/// <summary>
/// Per-renderer caches for render resources and derived values that are otherwise
/// re-created on every draw call. Owned by the renderer (one per BibuiRenderer,
/// alongside its Fonts) and handed to each
/// frame's DrawingContext; EndFrame runs once per painted frame, and ticks the
/// raster backend's own caches with it.
///
/// Caching is safe for parity because every key encodes all inputs that affect
/// the value — identical draw commands produce identical pixels whether the
/// paint object is fresh or reused. That property is enforced by the byte-exact
/// Render/RenderTo parity test and the verifier spec suite.
/// </summary>
/// Caches are constructed *here* and nowhere else, so the frame tick and the
/// disposal are derived from that one list rather than restated per cache. A
/// cache built somewhere else is one whose eviction someone has to remember to
/// call — and a forgotten tick does not fail, it just grows, which is the
/// failure mode least likely to be noticed.
internal sealed class RenderCaches : IDisposable
{
    private readonly List<Action> _endFrames = [];
    private readonly List<IDisposable> _disposables = [];

    /// <summary>The raster backend these caches hold resources of. Owned: disposed with them.</summary>
    public readonly IRasterBackend Backend;

    // Geometry that repeats across frames, as bibui's own paths: a draw list
    // names these, and the replay keeps the native path it builds from each.
    // Paints are not here: a paint is a value (RasterPaint) the backend resolves
    // to whatever it keeps. Text measurement, metrics and line breaks are
    // Fonts', which keeps its own.
    public readonly ResourceCache<CacheKey, VectorPath> Paths;

    public RenderCaches(IRasterBackend backend)
    {
        Backend = backend;
        Paths = Create<CacheKey, VectorPath>();
        Anims = Create<AnimKey, AnimCell>(keepFrames: 1);
        _endFrames.Add(backend.EndFrame);
    }

    private readonly VectorPath _scratchPath = new();

    /// <summary>
    /// A path for geometry drawn once and dropped — a chart series whose points
    /// are the content, so no key could name it. Rewound before it is handed out;
    /// valid until the next call, so not for anything that nests. Drawing it
    /// copies it into the draw list.
    /// </summary>
    public VectorPath ScratchPath()
    {
        _scratchPath.Rewind();
        return _scratchPath;
    }

    /// <summary>
    /// A cache whose eviction this registry drives. Held by whoever asked for it,
    /// while the frame clock stays in one place.
    /// </summary>
    public ResourceCache<TKey, TValue> Create<TKey, TValue>(ulong keepFrames = 60)
        where TKey : notnull
    {
        var cache = new ResourceCache<TKey, TValue>(keepFrames);
        _endFrames.Add(cache.EndFrame);
        _disposables.Add(cache);
        return cache;
    }


    // The store behind DrawContext.Animate / Sawtooth. A cache like any other:
    // addressed by key, built on a miss, evicted once nobody asks. The stepping
    // itself lives with the drawing, next to the calls that ask for a value.
    public readonly ResourceCache<AnimKey, AnimCell> Anims;

    public void EndFrame()
    {
        foreach (var endFrame in _endFrames)
        {
            endFrame();
        }
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
        Backend.Dispose();
    }
}
