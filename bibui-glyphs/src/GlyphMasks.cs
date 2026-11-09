using System.Numerics;
using Bibui.Text.FreeType;
using Bibui.Text.FreeType.Native;

namespace Bibui.Glyphs;

/// <summary>
/// The coverage of one glyph at one strike and one quarter-pixel x phase: an A8
/// image, <see cref="Width"/> bytes per row, placed with its top-left at
/// (<c>originX + Left</c>, <c>baselineY - Top</c>). The coverage is FreeType's own,
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

    /// <summary>Row-major coverage, stride <see cref="Width"/>.</summary>
    public ReadOnlySpan<byte> Coverage => _coverage;

    public bool IsEmpty => Width == 0 || Height == 0;

    internal GlyphMask(int left, int top, int width, int height, byte[] coverage)
    {
        Left = left; Top = top; Width = width; Height = height; _coverage = coverage;
    }
}

/// <summary>A glyph's mask and where its top-left lands, in device pixels.</summary>
public readonly record struct PlacedGlyph(GlyphMask Mask, int X, int Y);

/// <summary>
/// The masks a renderer has drawn glyphs with: each strike's, by glyph and
/// phase. Rendered on first use and kept for the cache's life, as strikes are
/// kept for their engine's. The data; what is done with it is <see cref="GlyphMasks"/>.
///
/// <para>Confined to the thread of the engine its strikes come from: a miss
/// loads through the strike's FreeType face. The masks it holds are immutable
/// and may be read anywhere.</para>
/// </summary>
public sealed class GlyphCache
{
    // Each strike's masks, under glyph << 2 | phase. A strike is its own key: the
    // engine makes one per font and size, and keeps it.
    internal readonly Dictionary<FontStrike, Dictionary<uint, GlyphMask>> Masks = new(ReferenceEqualityComparer.Instance);
}

/// <summary>
/// Glyphs as coverage masks, for a rasterizer that draws text itself: rendered
/// with FreeType, kept in a <see cref="GlyphCache"/>, and placed in device pixels.
///
/// <para><b>Settings.</b> Glyphs are loaded with
/// <c>FT_LOAD_TARGET_NORMAL | FORCE_AUTOHINT | NO_BITMAP | IGNORE_GLOBAL_ADVANCE_WIDTH</c>:
/// autohinted, fully hinted outlines, no embedded bitmaps, no LCD. Masks are
/// rendered at four x phases, a quarter pixel apart, and a glyph lands at
/// x + 1/8 floored to a quarter pixel, y rounded. These are the settings the
/// Skia backend's text paint (IsAutohinted, HintingLevel Full, SubpixelText)
/// resolves to on FreeType, and the placement rule is its raster glyph
/// painter's, so the masks reproduce its glyphs exactly.</para>
/// </summary>
public static unsafe class GlyphMasks
{
    private const int LoadFlags = FT.LOAD_TARGET_NORMAL | FT.LOAD_FORCE_AUTOHINT
        | FT.LOAD_NO_BITMAP | FT.LOAD_IGNORE_GLOBAL_ADVANCE_WIDTH;

    /// <summary>The subpixel rounding bias on x: half of the 1/4 px sample spacing.</summary>
    private const float SubpixelBias = 1f / 8f;

    /// <summary>
    /// The masks of <paramref name="glyphs"/> of <paramref name="strike"/> at
    /// <paramref name="positions"/> from an origin (device x = <paramref name="originX"/>
    /// + position.X), each from <paramref name="cache"/> or rendered into it, and
    /// placed by the placement rule (<see cref="GlyphMasks"/>), written to
    /// <paramref name="into"/>: what a rasterizer composites. Empty masks (spaces)
    /// are skipped; returns how many were written.
    /// </summary>
    public static int PlacedFromGlyphs(GlyphCache cache, FontStrike strike, ReadOnlySpan<ushort> glyphs,
        ReadOnlySpan<Vector2> positions, float originX, float baselineY, Span<PlacedGlyph> into)
    {
        if (glyphs.Length != positions.Length)
            throw new ArgumentException("One position per glyph.", nameof(positions));
        var masks = MasksOfStrike(cache, strike);
        int n = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            var placed = PlacedFromGlyph(masks, strike, glyphs[i], originX + positions[i].X, baselineY + positions[i].Y);
            if (!placed.Mask.IsEmpty) into[n++] = placed;
        }
        return n;
    }

    // A run's glyphs are all one strike's: it looks the strike up once.
    private static Dictionary<uint, GlyphMask> MasksOfStrike(GlyphCache cache, FontStrike strike)
    {
        if (!cache.Masks.TryGetValue(strike, out var masks)) cache.Masks[strike] = masks = new();
        return masks;
    }

    // Where a glyph positioned at (x, y) in device pixels lands: its phase and
    // its integer origin.
    private static PlacedGlyph PlacedFromGlyph(Dictionary<uint, GlyphMask> masks, FontStrike strike, uint glyph, float x, float y)
    {
        float px = x + SubpixelBias;
        float ix = MathF.Floor(px);
        int sub = (int)((px - ix) * 4f) & 3;
        var m = MaskFromGlyph(masks, strike, glyph, sub);
        return new PlacedGlyph(m, (int)ix + m.Left, (int)MathF.Floor(y + 0.5f) - m.Top);
    }

    // The glyph's mask at a quarter-pixel phase (0..3), kept or rendered: the
    // hinted outline translated right by the phase, then rendered as 8-bit
    // anti-aliased coverage.
    private static GlyphMask MaskFromGlyph(Dictionary<uint, GlyphMask> masks, FontStrike strike, uint glyph, int sub)
    {
        uint key = glyph << 2 | (uint)sub;
        if (masks.TryGetValue(key, out var m)) return m;
        m = Render(strike, glyph, sub);
        masks[key] = m;
        return m;
    }

    // The strike's size is made the face's active one first: the face, and its
    // one glyph slot, are shared by every strike of the font (FontStrike.Activate).
    private static GlyphMask Render(FontStrike strike, uint glyph, int sub)
    {
        strike.Activate();
        IntPtr face = strike.Font.FtFace;
        if (FT.FT_Load_Glyph(face, glyph, LoadFlags) != 0) return GlyphMask.Empty;
        IntPtr slot = FT.Read<IntPtr>(face, FT.Face_Glyph);
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
}
