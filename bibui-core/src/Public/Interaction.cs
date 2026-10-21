namespace Bibui.Core;

/// <summary>
/// What happened to one node this frame. The answer a build function gets when
/// it asks about a node it described — a Button being a box that asked this
/// and acted on <see cref="Pressed"/>, not a kind the runtime knows about.
/// </summary>
public record struct Interaction
{
    /// <summary>The pointer is over this node right now.</summary>
    public bool Hovering { get; set; }

    /// <summary>The left button went down over it this frame.</summary>
    public bool Pressed { get; set; }

    /// <summary>
    /// Which press of a streak the left press was: 1 for a single press, 2 for
    /// the second press of a double-click, 3 for a triple. Reported on the
    /// frame it is <see cref="Pressed"/> and on every frame it is held after
    /// (<see cref="Dragging"/>), so a double press dragged is still a double
    /// press. The window counts the streak; 0 when the left button is not
    /// pressed or held here.
    /// </summary>
    public int PressCount { get; set; }

    /// <summary>
    /// The left button came up while this node held it — wherever the pointer
    /// had got to. A release goes by who took the press, not by where it
    /// happened, so a drag let go anywhere still ends on the node that started
    /// it. Whether it counts as a click is a separate question.
    /// </summary>
    public bool Released { get; set; }

    public bool RightPressed { get; set; }
    public bool MiddlePressed { get; set; }

    /// <summary>The same as <see cref="Released"/>, for the other two buttons.</summary>
    public bool RightReleased { get; set; }
    public bool MiddleReleased { get; set; }

    /// <summary>
    /// 0 when this node was not clicked, 1 when it was. A click is pressed here
    /// <em>and</em> released here — narrower than <see cref="Released"/>, which
    /// is reported wherever the pointer had got to.
    ///
    /// <para>An int rather than a bool because it is meant to carry the click
    /// streak, but it never reports more than 1 today: the streak the window
    /// counts is measured press-to-press, knowing nothing of releases or of
    /// which box, so a press, a drag away, a release elsewhere and a second
    /// press counts as two though no click ever completed. Until that is fixed
    /// at the window, a double-click is two clicks and says so no other way.</para>
    /// </summary>
    public int ClickCount { get;  set; }

    /// <summary>Pressed on this node and released on it too.</summary>
    public bool Clicked => ClickCount > 0;

    /// <summary>
    /// Wheel travel over this node this frame, summed rather than
    /// last-one-wins: three notches in one frame are three notches.
    /// </summary>
    public float ScrollX { get; set; }
    public float ScrollY { get; set; }

    /// <summary>Where the pointer is, in logical pixels.</summary>
    public float X { get; set; }
    public float Y { get; set; }

    public KeyModifiers Modifiers { get; set; }

    /// <summary>
    /// The left button is still held and went down on this node — so this is
    /// the node's drag even once the pointer has travelled off it.
    /// </summary>
    public bool Dragging { get; set; }

    /// <summary>The same for the middle and right buttons.</summary>
    public bool RightDragging { get; set; }
    public bool MiddleDragging { get; set; }

    /// <summary>
    /// Where the drag's button went down — the one still held, or failing that
    /// the one that came up this frame. NaN when neither is true.
    ///
    /// <para>The release is included because a press and its release reach one
    /// frame whenever they are delivered faster than frames are drawn, which is
    /// what a real pointer does between renders. Reporting the travel only while
    /// the button is still down would leave it unreadable in exactly the frame
    /// that commits the gesture.</para>
    /// </summary>
    public float DragStartX { get; set; }
    public float DragStartY { get; set; }

    /// <summary>
    /// How far the pointer has travelled since. Zero when there is no drag to
    /// measure — neither held now nor released this frame.
    /// </summary>
    public float DragDeltaX { get; set; }
    public float DragDeltaY { get; set; }

    /// <summary>Nothing at all happened to this node.</summary>
    public bool None => !Hovering && !Pressed && !Released && !RightPressed
                     && !MiddlePressed && !RightReleased && !MiddleReleased
                     && !Dragging && !RightDragging && !MiddleDragging
                     && ScrollX == 0f && ScrollY == 0f;
}
