using Bibui;
using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// An avatar: a circle in the accent colour with the initials centred on it,
/// bold. It claims nothing; it only shows who.
/// </summary>
public static class Avatar
{
    /// <summary>The circle's diameter, unless the caller says another.</summary>
    public const float DefaultSize = 40f;

    /// <summary>
    /// Builds an avatar reading <paramref name="initials"/> under the parent
    /// in scope, <paramref name="size"/> across, and returns its box.
    /// <paramref name="fill"/> and <paramref name="ink"/> are the circle and
    /// the initials, the accent and the text on it unless said otherwise.
    /// </summary>
    public static Box Build(BibuiRuntime runtime, ReadOnlySpan<char> initials, float size = DefaultSize,
        UiColor? fill = null, UiColor? ink = null, ComponentSize fontSize = ComponentSize.Lg)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        Box circle = runtime.Box();
        circle.ChildLayoutAxis = LayoutAxis.X;
        circle.Px(size, size);
        circle.Style.CornerRadius = size / 2f;
        circle.Style.BackgroundColor = fill ?? UiColor.Accent;

        // Centred both ways: a spring either side of a column with a spring
        // above and below the initials.
        using (runtime.PushParent(circle))
        {
            Arrange.Spring(circle);
            Box column = runtime.Box();
            column.ChildLayoutAxis = LayoutAxis.Y;
            column.SizeX = SemanticSize.ChildrenSum();
            column.SizeY = SemanticSize.ParentPct(1f);
            using (runtime.PushParent(column))
            {
                Arrange.Spring(column);
                using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(initials, fontSize, ink ?? UiColor.AccentForeground);
                Arrange.Spring(column);
            }
            Arrange.Spring(circle);
        }
        return circle;
    }
}
