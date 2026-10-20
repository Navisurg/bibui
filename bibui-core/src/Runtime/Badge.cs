using Bibui.Core;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

/// <summary>
/// A badge, described as a box holding a text: a pill as large as its label
/// plus padding. It claims nothing and is found by nothing, so it is a keyless
/// box from the runtime, linked under the parent in scope.
/// </summary>
public static class Badge
{
    // Spacing.Md across; 4 above and below makes the small label's line box
    // about 24 px.
    private const float PadX = 12f;
    private const float PadY = 4f;
    private const float DotGap = 6f;
    private const string Bullet = "\u2022";

    /// <summary>
    /// Builds a badge reading <paramref name="label"/> under the parent in
    /// scope and returns its box. A leading dot is a bullet glyph in the
    /// variant's accent, set in the label's own font so the two share a line.
    /// </summary>
    public static Box Build(BibuiRuntime runtime, ReadOnlySpan<char> label,
        BadgeVariant variant = BadgeVariant.Default, bool leadingDot = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var (fill, ink, edge, dot) = Colours(variant);

        Box pill = runtime.Box();
        pill.ChildLayoutAxis = LayoutAxis.X;
        pill.SizeX = pill.SizeY = SemanticSize.ChildrenSum();
        Style style = pill.Style;
        style.Padding = EdgeInsets.Symmetric(horizontal: PadX, vertical: PadY);
        style.CornerRadius = 9999f;
        style.BackgroundColor = variant == BadgeVariant.Outline ? (UiColor?)null : fill;
        if (variant == BadgeVariant.Outline)
        {
            style.BorderColor = edge;
            style.BorderWidth = runtime.Theme.Borders.Width;
        }

        using (runtime.PushParent(pill))
        {
            if (leadingDot)
            {
                runtime.Text(Bullet, ComponentSize.Sm, dot);
                runtime.Gap(DotGap);
            }
            runtime.Text(label, ComponentSize.Sm, ink);
        }
        return pill;
    }

    private static (UiColor Fill, UiColor Ink, UiColor Edge, UiColor Dot) Colours(BadgeVariant variant) => variant switch
    {
        BadgeVariant.Primary     => (UiColor.Accent, UiColor.AccentForeground, UiColor.Accent, UiColor.Accent),
        BadgeVariant.Success     => (UiColor.SuccessBg, UiColor.Success, UiColor.Success, UiColor.Success),
        BadgeVariant.Destructive => (UiColor.Destructive, UiColor.DestructiveForeground, UiColor.Destructive, UiColor.Destructive),
        BadgeVariant.Outline     => (UiColor.Surface, UiColor.TextPrimary, UiColor.Border, UiColor.TextSecondary),
        _                        => (UiColor.SurfaceRaised, UiColor.TextPrimary, UiColor.Border, UiColor.TextSecondary),
    };
}
