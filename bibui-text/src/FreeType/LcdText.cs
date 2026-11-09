namespace Bibui.Text.FreeType;

/// <summary>
/// Subpixel (LCD) text, an experiment: <c>BIBUI_LCD=rgb</c> or <c>bgr</c> renders
/// glyphs at three samples a pixel, one per colour stripe of the panel, in that
/// order left to right; unset or anything else keeps greyscale coverage.
/// Read once, at start.
/// </summary>
public static class LcdText
{
    private static readonly string? s_order = Environment.GetEnvironmentVariable("BIBUI_LCD")?.Trim().ToLowerInvariant();

    /// <summary>Whether glyphs are rendered as LCD masks.</summary>
    public static bool Enabled { get; } = s_order is "rgb" or "bgr";

    /// <summary>The panel's stripes run blue, green, red left to right.</summary>
    public static bool Bgr { get; } = s_order is "bgr";
}
