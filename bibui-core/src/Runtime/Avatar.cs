using Bibui.Core;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

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
        circle.Sized(Size.Px(size), Size.Px(size));
        circle.Style.CornerRadius = size / 2f;
        circle.Style.BackgroundColor = fill ?? UiColor.Accent;

        runtime.BeginParent(circle);
        runtime.BeginCenterX();
        runtime.BeginCenterY();
        runtime.BeginFontFace(weight: FontWeight.Bold);
        runtime.Text(initials, fontSize, ink ?? UiColor.AccentForeground);
        runtime.EndFontFace();
        runtime.EndCenterY();
        runtime.EndCenterX();
        runtime.EndParent(circle);
        return circle;
    }
}
