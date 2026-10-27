namespace Bibui.Text.FreeType;

/// <summary>
/// A text colour as 8-bit, unpremultiplied RGBA. Alpha scales the coverage; the
/// RGB picks the gamma table (see <see cref="MaskGamma"/>).
/// </summary>
public readonly record struct TextColor(byte R, byte G, byte B, byte A = 255);

/// <summary>
/// Vertical metrics of a strike in physical px, y growing down, so
/// <see cref="Ascent"/> is negative and <see cref="Descent"/> positive. A line's
/// em box is <c>Descent - Ascent</c>; the baseline of text whose box top is at
/// <c>top</c> is <c>top - Ascent</c>.
/// </summary>
public readonly record struct FontMetrics(float Ascent, float Descent, float Leading);

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
