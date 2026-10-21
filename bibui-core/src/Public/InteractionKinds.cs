namespace Bibui.Core;

/// <summary>
/// What a box says it may take. A claim decides both what its
/// <see cref="Interaction"/> reports and what asking for one consumes — the
/// same question, since a node acting on what it never claimed is a node two
/// handlers act on.
///
/// No claim for dragging: a drag is what a press becomes, so taking the press
/// is what makes it yours.
///
/// No claim for hovering either. A box that takes a press is one the pointer
/// can be over, so hotness follows from the press claim rather than being asked
/// for separately — one claim, and both the hover and the press fall out of it.
/// </summary>
[Flags]
public enum InteractionKinds
{
    None = 0,

    LeftPress = 1 << 0,
    RightPress = 1 << 1,
    MiddlePress = 1 << 2,
    /// <summary>
    /// The wheel moves this box's view along X: its travel is added to
    /// <see cref="Box.TargetViewOffset"/>. A box claiming one axis takes the
    /// other's travel onto it when the event has none on its own.
    /// </summary>
    ViewScrollX = 1 << 3,

    /// <summary>The same as <see cref="ViewScrollX"/>, along Y.</summary>
    ViewScrollY = 1 << 4,

    /// <summary>
    /// Takes nothing, deliberately — and says so, which <see cref="None"/> does
    /// not. A box that never mentions claims is not asking to be interacted
    /// with at all, and takes nothing for that reason; one that claims this is
    /// asking to be there and take nothing, which is a different thing.
    ///
    /// The difference is what it does to the events. Both report nothing, but a
    /// box claiming this is still known to the runtime, so a control can be
    /// disabled without the press falling through to whatever is beneath it.
    /// </summary>
    Disabled = 1 << 5,

    /// <summary>
    /// Keeps <see cref="Box.TargetViewOffset"/> within the content along X:
    /// between 0 and how far <see cref="Box.ContentSize"/> runs past
    /// <see cref="Box.Size"/>, so the content can never be moved off its box.
    /// Takes nothing; pairs with whatever moves the offset, such as
    /// <see cref="ViewScrollX"/>.
    ///
    /// <para>Keep in mind: it is still a claim, so a box carrying only this is
    /// not <see cref="None"/>. It lives here because it has to be known when the
    /// box asks, and only what <c>BoxFromId</c> was given is.</para>
    /// </summary>
    ClampX = 1 << 6,

    /// <summary>The same as <see cref="ClampX"/>, along Y.</summary>
    ClampY = 1 << 7,

    /// <summary>
    /// Children are clipped to this box: painting, and the rects of boxes
    /// acquired under it with <c>BibuiRuntime.BeginParent</c> before they are tested.
    /// Takes nothing itself, but like <see cref="ClampX"/> counts as interactive.
    /// </summary>
    Clip = 1 << 8,

    /// <summary>
    /// The wheel is reported to this box, in <see cref="Interaction.ScrollX"/>
    /// and <see cref="Interaction.ScrollY"/>, and moves nothing: for a box that
    /// spends it on something else, such as a zoom.
    /// </summary>
    Scroll = 1 << 9,

    Press = LeftPress | RightPress | MiddlePress,
    ViewScroll = ViewScrollX | ViewScrollY,
    Clamp = ClampX | ClampY,
    All = Press | ViewScroll | Scroll,
}
