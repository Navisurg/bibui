namespace Bibui.Raster;

/// <summary>
/// A texture, as the draw layer names it: a value the raster backend gave out
/// and knows the meaning of. bibui never sees what is behind it. Usage code
/// creates textures through its backend (<c>CreateTexture</c>), owns them, and
/// releases them; <see cref="IRasterCanvas.DrawTexture"/> draws one.
///
/// <para><see cref="None"/> (zero) draws nothing, and so does a handle whose
/// texture was released: neither is an error.</para>
/// </summary>
public readonly record struct TextureHandle(ulong Value)
{
    public static TextureHandle None => default;

    public bool IsNone => Value == 0;
}

/// <summary>
/// A backend's textures by <see cref="TextureHandle"/>: slots reused through a
/// free list, each with a generation, so a released handle never finds the
/// texture that took its slot. A handle is the slot plus one in its low 32 bits
/// and the generation in its high 32. Each texture also has a content version,
/// bumped when it is replaced, for a backend that compares frames.
///
/// <para>Confined to the rendering thread, like the backend that owns it.</para>
/// </summary>
public sealed class TextureTable<T> where T : class
{
    private T?[] _items = new T?[16];
    private uint[] _generations = new uint[16];
    private uint[] _versions = new uint[16];
    private int[] _free = new int[16];
    private int _freeCount;
    private int _used;

    /// <summary>Stores <paramref name="texture"/> and returns its handle.</summary>
    public TextureHandle Add(T texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        int slot;
        if (_freeCount > 0) slot = _free[--_freeCount];
        else
        {
            if (_used == _items.Length)
            {
                Array.Resize(ref _items, _used * 2);
                Array.Resize(ref _generations, _used * 2);
                Array.Resize(ref _versions, _used * 2);
            }
            slot = _used++;
        }
        _items[slot] = texture;
        _versions[slot]++;
        return new(((ulong)_generations[slot] << 32) | (uint)(slot + 1));
    }

    /// <summary>The texture behind <paramref name="handle"/>, or null for none or a released one.</summary>
    public T? Get(TextureHandle handle) => Slot(handle) is int slot and >= 0 ? _items[slot] : null;

    /// <summary>Its content version: different after every <see cref="Replace"/>. Zero for none.</summary>
    public uint Version(TextureHandle handle) => Slot(handle) is int slot and >= 0 ? _versions[slot] : 0;

    /// <summary>Puts <paramref name="texture"/> behind <paramref name="handle"/> and returns the one it replaced, or null when the handle is stale.</summary>
    public T? Replace(TextureHandle handle, T texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (Slot(handle) is not (int slot and >= 0)) return null;
        var old = _items[slot];
        _items[slot] = texture;
        _versions[slot]++;
        return old;
    }

    /// <summary>Releases <paramref name="handle"/> and returns its texture to dispose, or null when it was none or already released.</summary>
    public T? Remove(TextureHandle handle)
    {
        if (Slot(handle) is not (int slot and >= 0)) return null;
        var old = _items[slot];
        _items[slot] = null;
        _generations[slot]++;
        if (_freeCount == _free.Length) Array.Resize(ref _free, _free.Length * 2);
        _free[_freeCount++] = slot;
        return old;
    }

    /// <summary>Every live texture, for a backend that disposes them with itself.</summary>
    public IEnumerable<T> Live()
    {
        for (int i = 0; i < _used; i++)
            if (_items[i] is { } item) yield return item;
    }

    private int Slot(TextureHandle handle)
    {
        int slot = (int)(uint)handle.Value - 1;
        if (slot < 0 || slot >= _used || _items[slot] is null) return -1;
        return _generations[slot] == (uint)(handle.Value >> 32) ? slot : -1;
    }
}
