namespace Bibui.Core;

// bibui's own graphics value types. The public drawing API speaks these, not
// SkiaSharp's, so an app that draws custom content does not take a dependency
// on the rasterizer that happens to paint it. They are plain structs with no
// Skia in their signatures; the conversion to Skia lives with the Skia backend
// (Bibui.Raster.Skia, SkiaInterop) and is internal to it.
//
// Points are System.Numerics.Vector2: it is already the point type of the
// layout API (Box.Rect), it is Skia-free, and its layout matches SKPoint.

/// <summary>
/// A straight-alpha 8-bit RGBA colour, packed <c>0xAARRGGBB</c>. Resolved and
/// concrete — unlike <see cref="UiColor"/>, which may name a theme token and is
/// only a colour once a theme resolves it.
/// </summary>
public readonly record struct Rgba
{
    /// <summary>The packed value, <c>0xAARRGGBB</c>.</summary>
    public uint Argb { get; }

    public Rgba(uint argb) => Argb = argb;

    public Rgba(byte r, byte g, byte b, byte a = 255)
        => Argb = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

    public byte R => (byte)(Argb >> 16);
    public byte G => (byte)(Argb >> 8);
    public byte B => (byte)Argb;
    public byte A => (byte)(Argb >> 24);

    /// <summary>The same colour with its alpha replaced.</summary>
    public Rgba WithAlpha(byte a) => new((Argb & 0x00FFFFFFu) | ((uint)a << 24));

    /// <summary>
    /// Parses <c>#rrggbb</c> or <c>#rrggbbaa</c>, as the theme spells colours.
    /// A malformed value gives opaque black rather than throwing.
    /// </summary>
    public static Rgba Parse(ReadOnlySpan<char> hex)
    {
        var (r, g, b, a) = UiColor.ParseHex(hex);
        return new Rgba(r, g, b, a);
    }

    public static Rgba Black => new(0xFF000000u);
    public static Rgba White => new(0xFFFFFFFFu);
    public static Rgba Transparent => default;

    /// <summary><c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when not opaque.</summary>
    public override string ToString() =>
        A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

/// <summary>An axis-aligned rectangle by its edges, in whatever units the caller names.</summary>
public readonly record struct RectF(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public float MidX => Left + Width / 2f;
    public float MidY => Top + Height / 2f;

    /// <summary>From an origin and a size.</summary>
    public static RectF FromXywh(float x, float y, float width, float height)
        => new(x, y, x + width, y + height);
}

/// <summary>Vertical extents of a font at a size, in physical px. Ascent is negative (above the baseline).</summary>
public readonly record struct TextMetrics(float Ascent, float Descent, float Leading);

/// <summary>Whether a paint fills the shape, strokes its outline, or both.</summary>
public enum PaintStyle { Fill = 0, Stroke = 1, StrokeAndFill = 2 }

/// <summary>How an open stroke ends.</summary>
public enum StrokeCap { Butt = 0, Round = 1, Square = 2 }

/// <summary>How a stroke turns a corner.</summary>
public enum StrokeJoin { Miter = 0, Round = 1, Bevel = 2 }

/// <summary>How an image is sampled when it is scaled.</summary>
public enum FilterQuality { None = 0, Low = 1, Medium = 2, High = 3 }
