using Bibui;
using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// A button, described as a box rather than drawn.
///
/// There is no button renderer here and no <c>Button</c> node type: this builds
/// a tree an app could compose by hand out of <see cref="Box"/> and
/// <see cref="Text"/>, and the ordinary painter draws it. One implementation,
/// so nothing can drift.
///
/// What it takes it says at the door, and the caller asks about what it holds —
/// no callback. The label comes in by value and <see cref="BibuiWidget.Clicked"/>
/// goes back, so the caller decides what a press means.
/// </summary>
public static class Button
{
    // Not read from the theme, the way Switch does not read its radius: these
    // are Spacing.Sm and Spacing.Lg in every theme the repo ships, so neither is
    // actually themed. Colours are the opposite — they are tokens resolved at
    // paint time, so the theme reaches them without being passed around.
    private const float RadiusMd = 8f;
    private const float PadGhost = 8f;
    private const float PadSolid = 16f;
    private const float PadY = 8f;

    /// <summary>
    /// Builds the button named <paramref name="id"/> and reports what happened
    /// to it this frame.
    ///
    /// <paramref name="ghost"/> gives up the resting fill and edge and keeps
    /// only the hover: an explicit hover colour, because a treatment the theme
    /// supplies needs a resting colour of the theme's to blend from, and a
    /// ghost has none.
    ///
    /// <paramref name="disabled"/> is a claim, not the absence of one. It
    /// reports nothing and swallows the press, so it does not fall through to
    /// whatever is beneath.
    /// </summary>
    public static BibuiWidget Build(
        BibuiRuntime runtime,
        string id,
        BibuiStr label,
        bool ghost = false,
        bool disabled = false,
        ComponentSize size = ComponentSize.Md)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(label);

        Box box = runtime.BoxFromId(
            id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);

        Interaction interaction = runtime.InteractionsFor(box);

        // No height. The label's own line box already scales with the size it
        // was given, so the padding alone sizes the button, without heights that
        // would have to be kept in step with the type scale.
        float padX = ghost ? PadGhost : PadSolid;
        var theme = runtime.Theme;

        Style style = box.Style;   // cleared in place by BoxFromId
        style.CornerRadius = RadiusMd;
        style.Padding = EdgeInsets.Symmetric(horizontal: padX, vertical: theme.Density.Of(PadY));
        style.BorderWidth = theme.Borders.Width;

        if (ghost)
        {
                style.HoverBackgroundColor = UiColor.SurfaceRaised;
        }
        else
        {
            // Every colour is the theme's, so the flags name them rather than
            // spelling four out: the raised surface, the edge, and the hover
            // and held treatments over both.
            style.Draw = DrawFlags.Border | DrawFlags.Background
                | DrawFlags.HotEffects | DrawFlags.ActiveEffects;
        }

        // As large as the label and its padding; a caller that sizes it
        // otherwise says where the label sits with the padding.
        box.ChildLayoutAxis = LayoutAxis.X;
        box.SizeX = box.SizeY = SemanticSize.ChildrenSum();
        using (runtime.PushParent(box))
            runtime.Text(label.AsMemory(), size, disabled ? TextColor.Disabled : TextColor.Primary);

        return new BibuiWidget(box, interaction);
    }
}
