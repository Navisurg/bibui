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
/// One texture's place in a <see cref="TextureTable{T}"/>: the backend's resource,
/// and what decides which handles still name it. A node is made once per slot and
/// reused after its texture is released, on the live list or the free list,
/// never both, so one <see cref="Next"/> serves whichever it is on.
/// </summary>
public sealed class TextureNode<T> where T : class
{
    internal TextureNode(ulong id) => Id = id;

    /// <summary>The slot, one past its index: every handle to this node carries it.</summary>
    public readonly ulong Id;

    /// <summary>The backend's texture; null while the node is free.</summary>
    public T? Item;

    /// <summary>Bumped when the texture is released, so an older handle stops matching.</summary>
    public ulong Generation;

    /// <summary>Bumped when a texture is put here or replaced: what damage tracking compares.</summary>
    public uint Version;

    /// <summary>The next live node, or the next free one while this node is free.</summary>
    public TextureNode<T>? Next;

    /// <summary>The previous live node: release unlinks without walking. Unused while free.</summary>
    public TextureNode<T>? Prev;
}

/// <summary>
/// A backend's textures by <see cref="TextureHandle"/>. A handle is a plain
/// value, so a lookup goes through an array of nodes indexed by its
/// <see cref="TextureHandle.Id"/>; the nodes themselves are on two intrusive
/// lists, the live ones from <see cref="FirstLive"/> and the free ones waiting
/// to be reused. A released node's generation moves on, so a released handle
/// never finds the texture that took its slot.
///
/// <para>Confined to the rendering thread, like the backend that owns it.</para>
/// </summary>
public sealed class TextureTable<T> where T : class
{
    private TextureNode<T>[] _nodes = new TextureNode<T>[16];
    private int _count;
    private TextureNode<T>? _firstFree;

    /// <summary>The first live texture's node; the rest follow by <see cref="TextureNode{T}.Next"/>.</summary>
    public TextureNode<T>? FirstLive { get; private set; }

    /// <summary>Stores <paramref name="texture"/> and returns its handle.</summary>
    public TextureHandle Add(T texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var node = _firstFree;
        if (node is not null) _firstFree = node.Next;
        else
        {
            if (_count == _nodes.Length) Array.Resize(ref _nodes, _count * 2);
            node = _nodes[_count] = new TextureNode<T>((ulong)_count + 1);
            _count++;
        }
        node.Item = texture;
        node.Version++;
        node.Prev = null;
        node.Next = FirstLive;
        if (FirstLive is not null) FirstLive.Prev = node;
        FirstLive = node;
        return new(node.Id, node.Generation);
    }

    /// <summary>The texture behind <paramref name="handle"/>, or null for none or a released one.</summary>
    public T? Get(TextureHandle handle) => Node(handle)?.Item;

    /// <summary>Its content version: different after every <see cref="Replace"/>. Zero for none.</summary>
    public uint Version(TextureHandle handle) => Node(handle)?.Version ?? 0;

    /// <summary>Puts <paramref name="texture"/> behind <paramref name="handle"/> and returns the one it replaced, or null when the handle is stale.</summary>
    public T? Replace(TextureHandle handle, T texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (Node(handle) is not { } node) return null;
        var old = node.Item;
        node.Item = texture;
        node.Version++;
        return old;
    }

    /// <summary>Releases <paramref name="handle"/> and returns its texture to dispose, or null when it was none or already released.</summary>
    public T? Remove(TextureHandle handle)
    {
        if (Node(handle) is not { } node) return null;
        if (node.Prev is not null) node.Prev.Next = node.Next; else FirstLive = node.Next;
        if (node.Next is not null) node.Next.Prev = node.Prev;
        var old = node.Item;
        node.Item = null;
        node.Generation++;
        node.Prev = null;
        node.Next = _firstFree;
        _firstFree = node;
        return old;
    }

    private TextureNode<T>? Node(TextureHandle handle)
    {
        if (handle.Id == 0 || handle.Id > (ulong)_count) return null;
        var node = _nodes[handle.Id - 1];
        return node.Item is not null && node.Generation == handle.Generation ? node : null;
    }
}
