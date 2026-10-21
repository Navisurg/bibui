using Bibui.Core;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

/// <summary>
/// A checkbox, described as boxes: an 18 px square holding a check or a dash,
/// and a label beside it. The row claims the press, so the label is as much
/// a target as the square.
/// </summary>
public static class CheckboxExtensions
{
    private const float Square = 18f;
    private const float Gap = 12f;      // Spacing.Md
    private const float Radius = 4f;    // Radii.Sm
    private const float Mark = 14f;
    private const float DashWidth = 10f;
    private const float DashHeight = 2f;

    /// <summary>
    /// Builds the checkbox named <paramref name="id"/> and returns it with
    /// what happened to it this frame. <paramref name="on"/> is drawn, not
    /// kept: the caller applies <see cref="BibuiWidget.Clicked"/> to its own
    /// state. <paramref name="indeterminate"/> draws a dash and wins over
    /// <paramref name="on"/>.
    /// </summary>
    public static BibuiWidget Checkbox(this BibuiRuntime runtime, string id, bool on, ReadOnlySpan<char> label = default,
        bool disabled = false, bool indeterminate = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);

        Box row = runtime.BoxFromId(id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        Interaction interaction = runtime.InteractionsFor(row);
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = row.SizeY = SemanticSize.ChildrenSum();

        bool filled = on || indeterminate;
        runtime.BeginParent(row);
        Box square = runtime.Box();
        square.Sized(Size.Px(Square), Size.Px(Square));
        square.Style.CornerRadius = Radius;
        square.Style.BorderWidth = runtime.Theme.Borders.Width;
        square.Style.BackgroundColor = filled ? UiColor.Accent : UiColor.Surface;
        square.Style.BorderColor = disabled ? UiColor.TextDisabled : filled ? UiColor.Accent : UiColor.Border;

        if (filled)
        {
            // Centred in the square, which has a size, both ways.
            square.ChildLayoutAxis = LayoutAxis.X;
            runtime.BeginParent(square);
            runtime.BeginCenterX();
            runtime.BeginCenterY();
            if (indeterminate)
            {
                Box dash = runtime.Box();
                dash.Sized(Size.Px(DashWidth), Size.Px(DashHeight));
                dash.Style.CornerRadius = 1f;
                dash.Style.BackgroundColor = UiColor.AccentForeground;
            }
            else
            {
                Icon check = runtime.Icon("check", size: Mark);
                check.Color = UiColor.AccentForeground;
            }
            runtime.EndCenterY();
            runtime.EndCenterX();
            runtime.EndParent(square);
        }

        if (!label.IsEmpty)
        {
            runtime.Gap(Gap);
            runtime.Text(label, textColor: disabled ? TextColor.Disabled : TextColor.Primary);
        }
        runtime.EndParent(row);
        return new BibuiWidget(row, interaction);
    }
}
