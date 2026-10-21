using Bibui.Core;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

/// <summary>One item of a <see cref="ToggleGroup"/>: what it reads, and the value it stands for.</summary>
public readonly record struct ToggleOption(string Label, string Value);

/// <summary>What a <see cref="ToggleGroup"/> built: its box, and the value picked this frame, if any.</summary>
public readonly record struct ToggleGroupWidget(Box Node, string? Picked);

/// <summary>
/// A segmented control: items side by side in one outline, the active one
/// raised. Each item is a box that takes its own press; the group reports
/// which was picked and the caller applies it.
/// </summary>
public static class ToggleGroupExtensions
{
    private const float Height = 36f;
    private const float PadX = 12f;     // Spacing.Md
    private const float Radius = 6f;    // Radii.Md

    /// <summary>
    /// Builds the group named <paramref name="id"/> showing <paramref name="items"/>,
    /// with the item whose value is <paramref name="value"/> active. Its items
    /// are named <c>{id}-{value}</c>. The group is 36 px tall at the theme's
    /// default density; set its size on
    /// what comes back to change that.
    /// </summary>
    public static ToggleGroupWidget ToggleGroup(this BibuiRuntime runtime, string id,
        IReadOnlyList<ToggleOption> items, string? value)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(items);

        var theme = runtime.Theme;
        float line = theme.Borders.Width;
        Box group = runtime.Box();
        group.ChildLayoutAxis = LayoutAxis.X;
        group.SizeX = SemanticSize.ChildrenSum();
        group.SizeY = SemanticSize.Pixels(theme.Density.Of(Height));
        group.Style.CornerRadius = Radius;
        group.Style.BorderColor = UiColor.Border;
        group.Style.BorderWidth = line;
        group.Style.Padding = EdgeInsets.All(line);   // the items sit inside the outline, not over it

        string? picked = null;
        using (runtime.PushParent(group))
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    Box divider = runtime.Box();
                    divider.SizeX = SemanticSize.Pixels(line);
                    divider.SizeY = SemanticSize.ParentPct(1f);
                    divider.Style.BackgroundColor = UiColor.Border;
                }

                bool active = items[i].Value == value;
                Box item = runtime.BoxFromId($"{id}-{items[i].Value}", InteractionKinds.LeftPress);
                if (runtime.InteractionsFor(item).Clicked) picked = items[i].Value;
                item.ChildLayoutAxis = LayoutAxis.Y;
                item.SizeX = SemanticSize.ChildrenSum();
                item.SizeY = SemanticSize.ParentPct(1f);
                item.Style.Padding = EdgeInsets.Symmetric(horizontal: PadX, vertical: 0f);
                item.Style.CornerRadius = i == 0 || i == items.Count - 1 ? Radius : 0f;
                item.Style.BackgroundColor = active ? UiColor.SurfaceRaised : (UiColor?)null;

                // Centred in the item, which has the group's height.
                using (runtime.PushParent(item))
                {
                    Arrange.Spring(item);
                    runtime.Text(items[i].Label, textColor: active ? TextColor.Primary : TextColor.Secondary);
                    Arrange.Spring(item);
                }
            }
        }
        return new ToggleGroupWidget(group, picked);
    }
}
