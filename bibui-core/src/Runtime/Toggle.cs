using Bibui.Core;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

/// <summary>
/// A toggle: a button that stays pressed. Outlined when off, filled with the
/// raised surface when on, an optional icon before its label. It takes the
/// press and reports the click; whether it is on is the caller's, passed in
/// to be drawn and flipped by the caller on <see cref="BibuiWidget.Clicked"/>.
/// </summary>
public static class ToggleExtensions
{
    private const float Height = 36f;
    private const float PadX = 12f;     // Spacing.Md
    private const float Radius = 6f;    // Radii.Md
    private const float IconSize = 16f;
    private const float IconGap = 6f;

    /// <summary>
    /// Builds the toggle named <paramref name="id"/>, drawn on when
    /// <paramref name="on"/>, and returns it with what happened to it this
    /// frame. <paramref name="icon"/> names an icon drawn before the label.
    /// Disabled, it swallows the press rather than reporting it.
    /// </summary>
    public static BibuiWidget Toggle(this BibuiRuntime runtime, string id, bool on, ReadOnlySpan<char> label = default,
        string? icon = null, bool disabled = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);

        Box box = runtime.BoxFromId(id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        Interaction interaction = runtime.InteractionsFor(box);
        box.ChildLayoutAxis = LayoutAxis.X;
        box.SizeX = SemanticSize.ChildrenSum();
        var theme = runtime.Theme;
        box.SizeY = SemanticSize.Pixels(theme.Density.Of(Height));

        Style style = box.Style;
        style.CornerRadius = Radius;
        style.BorderWidth = theme.Borders.Width;
        style.Padding = EdgeInsets.Symmetric(horizontal: PadX, vertical: 0f);
        if (on) style.BackgroundColor = UiColor.SurfaceRaised;
        else style.BorderColor = UiColor.Border;
        if (!disabled) style.HoverBackgroundColor = on ? UiColor.SurfaceRaisedHover : UiColor.SurfaceRaised;

        UiColor ink = disabled ? TextColor.Disabled : TextColor.Primary;
        bool hasLabel = !label.IsEmpty;
        using (runtime.PushParent(box))
        {
            if (icon is not null)
            {
                using (runtime.PushParent(Cell(runtime)))
                {
                    Arrange.Spring(runtime.ParentInScope);
                    runtime.Icon(icon, IconSize).Color = ink;
                    Arrange.Spring(runtime.ParentInScope);
                }
                if (hasLabel) runtime.Gap(IconGap);
            }
            if (hasLabel)
            {
                using (runtime.PushParent(Cell(runtime)))
                {
                    Arrange.Spring(runtime.ParentInScope);
                    runtime.Text(label, textColor: ink);
                    Arrange.Spring(runtime.ParentInScope);
                }
            }
        }
        return new BibuiWidget(box, interaction);
    }

    // A column as tall as the toggle and as wide as what it holds.
    private static Box Cell(BibuiRuntime runtime)
    {
        Box cell = runtime.Box();
        cell.ChildLayoutAxis = LayoutAxis.Y;
        cell.SizeX = SemanticSize.ChildrenSum();
        cell.SizeY = SemanticSize.ParentPct(1f);
        return cell;
    }
}
