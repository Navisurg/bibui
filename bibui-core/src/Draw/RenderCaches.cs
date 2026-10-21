namespace Bibui.Core.Draw;

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
/// Per-runtime caches for what recording a frame would otherwise re-create on
/// every draw call. Owned by the runtime (one per runtime, alongside its Fonts)
/// and handed to each frame's DrawingContext; EndFrame runs once per recorded
/// frame. What draws the list keeps its own native caches.
///
/// Caching is safe for parity because every key encodes all inputs that affect
/// the value — identical draw commands produce identical pixels whether the
/// paint object is fresh or reused. That property is enforced by the verifier
/// spec suite.
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

    // Geometry that repeats across frames, as bibui's own paths: a draw list
    // names these, and a renderer keeps the native path it builds from each.
    // Paints are not here: a paint is a value the op carries, and a renderer
    // resolves it to whatever it keeps. Text measurement, metrics and line breaks are
    // Fonts', which keeps its own.
    public readonly ResourceCache<CacheKey, VectorPath> Paths;

    public RenderCaches()
    {
        Paths = Create<CacheKey, VectorPath>();
        Anims = Create<AnimKey, AnimCell>(keepFrames: 1);
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
    }
}
