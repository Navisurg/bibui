using Bibui.Core;
using Bibui.Core.TextEdit;
using Bibui.Core.Theming;

namespace Bibui.Core.Runtime;

/// <summary>
/// A tooltip: one line of small text on a pill in the primary text colour,
/// built into the runtime's tooltip root, so it is drawn over everything and
/// takes nothing — the pointer goes through it to whatever is beneath.
///
/// It owns nothing and decides nothing: the caller builds it in a frame it
/// wants it shown, typically while the trigger it holds reports
/// <see cref="Interaction.Hovering"/>.
/// </summary>
public static class Tooltip
{
    private const float PadX = 8f;
    private const float PadY = 6f;
    private const float Radius = 4f;
    private const float Gap = 8f;

    /// <summary>
    /// Builds a tooltip reading <paramref name="text"/> and returns its pill.
    /// Its left edge at the middle of <paramref name="anchor"/>, where it was
    /// drawn last frame, above it <see cref="Gap"/> px clear, or below it when
    /// there is no room above in the window; with no anchor, by the mouse, where
    /// <see cref="BibuiRuntime.DefaultToolTipOffset"/> puts it.
    /// </summary>
    public static Box Build(BibuiRuntime runtime, ReadOnlySpan<char> text, Box? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        Box root = runtime.OpenToolTip();
        if (anchor is null) runtime.DefaultToolTipOffset(root);
        else
        {
            // Placed by its left edge, so its width is layout's to find (the pill
            // is its text's size). Above the anchor needs its height, one line
            // and its padding, known before layout, so it is in place on the
            // first frame it is shown.
            float height = runtime.Fonts.LineHeight(runtime.FontInScope(ComponentSize.Sm)) + 2f * PadY;
            float above = anchor.Rect.Y - Gap - height;
            float y = above >= 0f ? above : anchor.Rect.Y + anchor.Extent.Y + Gap;
            root.At(anchor.Rect.X + anchor.Extent.X / 2f, y);
        }

        Box pill;
        using (runtime.Parent(root))
        {
            pill = runtime.Box();
            pill.ChildLayoutAxis = LayoutAxis.X;
            pill.SizeX = pill.SizeY = SemanticSize.ChildrenSum();
            pill.Style.Padding = EdgeInsets.Symmetric(horizontal: PadX, vertical: PadY);
            pill.Style.CornerRadius = Radius;
            pill.Style.BackgroundColor = UiColor.TextPrimary;
            pill.Style.Shadow = runtime.Theme.Elevation.Level(ElevationTokens.Tooltip);
            using (runtime.Parent(pill))
                runtime.Text(text, ComponentSize.Sm, UiColor.Surface);
        }
        return pill;
    }
}
