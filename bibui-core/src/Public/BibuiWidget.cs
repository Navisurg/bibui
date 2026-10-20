namespace Bibui.Core;

/// <summary>
/// A component and what happened to it this frame: the node to place, and the
/// <see cref="Interaction"/> the caller acts on.
///
/// A build function returns this instead of writing through a <c>ref</c>. A
/// pointer parameter is for state the component must <em>maintain</em> across
/// frames — a text cursor, an edit buffer. Everything else returns what
/// happened and lets the caller act on it, which is why there is no toggle
/// component: a toggle is a button whose press the caller applies to its own
/// bool.
///
/// A switch's bool is not state the switch maintains. What it does maintain —
/// hotness, activeness, where the knob is mid-crossing — already lives on the
/// retained box and in <c>runtime.Animate</c>. So the bool comes in by value and
/// the press goes back as a signal, and the caller keeps ownership of its own
/// state rather than discovering after the fact that something changed it.
/// </summary>
public readonly record struct BibuiWidget(UiNode Node, Interaction Interaction)
{
    /// <summary>
    /// Pressed on it and released on it. What a control acts on: a press that
    /// wanders off and lets go elsewhere is how someone takes it back, and
    /// acting on the press alone gives them no way to.
    /// </summary>
    public bool Clicked => Interaction.Clicked;

    /// <summary>
    /// The left button went down over it this frame. For the press-down look,
    /// not for the action — see <see cref="Clicked"/>.
    /// </summary>
    public bool Pressed => Interaction.Pressed;

    /// <summary>The pointer is over it right now.</summary>
    public bool Hovering => Interaction.Hovering;

    /// <summary>
    /// So a built component can be placed straight into a container's children
    /// without reaching past the signal to get at the node.
    /// </summary>
    public static implicit operator UiNode(BibuiWidget widget) => widget.Node;
}
