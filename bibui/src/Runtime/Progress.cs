using Bibui;
using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// A progress bar: an optional label above a track, the track a pill in the
/// raised surface colour holding a pill in the accent as long as the share
/// done. It claims nothing; the value is the caller's, given to draw.
/// </summary>
public static class Progress
{
    /// <summary>The track's height, unless the caller says another.</summary>
    public const float DefaultThickness = 8f;

    private const float LabelGap = 4f;

    /// <summary>
    /// Builds a progress bar showing <paramref name="value"/> of
    /// <paramref name="max"/> under the parent in scope, as wide as all of
    /// it unless the caller sizes the node otherwise, and returns its node:
    /// the column holding the label and the track, or the track alone.
    /// A value outside 0..max is drawn clamped to it.
    /// </summary>
    public static Box Build(BibuiRuntime runtime, float value, float max = 100f, BibuiStr? label = null,
        float thickness = DefaultThickness, UiColor? fill = null, UiColor? track = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        Box? column = label is { Length: > 0 } ? runtime.Box() : null;
        using var inColumn = column is not null ? runtime.PushParent(column) : default;
        if (column is not null)
        {
            column.ChildLayoutAxis = LayoutAxis.Y;
            column.SizeX = SemanticSize.ParentPct(1f);
            column.SizeY = SemanticSize.ChildrenSum();
            runtime.Text(label!.AsMemory(), ComponentSize.Sm, TextColor.Secondary);
            runtime.Gap(LabelGap);
        }

        float height = MathF.Max(1f, thickness);
        float share = Share(value, max);

        Box groove = runtime.Box();
        groove.SizeX = SemanticSize.ParentPct(1f);
        groove.SizeY = SemanticSize.Pixels(height);
        groove.Style.BackgroundColor = track ?? UiColor.SurfaceRaised;
        groove.Style.CornerRadius = height / 2f;

        if (share > 0f)
            using (runtime.PushParent(groove))
            {
                Box done = runtime.Box();
                done.SizeX = SemanticSize.ParentPct(share);
                done.SizeY = SemanticSize.Pixels(height);
                done.Style.BackgroundColor = fill ?? UiColor.Accent;
                done.Style.CornerRadius = height / 2f;
                done.At(0f, 0f);
            }

        return column ?? groove;
    }

    /// <summary>The share of the track a value fills: <paramref name="value"/> over <paramref name="max"/>, in 0..1.</summary>
    internal static float Share(float value, float max)
        => max > 0f && !float.IsNaN(value) ? Math.Clamp(value / max, 0f, 1f) : 0f;
}
