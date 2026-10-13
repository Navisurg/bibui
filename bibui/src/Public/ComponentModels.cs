namespace Bibui;

public abstract class UiComponent : UiNode
{
}

/// <summary>
/// A text: the UTF-8 bytes in <see cref="Content"/>, drawn as they stand on one
/// line in one font. Built with <c>runtime.Text</c>, which says everything
/// about it in the call; only its <see cref="Size"/> may be changed afterwards.
/// </summary>
public sealed class Text : UiComponent
{
    internal Text() { }

    /// <summary>
    /// What it shows. The caller's memory, not a copy: the caller keeps it as
    /// it is until the frame is painted.
    /// </summary>
    public ReadOnlyMemory<byte> Content { get; internal set; }

    /// <summary>
    /// Its size, always said: what it was built with, or the size pushed with
    /// <c>runtime.PushFontSize</c>, or <see cref="ComponentSize.Md"/>. Set it
    /// on the text that came back to change it.
    /// </summary>
    public FontSize Size { get; set; } = ComponentSize.Md;

    /// <summary>Its weight: the one pushed with <c>runtime.PushFontFace</c> when it was built.</summary>
    public FontWeight Weight { get; internal set; }

    /// <summary>Which of the theme's families it is set in.</summary>
    public FontSlot Family { get; internal set; }

    /// <summary>Its colour; null is <see cref="UiColor.TextPrimary"/>.</summary>
    public UiColor? Color { get; internal set; }
}

public enum FontSlot { Body, Display, Heading, Mono }

/// <summary>How heavy a text is set: the theme's normal weight, or its bold one.</summary>
public enum FontWeight { Regular, Bold }

/// <summary>
/// A standalone glyph, sized and colored independently — for placing a Lucide-style icon
/// anywhere without borrowing another component's icon slot (e.g. a leading search glyph in a
/// custom-composed search field, or a bare nav-item icon). Glyph names are looked up in
/// bibui's icon set, and a name it does not have is a mistake in the build:
/// <c>runtime.Icon</c> throws on one in a debug build, and the fixture verifier
/// fails a fixture that draws one. A release build draws the name's first
/// letter in its place instead, so a slot is never silently blank and a shipped
/// app does not fall over.
/// </summary>
public sealed class Icon : UiComponent
{
    /// <summary>
    /// True when bibui's icon set has an icon called <paramref name="name"/>
    /// (ignoring case). Check a name that comes from data before drawing it.
    /// </summary>
    public static bool IsKnown(string? name) => Bibui.Renderer.IconRegistry.Has(name);

    public string Name { get; set; } = "";

    /// <summary>
    /// Its side, always said, as a text's size is: what it was built with, or
    /// the size pushed with <c>runtime.PushFontSize</c>, or
    /// <see cref="ComponentSize.Md"/> — so an icon beside a label is the label's size.
    /// </summary>
    public FontSize Size { get; set; } = ComponentSize.Md;
    /// <summary>Null (default) resolves to the current theme's <c>TextPrimary</c> color.</summary>
    public UiColor? Color { get; set; }

    /// <summary>Back to the defaults above, for an icon the runtime reuses.</summary>
    internal void ResetIcon()
    {
        Name = "";
        Size = ComponentSize.Md;
        Color = null;
    }
}

public enum ComponentSize { Sm, Md, Lg, Xl, Xxl }

/// <summary>
/// How large a text is: one of the theme's size steps, or a size in logical
/// pixels. A step converts to one, so anything that takes a step takes this.
/// Pixels are for text that scales with something continuous — a view zoomed
/// by the caller lays its text out at the step's size times the zoom.
/// </summary>
public readonly record struct FontSize
{
    private readonly ComponentSize _step;
    private readonly float _pixels;   // 0: the step

    private FontSize(ComponentSize step, float pixels) => (_step, _pixels) = (step, pixels);

    /// <summary><paramref name="pixels"/> logical px, whatever the theme's steps are.</summary>
    public static FontSize Px(float pixels) => new(default, pixels > 0f ? pixels
        : throw new ArgumentOutOfRangeException(nameof(pixels), pixels, "A font size is larger than zero."));

    public static implicit operator FontSize(ComponentSize step) => new(step, 0f);

    /// <summary>The size in logical px, under <paramref name="theme"/>'s steps.</summary>
    internal float In(Bibui.Theming.ThemeTokens theme) => _pixels > 0f ? _pixels : Fonts.SizeOf(_step, theme);

    /// <summary>The same size times <paramref name="factor"/>, in pixels.</summary>
    public FontSize Times(float factor, Bibui.Theming.ThemeTokens theme) => Px(In(theme) * factor);
}
public enum BadgeVariant { Default, Primary, Success, Destructive, Outline }
// AccentForeground is the colour that reads *on* an accent fill, not the accent
// itself — the pair a Primary button needs.
public enum TextColor { Primary, Secondary, Disabled, Accent, AccentForeground }

public sealed class Spinner : UiComponent
{
    public ComponentSize Size { get; set; } = ComponentSize.Md;

    /// <summary>Exact diameter in logical pixels, overriding <see cref="Size"/>'s step.</summary>
    public float? Diameter { get; set; }

    /// <summary>Stroke width. Defaults to a tenth of the diameter, clamped to sensible bounds.</summary>
    public float? StrokeWidth { get; set; }

    /// <summary>The moving arc. Defaults to the theme's primary text colour.</summary>
    public UiColor? Color { get; set; }

    /// <summary>The ring behind it. Defaults to the theme's border colour.</summary>
    public UiColor? TrackColor { get; set; }

    /// <summary>Back to the defaults above, for a spinner the runtime reuses.</summary>
    internal void ResetSpinner()
    {
        Size = ComponentSize.Md;
        Diameter = null;
        StrokeWidth = null;
        Color = null;
        TrackColor = null;
    }
}
