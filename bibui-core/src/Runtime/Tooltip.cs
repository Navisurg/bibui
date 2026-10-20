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
    /// Centred above <paramref name="anchor"/>, where it was drawn last frame,
    /// <see cref="Gap"/> px clear of it; with no anchor, by the mouse, where
    /// <see cref="BibuiRuntime.DefaultToolTipOffset"/> puts it.
    /// </summary>
    public static Box Build(BibuiRuntime runtime, ReadOnlySpan<char> text, Box? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        Box root = runtime.OpenToolTip();
        if (anchor is null) runtime.DefaultToolTipOffset(root);
        else
        {
            // Its size is its text's and its padding, known before it is laid
            // out, so it can be centred on the first frame it is shown.
            Font font = runtime.FontInScope(ComponentSize.Sm);
            float width = runtime.Fonts.Measure(text, font) + 2f * PadX;
            float height = runtime.Fonts.LineHeight(font) + 2f * PadY;
            root.At(anchor.Rect.X + (anchor.Extent.X - width) / 2f, anchor.Rect.Y - height - Gap);
        }

        Box pill;
        using (runtime.PushParent(root))
        {
            pill = runtime.Box();
            pill.ChildLayoutAxis = LayoutAxis.X;
            pill.SizeX = pill.SizeY = SemanticSize.ChildrenSum();
            pill.Style.Padding = EdgeInsets.Symmetric(horizontal: PadX, vertical: PadY);
            pill.Style.CornerRadius = Radius;
            pill.Style.BackgroundColor = UiColor.TextPrimary;
            pill.Style.Shadow = runtime.Theme.Elevation.Level(ElevationTokens.Tooltip);
            using (runtime.PushParent(pill))
                runtime.Text(text, ComponentSize.Sm, UiColor.Background);
        }
        return pill;
    }
}
