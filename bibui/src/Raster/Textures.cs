namespace Bibui.Raster;

/// <summary>
/// A texture, as the draw layer names it: a value the raster backend gave out
/// and knows the meaning of. bibui never sees what is behind it. Usage code
/// creates textures through its backend (<c>CreateTexture</c>), owns them, and
/// releases them; <see cref="IRasterCanvas.DrawTexture"/> draws one.
///
/// <para><see cref="Id"/> names a slot in the backend's table (one past its
/// index, so zero is none) and <see cref="Generation"/> the texture that slot
/// held when the handle was made. <see cref="None"/> draws nothing, and so does
/// a handle whose texture was released: neither is an error.</para>
/// </summary>
public readonly record struct TextureHandle(ulong Id, ulong Generation)
{
    public static TextureHandle None => default;

    public bool IsNone => Id == 0;
}

/// <summary>
/// A backend's textures by <see cref="TextureHandle"/>: slots reused through a
/// free list, each with a generation, so a released handle never finds the
/// texture that took its slot. Each texture also has a content version,
/// bumped when it is replaced, for a backend that compares frames.
///
/// <para>Confined to the rendering thread, like the backend that owns it.</para>
/// </summary>
public sealed class TextureTable<T> where T : class
{
    private T?[] _items = new T?[16];
    private ulong[] _generations = new ulong[16];
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
        return new((ulong)slot + 1, _generations[slot]);
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
        if (handle.Id == 0 || handle.Id > (ulong)_used) return -1;
        int slot = (int)(handle.Id - 1);
        return _items[slot] is not null && _generations[slot] == handle.Generation ? slot : -1;
    }
}
