using Bibui;
using Bibui.TextEdit;

namespace Bibui.Runtime;

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
        using (runtime.PushParent(row))
        {
            Box square = runtime.Box();
            square.Px(Square, Square);
            square.Style.CornerRadius = Radius;
            square.Style.BorderWidth = runtime.Theme.Borders.Width;
            square.Style.BackgroundColor = filled ? UiColor.Accent : UiColor.Surface;
            square.Style.BorderColor = disabled ? UiColor.TextDisabled : filled ? UiColor.Accent : UiColor.Border;

            if (filled)
            {
                // Centred in the square, which has a size: a spring either side on both axes.
                using (runtime.PushParent(square))
                {
                    square.ChildLayoutAxis = LayoutAxis.X;
                    Arrange.Spring(square);
                    Box column = runtime.Box();
                    column.ChildLayoutAxis = LayoutAxis.Y;
                    column.SizeX = SemanticSize.ChildrenSum();
                    column.SizeY = SemanticSize.ParentPct(1f);
                    Arrange.Spring(square);
                    using (runtime.PushParent(column))
                    {
                        Arrange.Spring(column);
                        if (indeterminate)
                        {
                            Box dash = runtime.Box();
                            dash.Px(DashWidth, DashHeight);
                            dash.Style.CornerRadius = 1f;
                            dash.Style.BackgroundColor = UiColor.AccentForeground;
                        }
                        else
                        {
                            Icon check = runtime.Icon("check", size: Mark);
                            check.Color = UiColor.AccentForeground;
                        }
                        Arrange.Spring(column);
                    }
                }
            }

            if (!label.IsEmpty)
            {
                runtime.Gap(Gap);
                runtime.Text(label, textColor: disabled ? TextColor.Disabled : TextColor.Primary);
            }
        }
        return new BibuiWidget(row, interaction);
    }
}
