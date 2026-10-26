namespace Bibui.Text.FreeType;

/// <summary>
/// A text colour as 8-bit, unpremultiplied RGBA. Alpha scales the coverage; the
/// RGB picks the gamma table (see <see cref="MaskGamma"/>).
/// </summary>
public readonly record struct TextColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>From 0xAARRGGBB, the order bibui's Rgba and SKColor use.</summary>
    public static TextColor FromArgb(uint argb)
        => new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    /// <summary>From 0xRRGGBB, opaque.</summary>
    public static TextColor FromRgb(uint rgb) => FromArgb(rgb | 0xFF000000u);
}

/// <summary>
/// Vertical metrics of a strike in physical px, y growing down, so
/// <see cref="Ascent"/> is negative and <see cref="Descent"/> positive. A line's
/// em box is <c>Descent - Ascent</c>; the baseline of text whose box top is at
/// <c>top</c> is <c>top - Ascent</c>.
/// </summary>
public readonly record struct FontMetrics(float Ascent, float Descent, float Leading);

/// <summary>
/// How a string becomes glyphs.
/// </summary>
public enum ShapingMode
{
    /// <summary>
    /// One glyph per code point from the cmap, each advanced by its linear
    /// (unhinted) advance, with no shaping. This is the mode that keeps today's
    /// layouts and baselines, and it is the fast one. The default.
    /// </summary>
    Nominal,

    /// <summary>
    /// HarfBuzz shaping with <see cref="ShapeOptions.Features"/>. With kerning,
    /// ligatures and contextual alternates off it measures identically to
    /// <see cref="Nominal"/> over bibui's corpus; they are not turned on yet.
    /// </summary>
    HarfBuzz,
}

/// <summary>An OpenType feature setting for <see cref="ShapingMode.HarfBuzz"/>, e.g. ("kern", 0).</summary>
public readonly record struct FontFeature(string Tag, uint Value)
{
    internal uint TagValue => Native.HB.Tag(Tag);
}

/// <summary>Options for <see cref="FontStrike.Shape(ReadOnlySpan{char}, GlyphRun, ShapeOptions?)"/>.</summary>
public sealed record ShapeOptions(ShapingMode Mode, IReadOnlyList<FontFeature> Features)
{
    /// <summary>
    /// kern, liga and calt off: bibui's text today, which is not shaped. Whether
    /// to turn them on is still open.
    /// </summary>
    public static readonly IReadOnlyList<FontFeature> BaselineCompatibleFeatures =
        [new("kern", 0), new("liga", 0), new("calt", 0)];

    /// <summary>Cmap plus linear advances, unshaped. The default.</summary>
    public static readonly ShapeOptions Nominal = new(ShapingMode.Nominal, []);

    /// <summary>HarfBuzz with kern, liga and calt off.</summary>
    public static readonly ShapeOptions HarfBuzzBaselineCompatible = new(ShapingMode.HarfBuzz, BaselineCompatibleFeatures);

    /// <summary>HarfBuzz with the font's default features (kerning, ligatures, contextual alternates).</summary>
    public static readonly ShapeOptions HarfBuzzDefault = new(ShapingMode.HarfBuzz, []);
}

/// <summary>
/// The coverage of one glyph at one strike and one quarter-pixel x phase: an A8
/// image, <see cref="Width"/> bytes per row, placed with its top-left at
/// (<c>originX + Left</c>, <c>baselineY - Top</c>). The coverage is FreeType's own,
/// before any gamma: apply <see cref="MaskGamma.Table(TextColor)"/> when compositing.
///
/// <para>Immutable once handed out, so it may be read from any thread, including
/// while the engine that made it goes on making others.</para>
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

/// <summary>
/// A glyph mask placed on the device: its top-left pixel is (<see cref="X"/>,
/// <see cref="Y"/>). What a rasterizer needs to composite text itself, through its
/// own clip and layers.
/// </summary>
public readonly record struct PlacedGlyph(GlyphMask Mask, int X, int Y);
