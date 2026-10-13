namespace Bibui.Platform.Wayland;

/// <summary>Makes and frees the buffers a <see cref="BufferPool{TBuffer}"/> hands out.</summary>
internal interface IBufferAllocator<TBuffer> where TBuffer : class
{
    TBuffer Allocate(int width, int height);
    void Destroy(TBuffer buffer);
}

/// <summary>
/// The buffers a surface is presented from, and which of them the compositor
/// still holds.
///
/// <para>A buffer is busy from the commit that attaches it until the
/// compositor's <c>wl_buffer.release</c>; only a free one may be drawn into.
/// Up to <see cref="Depth"/> buffers of the current size exist (double or
/// triple buffering). A resize retires the old ones: a free one is destroyed
/// at once, a busy one when the compositor releases it, since destroying a
/// buffer it may still be reading is undefined.</para>
///
/// <para>Pure bookkeeping over an allocator, so it is tested without a
/// compositor. Not thread-safe: everything happens on the loop thread.</para>
/// </summary>
internal sealed class BufferPool<TBuffer>(IBufferAllocator<TBuffer> allocator, int depth, int maxExtra = 2)
    where TBuffer : class
{
    private sealed class Slot(TBuffer buffer, int width, int height)
    {
        public readonly TBuffer Buffer = buffer;
        public readonly int Width = width, Height = height;
        public bool Busy, Retired;
    }

    private readonly List<Slot> _slots = [];
    private int _width, _height;

    /// <summary>Buffers of the current size kept in rotation: 2 is double buffering, 3 triple.</summary>
    public int Depth { get; } = depth >= 1 ? depth : throw new ArgumentOutOfRangeException(nameof(depth));

    /// <summary>How many more than <see cref="Depth"/> may exist when the compositor holds them all for too long.</summary>
    public int MaxExtra { get; } = maxExtra;

    /// <summary>Every buffer alive, including retired ones the compositor has yet to release.</summary>
    public int Count => _slots.Count;

    public int BusyCount => _slots.Count(s => s.Busy);

    /// <summary>Retired buffers still waiting for their release.</summary>
    public int RetiredCount => _slots.Count(s => s.Retired);

    /// <summary>
    /// A free buffer of <paramref name="width"/> × <paramref name="height"/>,
    /// allocating one while fewer than <see cref="Depth"/> exist at that size
    /// (or, with <paramref name="allowExtra"/>, fewer than Depth + MaxExtra), or
    /// null when every one is busy.
    /// </summary>
    public TBuffer? TryAcquire(int width, int height, bool allowExtra = false)
    {
        if (width != _width || height != _height) Resize(width, height);

        foreach (var s in _slots)
            if (!s.Busy && !s.Retired) return s.Buffer;

        int current = _slots.Count(s => !s.Retired);
        if (current < Depth || (allowExtra && current < Depth + MaxExtra))
        {
            var slot = new Slot(allocator.Allocate(width, height), width, height);
            _slots.Add(slot);
            return slot.Buffer;
        }
        return null;
    }

    /// <summary>The buffer was attached and committed: the compositor holds it until it releases it.</summary>
    public void Submitted(TBuffer buffer) => Find(buffer).Busy = true;

    /// <summary>
    /// The compositor released <paramref name="buffer"/>. Returns false for a
    /// buffer this pool does not hold (already destroyed), which is not an error:
    /// a release can race the destroy.
    /// </summary>
    public bool Released(TBuffer buffer)
    {
        var i = _slots.FindIndex(s => ReferenceEquals(s.Buffer, buffer));
        if (i < 0) return false;
        var s = _slots[i];
        s.Busy = false;
        if (s.Retired)
        {
            _slots.RemoveAt(i);
            allocator.Destroy(s.Buffer);
        }
        return true;
    }

    /// <summary>A buffer by predicate — how a release event, which names the protocol object, finds its buffer.</summary>
    public TBuffer? Find(Func<TBuffer, bool> match)
    {
        foreach (var s in _slots) if (match(s.Buffer)) return s.Buffer;
        return null;
    }

    /// <summary>Destroys every buffer, busy or not: the surface is going away.</summary>
    public void Clear()
    {
        foreach (var s in _slots) allocator.Destroy(s.Buffer);
        _slots.Clear();
        _width = _height = 0;
    }

    private void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            var s = _slots[i];
            if (s.Width == width && s.Height == height) { s.Retired = false; continue; }
            if (s.Busy) { s.Retired = true; continue; }
            _slots.RemoveAt(i);
            allocator.Destroy(s.Buffer);
        }
    }

    private Slot Find(TBuffer buffer) =>
        _slots.Find(s => ReferenceEquals(s.Buffer, buffer))
        ?? throw new InvalidOperationException("buffer is not in this pool");
}
