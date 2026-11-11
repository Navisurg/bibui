using System.Numerics;
using Bibui.Text;
using Bibui.Text.FreeType;
using Bibui.Text.FreeType.Native;

namespace Bibui.Glyphs;

/// <summary>
/// The coverage of one glyph at one strike and one quarter-pixel x phase: an A8
/// image, <see cref="Width"/> bytes per row (a subpixel mask: three samples a
/// pixel, left to right), or a colour glyph's premultiplied pixels
/// (<see cref="IsColor"/>), placed with its top-left at (<c>originX + Left</c>,
/// <c>baselineY - Top</c>). The coverage is FreeType's own,
/// before any gamma: blend it through <see cref="MaskGamma.Table"/>.
///
/// <para>Immutable once made, so it may be read from any thread, including
/// while the cache that made it goes on making others.</para>
/// </summary>
public sealed class GlyphMask
{
    internal static readonly GlyphMask Empty = new(0, 0, 0, 0, []);

    /// <summary>Offset of the mask's left edge from the glyph's integer pen x.</summary>
    public int Left { get; }
    /// <summary>Distance from the baseline up to the mask's top row.</summary>
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }

    private readonly byte[] _coverage;
    private readonly byte[] _pixels;

    /// <summary>Row-major coverage, stride <see cref="Width"/> (a subpixel mask: <c>3 * Width</c>). Empty for a colour glyph.</summary>
    public ReadOnlySpan<byte> Coverage => _coverage;

    /// <summary>
    /// Whether this is a colour glyph (an emoji's bitmap, COLR layers): an image in
    /// <see cref="Pixels"/>, drawn as it is, its alpha scaled by the text colour's,
    /// rather than coverage filled with the text colour.
    /// </summary>
    public bool IsColor => _pixels.Length > 0;

    /// <summary>
    /// A colour glyph's pixels: premultiplied B, G, R, A bytes, row-major, stride
    /// <c>4 × Width</c>; on a little-endian machine, one premultiplied 0xAARRGGBB
    /// <see cref="uint"/> a pixel. Empty for a coverage mask.
    /// </summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    public bool IsEmpty => Width == 0 || Height == 0;

    internal GlyphMask(int left, int top, int width, int height, byte[] coverage)
        : this(left, top, width, height, coverage, []) { }

    /// <summary>A colour glyph: <paramref name="bgra"/> premultiplied, 4 bytes a pixel.</summary>
    internal static GlyphMask Color(int left, int top, int width, int height, byte[] bgra)
        => new(left, top, width, height, [], bgra);

    private GlyphMask(int left, int top, int width, int height, byte[] coverage, byte[] pixels)
    {
        Left = left; Top = top; Width = width; Height = height; _coverage = coverage; _pixels = pixels;
    }
}

/// <summary>A glyph's mask and where its top-left lands, in device pixels.</summary>
public readonly record struct PlacedGlyph(GlyphMask Mask, int X, int Y);

/// <summary>
/// Glyphs as coverage masks, for a rasterizer that draws text itself: rendered
/// with FreeType, kept in the caller's cache, and placed in device pixels.
///
/// <para><b>The cache.</b> The caller's dictionary, empty to start: each strike's
/// masks, filled here under glyph, antialias and phase (glyph &lt;&lt; 3 | antialias &lt;&lt; 2 | phase),
/// rendered on
/// first use and kept for the dictionary's life, as strikes are kept for their
/// engine's. A strike is its own key: the engine makes one per font and size.
/// Confined to the thread of the engine its strikes come from, since a miss loads
/// through the strike's FreeType face; the masks are immutable and may be read
/// anywhere.</para>
///
/// <para><b>Settings.</b> Glyphs are loaded with
/// <c>FT_LOAD_TARGET_NORMAL | FORCE_AUTOHINT | NO_BITMAP | IGNORE_GLOBAL_ADVANCE_WIDTH</c>:
/// autohinted, fully hinted outlines, no embedded bitmaps (a subpixel mask:
/// FreeType's LCD target, hinted only vertically). Masks are rendered at four
/// x phases, a quarter pixel apart, and a glyph lands at
/// x + 1/8 floored to a quarter pixel, y rounded. These are the settings the
/// Skia backend's text paint (IsAutohinted, HintingLevel Full, SubpixelText)
/// resolves to on FreeType, and the placement rule is its raster glyph
/// painter's, so the masks reproduce its glyphs exactly.</para>
///
/// <para><b>Colour faces.</b> A face with colour glyphs is loaded with
/// <c>FT_LOAD_COLOR</c>: CBDT bitmaps (from the bitmap size the strike picked,
/// scaled to its size by area averaging) and COLRv0 layers (blended by FreeType)
/// come back as premultiplied BGRA masks (<see cref="GlyphMask.IsColor"/>), placed
/// on whole pixels and whatever the antialias. A glyph such a face has no colour
/// for is coverage, as any other.</para>
/// </summary>
public static unsafe class GlyphMasks
{
    private const int LoadFlags = FT.LOAD_FORCE_AUTOHINT
        | FT.LOAD_NO_BITMAP | FT.LOAD_IGNORE_GLOBAL_ADVANCE_WIDTH;

    /// <summary>The subpixel rounding bias on x: half of the 1/4 px sample spacing.</summary>
    private const float SubpixelBias = 1f / 8f;

    // By TextAntialias (Grayscale, Subpixel): the load target, the render mode,
    // and the samples a pixel the bitmap has.
    private static readonly int[] s_loadTarget = [FT.LOAD_TARGET_NORMAL, FT.LOAD_TARGET_LCD];
    private static readonly int[] s_renderMode = [FT.RENDER_MODE_NORMAL, FT.RENDER_MODE_LCD];
    private static readonly int[] s_samples = [1, 3];

    /// <summary>
    /// The masks of <paramref name="glyphs"/> of <paramref name="strike"/> at
    /// <paramref name="positions"/> from an origin (device x = <paramref name="originX"/>
    /// + position.X), each from <paramref name="cache"/> or rendered into it, and
    /// placed by the placement rule (<see cref="GlyphMasks"/>), written to
    /// <paramref name="into"/>: what a rasterizer composites, with the edges
    /// <paramref name="antialias"/> asks for. Empty masks (spaces) are skipped;
    /// returns how many were written.
    /// </summary>
    public static int PlacedFromGlyphs(Dictionary<FontStrike, Dictionary<uint, GlyphMask>> cache, FontStrike strike, ReadOnlySpan<ushort> glyphs,
        ReadOnlySpan<Vector2> positions, float originX, float baselineY, Span<PlacedGlyph> into, TextAntialias antialias)
    {
        if (glyphs.Length != positions.Length)
            throw new ArgumentException("One position per glyph.", nameof(positions));
        var masks = MasksOfStrike(cache, strike);
        int n = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            var placed = PlacedFromGlyph(masks, strike, glyphs[i], originX + positions[i].X, baselineY + positions[i].Y, antialias);
            if (!placed.Mask.IsEmpty) into[n++] = placed;
        }
        return n;
    }

    // A run's glyphs are all one strike's: it looks the strike up once.
    private static Dictionary<uint, GlyphMask> MasksOfStrike(Dictionary<FontStrike, Dictionary<uint, GlyphMask>> cache, FontStrike strike)
    {
        if (!cache.TryGetValue(strike, out var masks)) cache[strike] = masks = new();
        return masks;
    }

    // Where a glyph positioned at (x, y) in device pixels lands: its phase and
    // its integer origin.
    private static PlacedGlyph PlacedFromGlyph(Dictionary<uint, GlyphMask> masks, FontStrike strike, uint glyph, float x, float y, TextAntialias antialias)
    {
        if (strike.Font.HasColor)
        {
            // A colour glyph is an image, placed on whole pixels: one mask a glyph.
            if (!masks.TryGetValue(glyph << 3, out var c)) masks[glyph << 3] = c = RenderColor(strike, glyph);
            return new PlacedGlyph(c, (int)MathF.Floor(x + 0.5f) + c.Left, (int)MathF.Floor(y + 0.5f) - c.Top);
        }
        float px = x + SubpixelBias;
        float ix = MathF.Floor(px);
        int sub = (int)((px - ix) * 4f) & 3;
        var m = MaskFromGlyph(masks, strike, glyph, sub, antialias);
        return new PlacedGlyph(m, (int)ix + m.Left, (int)MathF.Floor(y + 0.5f) - m.Top);
    }

    // The glyph's mask at a quarter-pixel phase (0..3), kept or rendered: the
    // hinted outline translated right by the phase, then rendered as 8-bit
    // anti-aliased coverage.
    private static GlyphMask MaskFromGlyph(Dictionary<uint, GlyphMask> masks, FontStrike strike, uint glyph, int sub, TextAntialias antialias)
    {
        uint key = glyph << 3 | (uint)antialias << 2 | (uint)sub;
        if (masks.TryGetValue(key, out var m)) return m;
        m = Render(strike, glyph, sub, antialias);
        masks[key] = m;
        return m;
    }

    // The strike's size is made the face's active one first: the face, and its
    // one glyph slot, are shared by every strike of the font (FontStrike.Activate).
    private static GlyphMask Render(FontStrike strike, uint glyph, int sub, TextAntialias antialias)
    {
        strike.Activate();
        IntPtr face = strike.Font.FtFace;
        if (FT.FT_Load_Glyph(face, glyph, LoadFlags | s_loadTarget[(int)antialias]) != 0) return GlyphMask.Empty;
        IntPtr slot = FT.Read<IntPtr>(face, FT.Face_Glyph);
        if (sub != 0) FT.FT_Outline_Translate(slot + FT.Slot_Outline, sub * 16, 0);   // 26.6: 16 = 1/4 px
        if (FT.FT_Render_Glyph(slot, s_renderMode[(int)antialias]) != 0) return GlyphMask.Empty;

        IntPtr bmp = slot + FT.Slot_Bitmap;
        int rows = (int)FT.Read<uint>(bmp, FT.Bitmap_Rows);
        int rowBytes = (int)FT.Read<uint>(bmp, FT.Bitmap_Width);
        int width = rowBytes / s_samples[(int)antialias];
        if (rows == 0 || width == 0) return GlyphMask.Empty;
        int pitch = FT.Read<int>(bmp, FT.Bitmap_Pitch);
        byte* buf = (byte*)FT.Read<IntPtr>(bmp, FT.Bitmap_Buffer);
        var coverage = new byte[rowBytes * rows];
        for (int y = 0; y < rows; y++)
            new ReadOnlySpan<byte>(buf + y * pitch, rowBytes).CopyTo(coverage.AsSpan(y * rowBytes, rowBytes));
        return new GlyphMask(
            FT.Read<int>(slot, FT.Slot_BitmapLeft),
            FT.Read<int>(slot, FT.Slot_BitmapTop),
            width, rows, coverage);
    }

    // A glyph of a colour face: FreeType's BGRA bitmap (a CBDT PNG decoded, or COLR
    // layers blended), scaled from the bitmap size to the strike's by area averaging.
    // A glyph the face has no colour for comes back as coverage, and is kept as that.
    private static GlyphMask RenderColor(FontStrike strike, uint glyph)
    {
        strike.Activate();
        IntPtr face = strike.Font.FtFace;
        int flags = FT.LOAD_COLOR | (strike.Font.IsScalable ? FT.LOAD_NO_HINTING : 0);
        if (FT.FT_Load_Glyph(face, glyph, flags) != 0) return GlyphMask.Empty;
        IntPtr slot = FT.Read<IntPtr>(face, FT.Face_Glyph);
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
        float s = strike.BitmapScale;
        if (s == 1f) return GlyphMask.Color(left, top, width, rows, src);

        int w = Math.Max(1, (int)MathF.Round(width * s)), h = Math.Max(1, (int)MathF.Round(rows * s));
        var scaled = ColorScale.Resample(src, width, rows, w, h);
        return GlyphMask.Color((int)MathF.Round(left * s), (int)MathF.Round(top * s), w, h, scaled);
    }
}
