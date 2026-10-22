using System.Numerics;
using Bibui.Core.FreeType.Native;

namespace Bibui.Core.FreeType;

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
/// <para><b>Colour faces.</b> A face with colour glyphs is loaded with
/// <c>FT_LOAD_COLOR</c>: CBDT bitmaps (from the strike nearest the size, picked
/// with <c>FT_Select_Size</c> on a face with no outlines, and scaled) and COLRv0
/// layers (blended by FreeType) come back as premultiplied BGRA masks
/// (<see cref="GlyphMask.IsColor"/>), placed on whole pixels. A face with no
/// outlines takes its advances from hmtx through HarfBuzz.</para>
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

    // A bitmap face (CBDT emoji) has only the sizes of its strikes: the one picked,
    // and how much its bitmaps are scaled to reach this size. 1 for an outline face.
    private readonly float _bitmapScale = 1f;

    public Font Font { get; }

    /// <summary>Physical px per em.</summary>
    public float Size { get; }

    public FontMetrics Metrics { get; }

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
        if (!font.IsScalable && font.FixedSizes.Length > 0)
        {
            // The smallest strike at least this size (shrinking keeps it sharp), else the largest.
            int pick = -1;
            for (int i = 0; i < font.FixedSizes.Length; i++)
            {
                float ppem = font.FixedSizes[i];
                if (pick < 0) { pick = i; continue; }
                float best = font.FixedSizes[pick];
                bool better = best < sizePx ? ppem > best : ppem >= sizePx && ppem < best;
                if (better) pick = i;
            }
            FT.Check(FT.FT_Select_Size(_ftFace, pick), "FT_Select_Size");
            _bitmapScale = sizePx / font.FixedSizes[pick];
        }
        else FT.Check(FT.FT_Set_Char_Size(_ftFace, size26, size26, 72, 72), "FT_Set_Char_Size");

        _advances = new float[font.GlyphCount];
        Array.Fill(_advances, float.NaN);

        // SkScalerContext_FreeType::generateFontMetrics, scalable branch: design
        // units over the em, then times the size, in that order.
        // A bitmap-only face (CBDT colour emoji) has no units per em: FreeType leaves it and the
        // face's ascender and descender 0. Its metrics are the selected strike's, in 26.6 pixels,
        // scaled to this size, as Skia's bitmap branch takes them.
        float upem = font.UnitsPerEm;
        if (upem > 0f)
        {
            float ascent = -(float)font.Ascender / upem;
            float descent = -(float)font.Descender / upem;
            float leading = (float)(font.LineHeight + (font.Descender - font.Ascender)) / upem;
            Metrics = new FontMetrics(ascent * sizePx, descent * sizePx, leading * sizePx);
        }
        else
        {
            float k = _bitmapScale / 64f;
            float asc = FT.Read<nint>(_ftSize, FT.Size_Ascender) * k, desc = FT.Read<nint>(_ftSize, FT.Size_Descender) * k;
            float height = FT.Read<nint>(_ftSize, FT.Size_Height) * k;
            Metrics = asc > 0f
                ? new FontMetrics(-asc, -desc, height - (asc - desc))
                : new FontMetrics(-sizePx, sizePx * 0.25f, 0f);   // no strike metrics either: an em above the baseline, a quarter below
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
        if (!Font.IsScalable)
        {
            // No outlines to take a linear advance from: hmtx, through HarfBuzz at this size.
            a = HB.hb_font_get_glyph_h_advance(_hbFont, glyph) / 65536f;
            _advances[glyph] = a;
            return a;
        }
        Activate();
        // The linear advance is the unhinted one, so FreeType's fast path (hmtx,
        // scaled, 16.16) gives the same number as linearHoriAdvance after a full
        // hinted load, without running the auto-hinter.
        if (FT.FT_Get_Advance(_ftFace, glyph, AdvanceFlags, out nint adv) == 0)
            a = adv / 65536f;
        _advances[glyph] = a;
        return a;
    }

    /// <summary>
    /// Width of <paramref name="text"/> in physical px, exactly as <c>SKPaint.MeasureText</c>
    /// computes it: one glyph per code point, linear advances summed in order. No
    /// shaping, so no kerning and no ligatures. Allocates nothing.
    /// </summary>
    public float Measure(ReadOnlySpan<char> text)
    {
        float total = 0f;
        var font = Font;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = NextCodepoint(text, ref i);
            total += Advance(font.GlyphIndex(cp));
        }
        return total;
    }

    /// <summary>Width of <paramref name="text"/> under <paramref name="options"/>.</summary>
    public float Measure(ReadOnlySpan<char> text, ShapeOptions options)
    {
        if (options.Mode == ShapingMode.Nominal) return Measure(text);
        var run = Font.Engine.ScratchRun;
        Shape(text, run, options);
        float w = run.Width;
        run.Clear();
        return w;
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
            var font = Font;
            for (int i = 0; i < text.Length; i++)
            {
                int start = i;
                uint g = font.GlyphIndex(NextCodepoint(text, ref i));
                into.Add(g, Advance(g), 0f, 0f, start);
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
        if (Font.HasColor) subpixelX = 0;   // a colour glyph is an image, placed on whole pixels
        uint key = glyph << 2 | (uint)(subpixelX & 3);
        if (_masks.TryGetValue(key, out var m)) return m;
        m = Font.HasColor ? RenderColor(glyph) : RenderMask(glyph, subpixelX & 3);
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

    // A glyph of a colour face: FreeType's BGRA bitmap (a CBDT PNG decoded, or COLR
    // layers blended), scaled from the strike to this size by area averaging. A glyph
    // the face has no colour for comes back as coverage, and is kept as that.
    private GlyphMask RenderColor(uint glyph)
    {
        Activate();
        int flags = FT.LOAD_COLOR | (Font.IsScalable ? FT.LOAD_NO_HINTING : 0);
        if (FT.FT_Load_Glyph(_ftFace, glyph, flags) != 0) return GlyphMask.Empty;
        IntPtr slot = FT.Read<IntPtr>(_ftFace, FT.Face_Glyph);
        IntPtr bmp = slot + FT.Slot_Bitmap;
        // A CBDT glyph loads as its bitmap; COLR layers load as an outline, and
        // rendering it blends them into one.
        if (FT.Read<uint>(slot, FT.Slot_Format) != FT.GLYPH_FORMAT_BITMAP && FT.FT_Render_Glyph(slot, FT.RENDER_MODE_NORMAL) != 0)
            return GlyphMask.Empty;
        int rows = (int)FT.Read<uint>(bmp, FT.Bitmap_Rows);
        int width = (int)FT.Read<uint>(bmp, FT.Bitmap_Width);
        if (rows == 0 || width == 0) return GlyphMask.Empty;
        int pitch = FT.Read<int>(bmp, FT.Bitmap_Pitch);
        byte mode = FT.Read<byte>(bmp, FT.Bitmap_PixelMode);
        byte* buf = (byte*)FT.Read<IntPtr>(bmp, FT.Bitmap_Buffer);
        if (buf == null || pitch < 0) return GlyphMask.Empty;
        int left = FT.Read<int>(slot, FT.Slot_BitmapLeft), top = FT.Read<int>(slot, FT.Slot_BitmapTop);

        if (mode != FT.PIXEL_MODE_BGRA)
        {
            if (mode != FT.PIXEL_MODE_GRAY) return GlyphMask.Empty;
            var coverage = new byte[width * rows];
            for (int y = 0; y < rows; y++)
                new ReadOnlySpan<byte>(buf + y * pitch, width).CopyTo(coverage.AsSpan(y * width, width));
            return new GlyphMask(left, top, width, rows, coverage);
        }

        var src = new byte[width * rows * 4];
        for (int y = 0; y < rows; y++)
            new ReadOnlySpan<byte>(buf + y * pitch, width * 4).CopyTo(src.AsSpan(y * width * 4, width * 4));
        if (_bitmapScale == 1f) return GlyphMask.Color(left, top, width, rows, src);

        float s = _bitmapScale;
        int w = Math.Max(1, (int)MathF.Round(width * s)), h = Math.Max(1, (int)MathF.Round(rows * s));
        var scaled = ColorScale.Resample(src, width, rows, w, h);
        return GlyphMask.Color((int)MathF.Round(left * s), (int)MathF.Round(top * s), w, h, scaled);
    }

    /// <summary>
    /// Where one glyph's mask lands for a glyph positioned at (<paramref name="x"/>,
    /// <paramref name="y"/>): picks the phase and the integer origin by the
    /// placement rule above.
    /// </summary>
    public PlacedGlyph PlaceGlyph(uint glyph, float x, float y)
    {
        if (Font.HasColor)
        {
            var c = GetMask(glyph, 0);
            return new PlacedGlyph(c, (int)MathF.Floor(x + 0.5f) + c.Left, (int)MathF.Floor(y + 0.5f) - c.Top);
        }
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

    /// <summary>
    /// Reads the code point at <paramref name="i"/>, advancing past a low surrogate.
    /// An unpaired surrogate is taken as itself, which no font maps, so it draws as .notdef.
    /// </summary>
    internal static int NextCodepoint(ReadOnlySpan<char> text, ref int i)
    {
        char c = text[i];
        if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
        {
            i++;
            return char.ConvertToUtf32(c, text[i]);
        }
        return c;
    }

    public override string ToString() => $"{Font} @ {Size}px";
}
