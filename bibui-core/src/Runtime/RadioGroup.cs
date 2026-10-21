using Bibui.Core;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

/// <summary>One option of a <see cref="RadioGroup"/>: what it reads, the value it stands for, and whether it takes a press.</summary>
public readonly record struct RadioOption(string Label, string Value, bool Disabled = false);

/// <summary>What a <see cref="RadioGroup"/> built: its box, and the value picked this frame, if any.</summary>
public readonly record struct RadioGroupWidget(Box Node, string? Picked);

/// <summary>
/// A radio group, described as boxes: a column of rows, each a 16 px ring —
/// a dot in it when it is the one chosen — and a label beside it. Each row
/// takes its own press, so the label is as much a target as the ring; the
/// group reports which value was picked and the caller applies it.
/// </summary>
public static class RadioGroupExtensions
{
    private const float RowHeight = 32f;
    private const float RowGap = 4f;
    private const float Ring = 16f;
    private const float Dot = 8f;
    private const float LabelGap = 8f;
    private const float FullRadius = 9999f;

    /// <summary>
    /// Builds the group named <paramref name="id"/> showing <paramref name="items"/>,
    /// with the one whose value is <paramref name="value"/> chosen. Its rows are
    /// named <c>{id}-{value}</c>. A disabled option — or every option, when
    /// <paramref name="disabled"/> — swallows its press rather than reporting it.
    /// <paramref name="value"/> is drawn, not kept: the caller applies
    /// <see cref="RadioGroupWidget.Picked"/> to its own state.
    /// </summary>
    public static RadioGroupWidget RadioGroup(this BibuiRuntime runtime, string id,
        IReadOnlyList<RadioOption> items, string? value, bool disabled = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(items);

        var theme = runtime.Theme;
        Box group = runtime.Box();
        group.ChildLayoutAxis = LayoutAxis.Y;
        group.SizeX = group.SizeY = SemanticSize.ChildrenSum();

        string? picked = null;
        using (runtime.PushParent(group))
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) runtime.Gap(RowGap);
                var item = items[i];
                bool off = disabled || item.Disabled;
                bool chosen = item.Value == value;

                Box row = runtime.BoxFromId($"{id}-{item.Value}",
                    off ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
                if (runtime.InteractionsFor(row).Clicked && !chosen) picked = item.Value;
                row.ChildLayoutAxis = LayoutAxis.X;
                row.SizeX = SemanticSize.ChildrenSum();
                row.SizeY = SemanticSize.Pixels(theme.Density.Of(RowHeight));

                runtime.BeginParent(row);

                // The ring, centred down the row.
                runtime.BeginCenterAcross();
                Box ring = runtime.Box();
                ring.ChildLayoutAxis = LayoutAxis.X;
                ring.Sized(Size.Px(Ring), Size.Px(Ring));
                ring.Style.CornerRadius = FullRadius;
                ring.Style.BorderWidth = chosen ? theme.Borders.Strong : theme.Borders.Width;
                ring.Style.BorderColor = off ? UiColor.TextDisabled : chosen ? UiColor.Accent : UiColor.Border;
                ring.Style.Padding = EdgeInsets.All((Ring - Dot) / 2f);
                if (chosen)
                {
                    runtime.BeginParent(ring);
                    Box dot = runtime.Box();
                    dot.Sized(Size.Px(Dot), Size.Px(Dot));
                    dot.Style.CornerRadius = FullRadius;
                    dot.Style.BackgroundColor = off ? UiColor.TextDisabled : UiColor.Accent;
                    runtime.EndParent(ring);
                }
                runtime.EndCenterAcross();

                if (item.Label.Length > 0)
                {
                    runtime.Gap(LabelGap);
                    runtime.BeginCenterAcross();
                    runtime.Text(item.Label, textColor: off ? TextColor.Disabled : TextColor.Primary);
                    runtime.EndCenterAcross();
                }
                runtime.EndParent(row);
            }
        }
        return new RadioGroupWidget(group, picked);
    }
}
