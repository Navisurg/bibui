using Bibui.FreeType.Native;

namespace Bibui.FreeType;

/// <summary>
/// One <see cref="FontData"/> opened in one <see cref="TextEngine"/>: the size-free
/// part of a face, i.e. its character map, design metrics and HarfBuzz face. Sized
/// work happens on a <see cref="FontStrike"/>, from <see cref="TextEngine.Strike(Font, float)"/>.
///
/// <para>Owned by its engine and confined to the engine's thread.</para>
/// </summary>
public sealed unsafe class Font
{
    private readonly IntPtr _ftFace;          // shared by the cmap and every strike (each with its own FT_Size)
    private readonly uint[] _bmpCmap;         // BMP code point -> glyph + 1; 0 = not looked up yet

    internal IntPtr HbFace { get; }
    internal IntPtr FtFace => _ftFace;

    /// <summary>The FT_Size currently active on <see cref="FtFace"/>; strikes switch it before loading.</summary>
    internal IntPtr ActiveSize { get; set; }
    internal TextEngine Engine { get; }

    /// <summary>The bytes this font reads.</summary>
    public FontData Data { get; }

    /// <summary>The family it was registered under; "" for the built-in default.</summary>
    public string Family { get; }

    /// <summary>The weight it was registered under.</summary>
    public int Weight { get; }

    /// <summary>Design units per em.</summary>
    public int UnitsPerEm { get; }

    /// <summary>Number of glyphs in the face.</summary>
    public int GlyphCount { get; }

    internal short Ascender { get; }
    internal short Descender { get; }
    internal short LineHeight { get; }

    internal Font(TextEngine engine, FontData data, string family, int weight)
    {
        Engine = engine;
        Data = data;
        Family = family;
        Weight = weight;
        _ftFace = engine.OpenFace(data);
        UnitsPerEm = FT.Read<ushort>(_ftFace, FT.Face_UnitsPerEm);
        GlyphCount = (int)FT.Read<long>(_ftFace, FT.Face_NumGlyphs);
        Ascender = FT.Read<short>(_ftFace, FT.Face_Ascender);
        Descender = FT.Read<short>(_ftFace, FT.Face_Descender);
        LineHeight = FT.Read<short>(_ftFace, FT.Face_Height);
        _bmpCmap = new uint[65536];

        IntPtr blob = HB.hb_blob_create(data.Pointer, (uint)data.Length, HB.MEMORY_MODE_READONLY, 0, 0);
        HbFace = HB.hb_face_create(blob, 0);
        HB.hb_blob_destroy(blob);   // the face holds its own reference
    }

    /// <summary>
    /// The glyph for a Unicode code point, 0 (.notdef) when the face has none:
    /// FreeType's cmap lookup.
    /// </summary>
    public uint GlyphIndex(int codepoint)
    {
        if ((uint)codepoint < 65536u)
        {
            uint g = _bmpCmap[codepoint];
            if (g != 0) return g - 1;
            g = FT.FT_Get_Char_Index(_ftFace, (nuint)codepoint);
            _bmpCmap[codepoint] = g + 1;
            return g;
        }
        return FT.FT_Get_Char_Index(_ftFace, (nuint)(uint)codepoint);
    }

    /// <summary>True when the face has a glyph for <paramref name="codepoint"/>.</summary>
    public bool HasGlyph(int codepoint) => GlyphIndex(codepoint) != 0;

    internal void Destroy()
    {
        HB.hb_face_destroy(HbFace);   // the FT_Face goes with the engine's FT_Library
    }

    public override string ToString() => $"{(Family.Length > 0 ? Family : "default")} {Weight} ({Data})";
}
