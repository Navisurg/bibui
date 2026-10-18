namespace Bibui.Draw;

/// <summary>
/// A texture, as the draw layer names it: a value a renderer gave out and knows
/// the meaning of. bibui never sees what is behind it. Usage code creates
/// textures through its renderer (<c>CreateTexture</c>), owns them, and
/// releases them; a <see cref="DrawOpKind.Texture"/> op draws one.
///
/// <para><see cref="Id"/> names a slot in the renderer's table (one past its
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
/// What a texture's pixels are, rad's names. Each draws with a default look the
/// renderer gives it, so any texture drawn plainly shows: R8 grey (r, r, r, 1),
/// A8 white coverage (1, 1, 1, a), RG8 (r, g, 0, 1), RGBA8 and BGRA8 as they are. What reads
/// a texture's own channels (a lookup) reads them raw, before that look.
/// </summary>
public enum TextureFormat : byte
{
    /// <summary>Red, green, blue, alpha; 4 bytes a pixel, straight (not premultiplied) alpha.</summary>
    RGBA8,
    /// <summary>One channel, 1 byte a pixel: a grey image, as a camera's NIR frame.</summary>
    R8,
    /// <summary>Two channels, 2 bytes a pixel: a signed field's two halves, say.</summary>
    RG8,
    /// <summary>Coverage, 1 byte a pixel: a mask.</summary>
    A8,
    /// <summary>Blue, green, red, alpha; 4 bytes a pixel, straight alpha: what many decoders and CPU framebuffers hold.</summary>
    BGRA8,
}

/// <summary>How often a texture's pixels change, which says how a renderer keeps it (rad's resource kinds).</summary>
public enum TextureKind : byte
{
    /// <summary>Made once and never changed: logos, thumbnails, lookup tables. An update is ignored.</summary>
    Static,
    /// <summary>
    /// One texture whose pixels are replaced whole, often: video frames. Its size
    /// and format are fixed; an update overwrites it in place, and the renderer
    /// keeps no copy of the old pixels.
    /// </summary>
    Stream,
}

/// <summary>The sizes a renderer checks a texture's pixels against.</summary>
public static class TexturePixels
{
    /// <summary>Bytes a pixel of <paramref name="format"/>.</summary>
    public static int BytesPerPixel(this TextureFormat format) => format switch
    {
        TextureFormat.RGBA8 or TextureFormat.BGRA8 => 4,
        TextureFormat.RG8 => 2,
        _ => 1,
    };

    /// <summary>
    /// The row stride of <paramref name="pixels"/>: <paramref name="rowBytes"/>, or
    /// rows packed tight when it is 0. Throws when the pixels are too few for a
    /// <paramref name="width"/> × <paramref name="height"/> texture of the format.
    /// </summary>
    public static int RowBytes(TextureFormat format, int width, int height, ReadOnlySpan<byte> pixels, int rowBytes)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"A texture is at least 1×1, not {width}×{height}.");
        int tight = width * format.BytesPerPixel();
        int stride = rowBytes == 0 ? tight : rowBytes;
        if (stride < tight) throw new ArgumentException($"{rowBytes} bytes a row is less than a {width}-pixel {format} row.", nameof(rowBytes));
        if (pixels.Length < (long)stride * (height - 1) + tight)
            throw new ArgumentException($"{pixels.Length} bytes do not hold a {width}×{height} {format} texture.", nameof(pixels));
        return stride;
    }
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

    /// <summary>Bumped when a texture is put here, replaced or changed in place: what damage tracking compares.</summary>
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

    /// <summary>Bumps the content version of a texture changed in place, so damage tracking sees it. False for a stale handle.</summary>
    public bool Touch(TextureHandle handle)
    {
        if (Node(handle) is not { } node) return false;
        node.Version++;
        return true;
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
