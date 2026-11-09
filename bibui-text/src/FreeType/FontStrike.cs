using Bibui.Text.FreeType.Native;

namespace Bibui.Text.FreeType;

/// <summary>
/// One <see cref="Font"/> at one physical pixel size: advances, metrics and
/// shaping. Get one from <see cref="TextEngine.Strike(Font, float)"/>; the engine
/// keeps it, so asking again for the same font and size is a lookup.
///
/// <para><b>Advances.</b> FreeType's <i>linear</i> (unhinted, 16.16) advances, so
/// text measures and positions at subpixel precision (the hinted advance would
/// move layouts by 1-6%), as the Skia backend's text paint measures. Drawing a
/// strike's glyphs is a rasterizer's: one that renders them itself loads them
/// through the strike's face (<see cref="Activate"/>), as Bibui.Glyphs does.</para>
///
/// <para>Owned by its engine and confined to the engine's thread.</para>
/// </summary>
public sealed unsafe class FontStrike
{
    private const int AdvanceFlags = FT.LOAD_NO_HINTING | FT.LOAD_NO_BITMAP | FT.LOAD_IGNORE_GLOBAL_ADVANCE_WIDTH;

    // The font's FT_Face with this strike's own FT_Size: one face
    // per typeface, so the auto-hinter's per-face analysis (script coverage, blue
    // zones) runs once per font rather than once per size. A face per strike made
    // a cold strike three times slower.
    private readonly IntPtr _ftFace;
    private readonly IntPtr _ftSize;
    private readonly IntPtr _hbFont;
    private readonly float[] _advances;   // per glyph, px; NaN = not loaded yet

    public Font Font { get; }

    /// <summary>Physical px per em.</summary>
    public float Size { get; }

    public FontMetrics Metrics { get; }

    /// <summary>
    /// How much a bitmap-only face's glyphs are scaled to reach <see cref="Size"/>:
    /// the size over the bitmap size the strike picked. 1 for an outline face.
    /// </summary>
    internal float BitmapScale { get; } = 1f;

    internal FontStrike(Font font, float sizePx)
    {
        if (!(sizePx > 0f) || float.IsInfinity(sizePx))
            throw new ArgumentOutOfRangeException(nameof(sizePx), sizePx, "Size must be positive and finite.");
        Font = font;
        Size = sizePx;
        _ftFace = font.FtFace;
        FT.Check(FT.FT_New_Size(_ftFace, out _ftSize), "FT_New_Size");
        Activate();
        // SkScalarToFDot6 truncates: (FT_F26Dot6)(size * 64). Rounding instead puts a
        // size off the 1/64 grid (14 px at scale 1.1) one step up, and every advance
        // with it (1.8 px over a paragraph). Metrics below still use the exact size.
        nint size26 = (nint)(sizePx * 64f);
        if (font.IsScalable)
            FT.Check(FT.FT_Set_Char_Size(_ftFace, size26, size26, 72, 72), "FT_Set_Char_Size");
        else
        {
            // A face with no outlines (CBDT colour emoji) has only the sizes its
            // bitmaps were drawn at, and FreeType refuses any other. The strike
            // takes the smallest at least this size (shrinking keeps it sharp),
            // else the largest, and its glyphs are scaled from it.
            var fixedSizes = font.FixedSizes;
            if (fixedSizes.Length == 0) throw new InvalidOperationException($"{font}: no outlines and no bitmap sizes.");
            int pick = 0;
            for (int i = 1; i < fixedSizes.Length; i++)
            {
                float best = fixedSizes[pick], ppem = fixedSizes[i];
                if (best < sizePx ? ppem > best : ppem >= sizePx && ppem < best) pick = i;
            }
            FT.Check(FT.FT_Select_Size(_ftFace, pick), "FT_Select_Size");
            BitmapScale = sizePx / fixedSizes[pick];
        }

        _advances = new float[font.GlyphCount];
        Array.Fill(_advances, float.NaN);

        if (font.IsScalable)
        {
            // SkScalerContext_FreeType::generateFontMetrics, scalable branch: design
            // units over the em, then times the size, in that order.
            float upem = font.UnitsPerEm;
            float ascent = -(float)font.Ascender / upem;
            float descent = -(float)font.Descender / upem;
            float leading = (float)(font.LineHeight + (font.Descender - font.Ascender)) / upem;
            Metrics = new FontMetrics(ascent * sizePx, descent * sizePx, leading * sizePx);
        }
        else
        {
            // A bitmap-only face has no units per em (FreeType leaves it, and the
            // face's ascender and descender, 0): its metrics are the picked
            // size's, 26.6 px, scaled to this size, as Skia's bitmap branch takes them.
            float k = BitmapScale / 64f;
            float ascender = FT.Read<nint>(_ftSize, FT.Size_Ascender) * k;
            float descender = FT.Read<nint>(_ftSize, FT.Size_Descender) * k;
            float height = FT.Read<nint>(_ftSize, FT.Size_Height) * k;
            Metrics = new FontMetrics(-ascender, -descender, height - (ascender - descender));
        }

        _hbFont = HB.hb_font_create(font.HbFace);
        // The same truncated 26.6 size FreeType has, in 16.16, so shaped advances agree with linear ones.
        int scale = (int)size26 * 1024;
        HB.hb_font_set_scale(_hbFont, scale, scale);
    }

    /// <summary>
    /// Makes this strike's size the active one on its font's FreeType face
    /// (<see cref="Font.FtFace"/>), for a load at this size. Every strike of a
    /// font shares the face, its active size and its one glyph slot: call it on
    /// the engine's thread right before loading a glyph, and read the slot before
    /// anything else of the font's engine is called.
    /// </summary>
    public void Activate()
    {
        if (Font.ActiveSize == _ftSize) return;
        FT.Check(FT.FT_Activate_Size(_ftSize), "FT_Activate_Size");
        Font.ActiveSize = _ftSize;
    }

    /// <summary>The linear advance of <paramref name="glyph"/> in px: FreeType's 16.16 linearHoriAdvance as a float.</summary>
    public float Advance(uint glyph)
    {
        if (glyph >= (uint)_advances.Length) return 0f;
        float a = _advances[glyph];
        if (!float.IsNaN(a)) return a;
        a = 0f;
        Activate();
        // The linear advance is the unhinted one, so FreeType's fast path (hmtx,
        // scaled, 16.16) gives the same number as linearHoriAdvance after a full
        // hinted load, without running the auto-hinter.
        if (FT.FT_Get_Advance(_ftFace, glyph, AdvanceFlags, out nint adv) == 0)
            a = adv / 65536f;
        _advances[glyph] = a;
        return a;
    }

    // For chars below 256, the glyph and its advance side by side, filled on
    // first use (rad's utf8_class1_direct_map): a walk reads one entry per char.
    // Anything else goes through the font's cmap and the strike's advances.
    // TextLayer.GlyphDirect reads it: the fast path's measuring block.
    internal readonly DirectGlyph[] Direct = new DirectGlyph[256];

    // A char's entry: its glyph and advance in physical px, side by side, once Filled.
    internal struct DirectGlyph
    {
        public ushort Glyph;
        public bool Filled;
        public float Advance;
    }


    // The char at i the hot loops did not take: below 256, its direct entry
    // filled; above, its code point (a surrogate pair is one) looked up the slow
    // way. An unpaired surrogate is taken as itself, which no font maps, so it
    // draws as .notdef. Its glyph, its advance, and where the next char starts.
    internal (ushort Glyph, float Advance, int Next) Next(ReadOnlySpan<char> text, int i)
    {
        char c = text[i];
        if (c < 256)
        {
            uint g = Font.GlyphIndex(c);
            Direct[c] = new DirectGlyph { Glyph = (ushort)g, Filled = true, Advance = Advance(g) };
            return (Direct[c].Glyph, Direct[c].Advance, i + 1);
        }
        int cp = c, next = i + 1;
        if (char.IsHighSurrogate(c) && next < text.Length && char.IsLowSurrogate(text[next]))
            cp = char.ConvertToUtf32(c, text[next++]);
        uint glyph = Font.GlyphIndex(cp);
        return ((ushort)glyph, Advance(glyph), next);
    }

    /// <summary>
    /// Shapes <paramref name="text"/> with HarfBuzz, the font's default features
    /// on, into <paramref name="into"/> (cleared first): a grapheme cluster the
    /// text layer sets in the cluster face, whose sequence the font's ligature
    /// makes one glyph.
    /// </summary>
    public void Shape(ReadOnlySpan<char> text, GlyphRun into)
    {
        into.Clear();
        into.Strike = this;
        if (text.IsEmpty) return;

        IntPtr buffer = Font.Engine.HbBuffer;
        HB.hb_buffer_clear_contents(buffer);
        fixed (char* p = text)
            HB.hb_buffer_add_utf16(buffer, p, text.Length, 0, text.Length);
        HB.hb_buffer_guess_segment_properties(buffer);

        HB.hb_shape(_hbFont, buffer, null, 0);

        HB.GlyphInfo* infos = HB.hb_buffer_get_glyph_infos(buffer, out uint n);
        HB.GlyphPosition* pos = HB.hb_buffer_get_glyph_positions(buffer, out _);
        into.Reserve((int)n);
        for (int i = 0; i < n; i++)
        {
            into.Add(infos[i].Codepoint,
                pos[i].XAdvance / 65536f,
                pos[i].XOffset / 65536f,
                -pos[i].YOffset / 65536f);      // HarfBuzz y is up; ours is down
        }
    }

    internal void Destroy()
    {
        HB.hb_font_destroy(_hbFont);   // the FT_Face goes with the engine's FT_Library
    }

    public override string ToString() => $"{Font} @ {Size}px";
}
