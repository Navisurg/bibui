using System.Numerics;
using Bibui.Text.FreeType.Native;

namespace Bibui.Text.FreeType;

/// <summary>
/// One <see cref="Font"/> at one physical pixel size: advances, metrics, shaping
/// and the glyph coverage cache. Get one from <see cref="TextEngine.Strike(Font, float)"/>;
/// the engine keeps it, so asking again for the same font and size is a lookup.
///
/// <para><b>Settings.</b> Glyphs are loaded with
/// <c>FT_LOAD_TARGET_NORMAL | FORCE_AUTOHINT | NO_BITMAP | IGNORE_GLOBAL_ADVANCE_WIDTH</c>:
/// autohinted, fully hinted outlines, no embedded bitmaps, no LCD. Advances are
/// FreeType's <i>linear</i> (unhinted, 16.16) advances, so text measures and
/// positions at subpixel precision (the hinted advance would move layouts by
/// 1-6%). Masks are rendered at four x phases, a quarter pixel apart, and a glyph
/// lands at x + 1/8 floored to a quarter pixel, y rounded.</para>
///
/// <para>These are the settings the Skia backend's text paint (IsAutohinted,
/// HintingLevel Full, SubpixelText) resolves to on FreeType, and the placement
/// rule is its raster glyph painter's: the fixture baselines are drawn by Skia,
/// so the strike reproduces its glyphs exactly.</para>
///
/// <para>Owned by its engine and confined to the engine's thread. The masks it
/// returns are immutable and may be read anywhere.</para>
/// </summary>
public sealed unsafe class FontStrike
{
    private const int LoadFlags = FT.LOAD_TARGET_NORMAL | FT.LOAD_FORCE_AUTOHINT
        | FT.LOAD_NO_BITMAP | FT.LOAD_IGNORE_GLOBAL_ADVANCE_WIDTH;

    private const int AdvanceFlags = FT.LOAD_NO_HINTING | FT.LOAD_NO_BITMAP | FT.LOAD_IGNORE_GLOBAL_ADVANCE_WIDTH;

    /// <summary>The subpixel rounding bias on x: half of the 1/4 px sample spacing.</summary>
    private const float SubpixelBias = 1f / 8f;

    // The font's FT_Face with this strike's own FT_Size: one face
    // per typeface, so the auto-hinter's per-face analysis (script coverage, blue
    // zones) runs once per font rather than once per size. A face per strike made
    // a cold strike three times slower.
    private readonly IntPtr _ftFace;
    private readonly IntPtr _ftSize;
    private readonly IntPtr _hbFont;
    private readonly float[] _advances;   // per glyph, px; NaN = not loaded yet
    private readonly Dictionary<uint, GlyphMask> _masks = new();

    public Font Font { get; }

    /// <summary>Physical px per em.</summary>
    public float Size { get; }

    public FontMetrics Metrics { get; }

    /// <summary>
    /// How much a bitmap-only face's glyphs are scaled to reach <see cref="Size"/>:
    /// the size over the bitmap size the strike picked. 1 for an outline face.
    /// </summary>
    internal float BitmapScale { get; } = 1f;

    /// <summary>Masks rendered so far (all four phases count separately).</summary>
    public int CachedMaskCount => _masks.Count;

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

    private void Activate()
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

    // The walks below keep their index, count and pen in registers, which is
    // most of their speed: nothing of theirs is passed by ref (a char is
    // decoded by value, Next returning where the next starts), and the hot loop
    // of Glyphs is a method of its own with no call in it. With either, the JIT
    // sends them through the stack every char, about three times slower.

    // Glyphs' hot loop: while the chars have filled direct entries.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static (int I, int N, float Pen) GlyphsDirect(ReadOnlySpan<char> text, int i, DirectGlyph[] direct,
        Span<ushort> glyphs, Span<System.Numerics.Vector2> points, int n, float pen)
    {
        for (char c; i < text.Length && (c = text[i]) < 256 && direct[c].Filled; i++, n++)
        {
            glyphs[n] = direct[c].Glyph;
            points[n] = new(pen, 0f);
            pen += direct[c].Advance;
        }
        return (i, n, pen);
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
    /// Shapes <paramref name="text"/> into <paramref name="into"/> (cleared first).
    /// <see cref="ShapeOptions.Nominal"/> (the default) maps each character through
    /// the cmap and takes its linear advance; HarfBuzz modes call hb_shape with the given features.
    /// </summary>
    public void Shape(ReadOnlySpan<char> text, GlyphRun into, ShapeOptions? options = null)
    {
        options ??= ShapeOptions.Nominal;
        into.Clear();
        into.Strike = this;
        if (text.IsEmpty) return;

        if (options.Mode == ShapingMode.Nominal)
        {
            into.Reserve(text.Length);
            for (int i = 0; i < text.Length;)
            {
                var (glyph, advance, next) = Next(text, i);
                into.Add(glyph, advance, 0f, 0f, i);
                i = next;
            }
            return;
        }

        IntPtr buffer = Font.Engine.HbBuffer;
        HB.hb_buffer_clear_contents(buffer);
        fixed (char* p = text)
            HB.hb_buffer_add_utf16(buffer, p, text.Length, 0, text.Length);
        HB.hb_buffer_guess_segment_properties(buffer);

        var features = options.Features;
        Span<HB.Feature> hbFeatures = features.Count <= 16
            ? stackalloc HB.Feature[features.Count]
            : new HB.Feature[features.Count];
        for (int i = 0; i < features.Count; i++)
            hbFeatures[i] = new HB.Feature { Tag = features[i].TagValue, Value = features[i].Value, Start = 0, End = uint.MaxValue };
        fixed (HB.Feature* f = hbFeatures)
            HB.hb_shape(_hbFont, buffer, f, (uint)hbFeatures.Length);

        HB.GlyphInfo* infos = HB.hb_buffer_get_glyph_infos(buffer, out uint n);
        HB.GlyphPosition* pos = HB.hb_buffer_get_glyph_positions(buffer, out _);
        into.Reserve((int)n);
        for (int i = 0; i < n; i++)
        {
            into.Add(infos[i].Codepoint,
                pos[i].XAdvance / 65536f,
                pos[i].XOffset / 65536f,
                -pos[i].YOffset / 65536f,       // HarfBuzz y is up; ours is down
                (int)infos[i].Cluster);
        }
    }

    /// <summary>
    /// The coverage mask for <paramref name="glyph"/> at quarter-pixel phase
    /// <paramref name="subpixelX"/> (0..3), rendered on first use and cached for the
    /// life of the strike. The hinted outline is translated right by
    /// the phase, then rendered as 8-bit anti-aliased coverage.
    /// </summary>
    public GlyphMask GetMask(uint glyph, int subpixelX)
    {
        uint key = glyph << 2 | (uint)(subpixelX & 3);
        if (_masks.TryGetValue(key, out var m)) return m;
        m = RenderMask(glyph, subpixelX & 3);
        _masks[key] = m;
        return m;
    }

    private GlyphMask RenderMask(uint glyph, int sub)
    {
        Activate();
        if (FT.FT_Load_Glyph(_ftFace, glyph, LoadFlags) != 0) return GlyphMask.Empty;
        IntPtr slot = FT.Read<IntPtr>(_ftFace, FT.Face_Glyph);
        if (sub != 0) FT.FT_Outline_Translate(slot + FT.Slot_Outline, sub * 16, 0);   // 26.6: 16 = 1/4 px
        if (FT.FT_Render_Glyph(slot, FT.RENDER_MODE_NORMAL) != 0) return GlyphMask.Empty;

        IntPtr bmp = slot + FT.Slot_Bitmap;
        int rows = (int)FT.Read<uint>(bmp, FT.Bitmap_Rows);
        int width = (int)FT.Read<uint>(bmp, FT.Bitmap_Width);
        if (rows == 0 || width == 0) return GlyphMask.Empty;
        int pitch = FT.Read<int>(bmp, FT.Bitmap_Pitch);
        byte* buf = (byte*)FT.Read<IntPtr>(bmp, FT.Bitmap_Buffer);
        var coverage = new byte[width * rows];
        for (int y = 0; y < rows; y++)
            new ReadOnlySpan<byte>(buf + y * pitch, width).CopyTo(coverage.AsSpan(y * width, width));
        return new GlyphMask(
            FT.Read<int>(slot, FT.Slot_BitmapLeft),
            FT.Read<int>(slot, FT.Slot_BitmapTop),
            width, rows, coverage);
    }

    /// <summary>
    /// Where one glyph's mask lands for a glyph positioned at (<paramref name="x"/>,
    /// <paramref name="y"/>): picks the phase and the integer origin by the
    /// placement rule above.
    /// </summary>
    public PlacedGlyph PlaceGlyph(uint glyph, float x, float y)
    {
        float px = x + SubpixelBias;
        float ix = MathF.Floor(px);
        int sub = (int)((px - ix) * 4f) & 3;
        var m = GetMask(glyph, sub);
        return new PlacedGlyph(m, (int)ix + m.Left, (int)MathF.Floor(y + 0.5f) - m.Top);
    }

    /// <summary>
    /// The coverage masks of <paramref name="run"/> drawn with its origin at
    /// (<paramref name="originX"/>, <paramref name="baselineY"/>), written to
    /// <paramref name="into"/> (which needs room for <c>run.Count</c>). Empty masks
    /// (spaces) are skipped; the return is how many were written. This is the
    /// hand-off to a rasterizer that composites masks itself.
    /// </summary>
    public int Place(GlyphRun run, float originX, float baselineY, Span<PlacedGlyph> into)
    {
        if (run.Strike is not null && run.Strike != this)
            throw new ArgumentException("The run was shaped in a different strike.", nameof(run));
        var glyphs = run.Glyphs;
        var adv = run.Advances;
        var xo = run.XOffsets;
        var yo = run.YOffsets;
        int n = 0;
        float pen = originX;
        for (int i = 0; i < glyphs.Length; i++)
        {
            var placed = PlaceGlyph(glyphs[i], pen + xo[i], baselineY + yo[i]);
            if (!placed.Mask.IsEmpty) into[n++] = placed;
            pen += adv[i];
        }
        return n;
    }

    /// <summary>
    /// The masks of glyphs at explicit positions relative to an origin
    /// (device x = <paramref name="originX"/> + position.X).
    /// Empty masks are skipped; returns how many were written.
    /// </summary>
    public int Place(ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> positions, float originX, float baselineY,
        Span<PlacedGlyph> into)
    {
        if (glyphs.Length != positions.Length)
            throw new ArgumentException("One position per glyph.", nameof(positions));
        int n = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            var placed = PlaceGlyph(glyphs[i], originX + positions[i].X, baselineY + positions[i].Y);
            if (!placed.Mask.IsEmpty) into[n++] = placed;
        }
        return n;
    }

    /// <summary>Drops every cached mask of this strike.</summary>
    public void ClearMasks() => _masks.Clear();

    internal void Destroy()
    {
        HB.hb_font_destroy(_hbFont);   // the FT_Face goes with the engine's FT_Library
    }

    public override string ToString() => $"{Font} @ {Size}px";
}
