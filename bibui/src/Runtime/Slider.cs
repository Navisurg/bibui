using Bibui;
using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// A slider and what happened to it this frame: the node to place, the value
/// under the pointer while the track is pressed or dragged — the value it was
/// given otherwise — and the interaction. An untouched slider hands back the
/// value it was given, so applying <see cref="Value"/> every frame is a no-op;
/// a caller for whom every intermediate value is expensive waits for
/// <see cref="Interaction.Released"/> instead.
/// </summary>
public readonly record struct SliderWidget(UiNode Node, float Value, Interaction Interaction)
{
    public static implicit operator UiNode(SliderWidget widget) => widget.Node;

    /// <summary>So it goes straight into <c>Row(...)</c> or <c>Column(...)</c>.</summary>
    public static implicit operator Arrange.Item(SliderWidget widget) => widget.Node;
}

/// <summary>
/// A slider: an optional label above a track, the track a retained box that
/// claims the press, and inside it a groove, the filled share and the thumb.
/// The component owns no state: it is given the value and reports the one
/// under the pointer.
/// </summary>
public static class Slider
{
    /// <summary>The thumb's diameter, and the track's height.</summary>
    public const float Height = 16f;

    private const float GrooveHeight = 4f;
    private const float LabelGap = 4f;

    /// <summary>
    /// Builds the slider named <paramref name="id"/> under the parent in scope,
    /// as wide as all of it unless the caller sizes the node otherwise.
    /// <paramref name="step"/> 0 is continuous.
    /// </summary>
    public static SliderWidget Build(BibuiRuntime runtime, string id, float value,
        float min = 0f, float max = 1f, float step = 0f, BibuiStr? label = null, bool disabled = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);

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

        var track = runtime.BoxFromId(id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        var interaction = runtime.InteractionsFor(track);
        track.SizeX = SemanticSize.ParentPct(1f);
        track.SizeY = SemanticSize.Pixels(Height);

        // Measured against the track as it was drawn last frame: the rect the
        // press landed on. An undrawn track has nothing to measure.
        float live = (interaction.Pressed || interaction.Dragging) && track.Extent.X > 0f
            ? ValueAt(interaction.X, track.Rect.X, track.Extent.X, min, max, step)
            : value;
        float t = max > min ? Math.Clamp((live - min) / (max - min), 0f, 1f) : 0f;

        using (runtime.PushParent(track))
        {
            float grooveTop = (Height - GrooveHeight) / 2f;
            Pill(runtime, SemanticSize.ParentPct(1f), GrooveHeight, UiColor.SurfaceRaised).At(0f, grooveTop);
            Pill(runtime, SemanticSize.ParentPct(t), GrooveHeight, disabled ? UiColor.TextDisabled : UiColor.Accent).At(0f, grooveTop);

            // Centred on the value, so it overhangs the ends by half itself.
            var thumb = Pill(runtime, SemanticSize.Pixels(Height), Height, disabled ? UiColor.TextDisabled : UiColor.TextPrimary);
            thumb.Style.BorderColor = UiColor.Border;
            thumb.Style.BorderWidth = runtime.Theme.Borders.Width;
            thumb.At(track.Extent.X * t - Height / 2f, 0f);
        }

        return new SliderWidget(column ?? (UiNode)track, live, interaction);
    }

    private static Box Pill(BibuiRuntime runtime, SemanticSize width, float height, UiColor color)
    {
        var pill = runtime.Box();
        pill.SizeX = width;
        pill.SizeY = SemanticSize.Pixels(height);
        pill.Style.BackgroundColor = color;
        pill.Style.CornerRadius = height / 2f;
        return pill;
    }

    /// <summary>Where along a track the pointer is, in the value's own units, snapped to the step.</summary>
    internal static float ValueAt(float pointerX, float trackX, float trackWidth, float min, float max, float step)
    {
        float span = max - min;
        if (trackWidth <= 0f || span <= 0f) return min;
        float raw = min + Math.Clamp((pointerX - trackX) / trackWidth, 0f, 1f) * span;
        if (step > 0f) raw = min + MathF.Round((raw - min) / step) * step;
        return Math.Clamp(raw, min, max);
    }
}
