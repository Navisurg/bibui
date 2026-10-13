using Bibui;
using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// A switch, described as boxes rather than drawn.
///
/// There is no switch renderer and no <c>Switch</c> node type: this builds the
/// same tree an app could compose by hand out of retained boxes and a
/// <see cref="Text"/>, and the ordinary painter draws it. One implementation,
/// so nothing can drift.
///
/// Styled to match what the old <c>SwitchRenderer</c> drew — a 44×24
/// full-radius track, Accent when on and Border when off, with a 20px knob
/// inset 2px from whichever end it sits at — with one thing a renderer could
/// not do: the knob <em>travels</em>.
///
/// Those are its proportions, not its sizes: everything is a fraction of the
/// height the caller gives it, 24px by default. A switch beside larger text,
/// or in a zoomed view, is the height the caller works out from that text or
/// that zoom. A renderer handed <c>Checked</c>, a bool,
/// can only draw the knob at one end or the other; the crossing is the
/// affordance, and it needs a value that remembers where it was last frame.
/// </summary>
public static class Switch
{
    /// <summary>
    /// Builds the switch named <paramref name="id"/> and returns it together
    /// with what happened to it this frame.
    ///
    /// <paramref name="on"/> comes in by value and is not written back: the
    /// caller applies <see cref="BibuiWidget.Clicked"/> to its own state — the
    /// click, not the press, so that pressing the switch and sliding off is a
    /// way to change your mind. No callbacks either: the track claims what it
    /// wants and the runtime answers.
    ///
    /// <paramref name="height"/> is the track's height in pixels, and the
    /// height of the row with its label; every other size is a fraction of it.
    /// </summary>
    public static BibuiWidget Build(BibuiRuntime runtime, string id, bool on, BibuiStr? label = null, bool disabled = false,
        float height = 24f)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);

        // With a label, the row is the outer box: acquired first, so the track
        // and the label are acquired under it.
        Box? row = label is { Length: > 0 } ? runtime.BoxFromId("row" + id) : null;
        using var inRow = row is not null ? runtime.PushParent(row) : default;
        if (row is not null)
        {
            row.ChildLayoutAxis = LayoutAxis.X;   // whatever the caller pushed
            row.SizeX = row.SizeY = SemanticSize.ChildrenSum();
        }

        // Retained, so HotT/ActiveT have somewhere to live. The reset clears
        // everything else, so it is described in full below.
        // Claimed at the door, so the box cannot exist without having said what
        // it takes. Disabled rather than None when it takes nothing: None reads
        // as "did not say" and is answered nothing, where Disabled is still
        // known to the runtime and swallows the press rather than letting it
        // fall through to whatever is beneath.
        Box track = runtime.BoxFromId(
            id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        track.ChildLayoutAxis = LayoutAxis.X;

        // Asked by the box, not by its name: there is no ordering to remember,
        // because holding it is proof it was described.
        Interaction interaction = runtime.InteractionsFor(track);

        // Drawn from the value handed in, not from a flip applied here. A caller
        // that acts on Clicked passes the new value next frame, and the crossing
        // eases toward it.
        float target = on ? 1f : 0f;

        // The state is a bool; what gets drawn is 0..1. The runtime keeps the
        // in-between, so this component owns no animation machinery of its own.
        // As fast as the theme moves a control between its states.
        float crossed = runtime.Animate(id, "checked", target, speed: runtime.Theme.Motion.Toggle);

        // The 44x24 track's proportions: 11/6 as wide as it is tall, a knob
        // 5/6 of the height, inset a twelfth of it from either end.
        float width = height * 11f / 6f, knobSide = height * 5f / 6f, inset = height / 12f;

        // Hover swells the knob by a twelfth of the height, a press by half
        // that, and the press suppresses the hover rather than competing with it.
        float hover = track.HotT * (1f - track.ActiveT);
        float grow = (hover / 12f + track.ActiveT / 24f) * height;
        float knobSize = knobSide + grow;
        float travel = width - knobSide - inset * 2f;

        (track.SizeX, track.SizeY) = (SemanticSize.Pixels(width), SemanticSize.Pixels(height));
        Style style = track.Style;   // cleared in place by BoxFromId
        style.Padding = new EdgeInsets((height - knobSize) / 2f, 0f, 0f, 0f);
        style.CornerRadius = FullRadius;
        style.BackgroundColor = disabled
            ? UiColor.SurfaceRaised
            : on ? UiColor.Accent : UiColor.Border;

        // Placed by a spacer before it: the track lays its children out along X
        // from its left edge, and the knob grows about its centre.
        using (runtime.PushParent(track)) runtime.Gap(inset + travel * crossed - grow / 2f);
        Box knob = runtime.BoxFromId("knob" + id, parent: track);
        (knob.SizeX, knob.SizeY) = (SemanticSize.Pixels(knobSize), SemanticSize.Pixels(knobSize));
        knob.Style.CornerRadius = FullRadius;
        knob.Style.BackgroundColor = on ? UiColor.AccentForeground : UiColor.TextPrimary;

        if (row is null) return new BibuiWidget(track, interaction);

        // The label in a cell the track's height, centred in it by a spring
        // above and below: the cell has a size, so the springs have a share.
        runtime.Gap(height / 3f);
        Box cell = runtime.Box();
        cell.ChildLayoutAxis = LayoutAxis.Y;
        cell.SizeX = SemanticSize.ChildrenSum();
        cell.SizeY = SemanticSize.Pixels(height);
        using (runtime.PushParent(cell))
        {
            Arrange.Spring(cell);
            runtime.Text(label?.AsMemory() ?? default, textColor: disabled ? TextColor.Disabled : TextColor.Primary);
            Arrange.Spring(cell);
        }
        return new BibuiWidget(row, interaction);
    }

    // Not read from the theme: Radii.Full is 9999 in every theme the repo
    // ships, so it is not actually themed. Colours are the
    // opposite — UiColor.Accent is a token resolved against the live theme at
    // paint time, so the theme reaches them without being passed around.
    private const float FullRadius = 9999f;
}
