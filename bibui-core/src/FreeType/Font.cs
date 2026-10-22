using Bibui.Core.FreeType.Native;

namespace Bibui.Core.FreeType;

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

    /// <summary>Whether the face has outlines that scale to any size; a bitmap font (CBDT, sbix) has only its fixed strikes.</summary>
    public bool IsScalable { get; }

    /// <summary>Whether the face has colour glyphs (CBDT bitmaps or COLR layers), drawn as images rather than coverage.</summary>
    public bool HasColor { get; }

    /// <summary>The pixel sizes of a face's embedded bitmap strikes, smallest first; empty for most fonts.</summary>
    internal float[] FixedSizes { get; }

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
        GlyphCount = (int)FT.Read<nint>(_ftFace, FT.Face_NumGlyphs);
        Ascender = FT.Read<short>(_ftFace, FT.Face_Ascender);
        Descender = FT.Read<short>(_ftFace, FT.Face_Descender);
        LineHeight = FT.Read<short>(_ftFace, FT.Face_Height);
        _bmpCmap = new uint[65536];
        long flags = FT.Read<nint>(_ftFace, FT.Face_FaceFlags);
        IsScalable = (flags & FT.FACE_FLAG_SCALABLE) != 0;
        HasColor = (flags & FT.FACE_FLAG_COLOR) != 0;
        int fixedCount = (flags & FT.FACE_FLAG_FIXED_SIZES) != 0 ? FT.Read<int>(_ftFace, FT.Face_NumFixedSizes) : 0;
        FixedSizes = new float[Math.Max(0, fixedCount)];
        IntPtr sizes = FT.Read<IntPtr>(_ftFace, FT.Face_AvailableSizes);
        for (int i = 0; i < FixedSizes.Length; i++)
            FixedSizes[i] = FT.Read<nint>(sizes + i * FT.BitmapSize_Size, FT.BitmapSize_YPpem) / 64f;   // 26.6

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
