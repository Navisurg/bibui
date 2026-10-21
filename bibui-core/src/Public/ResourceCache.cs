using System.Runtime.InteropServices;

namespace Bibui.Core;

/// <summary>
/// A content-addressed, touch-evicted cache for render resources (paints,
/// measured widths, truncated strings). The key encodes everything that
/// defines the value, so there is no invalidation: changed inputs are simply
/// a different key, and entries idle for more than <c>keepFrames</c> frames
/// are disposed by <see cref="EndFrame"/>.
///
/// The producer is supplied per get as a <c>static</c> lambda plus a params
/// value, so the hit path allocates nothing (no closure); a miss builds the
/// value synchronously inline — render resources are needed mid-draw, so
/// there is no async variant here.
///
/// Not thread-safe: frames are single-flighted by the runtime's render lock,
/// and each owner holds its own instance. A caller outside that lock (a host
/// app keeping its own cache) must serialize its own access.
/// </summary>
public sealed class ResourceCache<TKey, TValue> : IDisposable where TKey : notnull
{
    /// <summary>A live value and the frame it was last asked for.</summary>
    public struct Entry { public TValue Value; public ulong Touched; }

    /// <summary>
    /// The live entries. Public so a caller can sweep them — animation commits
    /// its double-buffered values at the end of a frame, and needs Touched to
    /// tell what was actually drawn from what is merely still cached.
    /// </summary>
    public readonly Dictionary<TKey, Entry> Entries = new();
    private readonly ulong _keepFrames;
    private ulong _frame;

    /// <summary>The frame entries touched now are stamped with.</summary>
    public ulong Frame => _frame;
    private List<TKey>? _dead; // eviction scratch, reused across frames

    public ResourceCache(ulong keepFrames = 60) => _keepFrames = keepFrames;

    public int Count => Entries.Count;

    /// <summary>
    /// The value for <paramref name="key"/> — built by <paramref name="produce"/>
    /// on a miss, the cached instance on a hit; always touched for eviction.
    /// </summary>
    public TValue FromKey<TParams>(TKey key, TParams p, Func<TParams, TValue> produce)
    {
        ref var e = ref CollectionsMarshal.GetValueRefOrAddDefault(Entries, key, out bool exists);
        if (!exists) e.Value = produce(p);
        e.Touched = _frame;
        return e.Value;
    }

    /// <summary>
    /// The value for <paramref name="key"/> if there is one, touched for
    /// eviction. With <see cref="Add"/>, the form for a producer that needs a
    /// span, which a lambda's parameters cannot carry.
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        ref var e = ref CollectionsMarshal.GetValueRefOrNullRef(Entries, key);
        if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref e)) { value = default!; return false; }
        e.Touched = _frame;
        value = e.Value;
        return true;
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, touched now, and returns it.</summary>
    public TValue Add(TKey key, TValue value)
    {
        Entries[key] = new Entry { Value = value, Touched = _frame };
        return value;
    }

    /// <summary>
    /// Evict entries idle for more than keepFrames frames, then advance.
    /// Checking before advancing means an entry touched this frame has age 0.
    /// </summary>
    public void EndFrame()
    {
        _dead ??= new List<TKey>();
        foreach (var (key, e) in Entries)
            if (_frame - e.Touched > _keepFrames)
                _dead.Add(key);
        foreach (var key in _dead)
        {
            if (Entries[key].Value is IDisposable d) d.Dispose();
            Entries.Remove(key);
        }
        _dead.Clear();
        _frame++;
    }

    public void Dispose()
    {
        foreach (var e in Entries.Values)
            if (e.Value is IDisposable d) d.Dispose();
        Entries.Clear();
    }
}
