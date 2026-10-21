using FtFont = Bibui.Core.FreeType.Font;
using FtStrike = Bibui.Core.FreeType.FontStrike;

namespace Bibui.Core;

/// <summary>
/// One font file, as bibui measures and draws with it: <see cref="Fonts"/> hands
/// these out, and text is split into runs of one face each. A raster backend
/// makes its own native face from <see cref="FontFile"/> (Skia) or draws from
/// <see cref="FreeType"/>'s glyph masks (the managed rasterizer).
///
/// <para>Confined to the rendering thread, like the <see cref="Fonts"/> it came
/// from: FreeType faces are not safe to use concurrently.</para>
/// </summary>
public sealed class FontFace
{
    private static int s_nextId;

    internal FontFace(FtFont font)
    {
        FreeType = font;
        Id = Interlocked.Increment(ref s_nextId);
    }

    /// <summary>Process-unique, for a backend's cache of native faces.</summary>
    public int Id { get; }

    /// <summary>The family it was registered under; empty for the built-in Inter.</summary>
    public string Family => FreeType.Family;

    /// <summary>The weight it was registered under (400 or 600 for the built-in Inter).</summary>
    public int Weight => FreeType.Weight;

    /// <summary>Whether the face draws <paramref name="codepoint"/>.</summary>
    public bool HasGlyph(int codepoint) => FreeType.HasGlyph(codepoint);

    /// <summary>The font file's bytes, for a backend to make its native face from.</summary>
    public unsafe ReadOnlySpan<byte> FontFile => new(FreeType.Data.Pointer, FreeType.Data.Length);

    /// <summary>The FreeType face, for a raster backend that draws with its glyph masks.</summary>
    public FtFont FreeType { get; }

    /// <summary>The face at <paramref name="sizePx"/> physical px per em, made once and kept.</summary>
    public FtStrike Strike(float sizePx) => FreeType.Engine.Strike(FreeType, sizePx);

    public override string ToString() => FreeType.ToString();
}
