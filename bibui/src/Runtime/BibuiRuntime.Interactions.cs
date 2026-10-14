using System.Numerics;
using Bibui.TextEdit;

namespace Bibui.Runtime;

public sealed partial class BibuiRuntime
{
    private const int Left = 0, Middle = 1, Right = 2;
    private string? _hotId;
    private readonly string?[] _activeId = new string?[3];
    private readonly Vector2[] _pressPos = new Vector2[3];
    private int _leftPressCount;   // the streak the left press being held carried

    private const InteractionKinds Pressable = InteractionKinds.LeftPress
        | InteractionKinds.MiddlePress
        | InteractionKinds.RightPress;


    /// <summary>
    /// What happened to <paramref name="box"/> this frame, taking what it claimed.
    ///
    /// The single place an interaction is decided: the rect test, the claim
    /// check, the hot id and the hovering flag are one block, so
    /// <see cref="Box.HotT"/>, <see cref="Box.ActiveT"/>, <c>Hovering</c> and
    /// <c>Dragging</c> are all read off the same two slots and cannot disagree.
    ///
    /// <para>It takes the box rather than its name so that describing has
    /// demonstrably happened: the only way to hold one is
    /// <see cref="BoxFromId"/>, which clears it and takes its claims in the
    /// same call. Asking by name could be done before anything described the
    /// box that frame, and would then answer from whatever the last frame left
    /// behind.</para>
    ///
    /// <para>Claims are read off the instance — this frame's. Bounds come from
    /// the last painted frame, so a box's first frame is not interactive.
    /// Where two boxes cover the same point the first to ask has it: asking
    /// consumes the event, and that is how z-order is expressed.</para>
    /// </summary>
    public Interaction InteractionsFor(Box box)
    {
        ArgumentNullException.ThrowIfNull(box);
        Interaction interaction = default;
        if (box.Id is not { } id) return interaction;

        // Claims off the instance handed in, geometry off the last painted
        // frame. For a retained box they are the same object; for one built
        // fresh each frame they are not, and only the older of the two has
        // ever been given a rect.
        Box geometry = PeekBox(id) ?? box;

        // Cut the rect to every ancestor that claims Clip. Links are this
        // frame's, rects last frame's.
        Vector2 rectMin = geometry.Rect;
        Vector2 rectMax = geometry.Rect + geometry.Extent;
        for (UiNode? parent = box.Parent; parent is not null; parent = parent.Parent == parent ? null : parent.Parent)
        {
            if (parent is not Box { Claims: var parentClaims } || (parentClaims & InteractionKinds.Clip) == 0) continue;
            rectMin = Vector2.Max(rectMin, parent.Rect);
            rectMax = Vector2.Min(rectMax, parent.Rect + parent.Extent);
        }
        Vector2 rect = rectMin;
        Vector2 size = Vector2.Max(Vector2.Zero, rectMax - rectMin);

        // A box drawn somewhere other than where it was laid out — moved,
        // scaled or turned by its own Style or an ancestor's — is hit where it
        // is drawn: the pointer is taken back into layout space, through the
        // transform last frame painted it with, and tested there.
        // (A static call with its arguments spelled out rather than a local
        // function: one capturing these locals costs an allocation per ask.)
        Dictionary<string, Drawn>? transforms;
        lock (_syncRoot) transforms = _pointerTransforms;

        InteractionKinds claims = box.Claims;
        if (claims == InteractionKinds.Disabled)
        {
            // Reports nothing, but is still in the way. Declining to act on a
            // press is not the same as not being there: without this the press
            // stays in the queue and the panel underneath, asked next, acts on
            // a click that landed on a disabled control.
            //
            // The wheel is left alone. A disabled button inside a scroll area
            // is a reason not to press it, not a reason the area cannot
            // scroll under it.
            foreach (var handle in _frameEvents)
            {
                if (handle.Consumed) continue;

                BibUiEvent blocked = handle.Event;
                if (blocked.Kind is not (BibUiEventKind.MouseDown or BibUiEventKind.MouseUp)) continue;
                Vector2 at = new(blocked.X, blocked.Y);
                if (!Hit(at, geometry, rect, size, transforms) || IsUnderDropDown(box, at)) continue;

                // A release belonging to a gesture someone else started is not
                // this box's to swallow: it has to reach its owner, or that
                // owner holds the button for ever. Nothing would go hot again —
                // the hover guard fails for every box while any button is held.
                int blockedButton = ButtonIndex(blocked.Button);
                if (blocked.Kind == BibUiEventKind.MouseUp
                    && _activeId[blockedButton] is not null && _activeId[blockedButton] != id) continue;

                handle.Eat();
                if (blocked.Kind == BibUiEventKind.MouseDown) PressTaken(box);
            }
            return interaction;
        }

        float mouseX, mouseY;
        lock (_syncRoot) { mouseX = _mouseX; mouseY = _mouseY; }

        foreach (var handle in _frameEvents)
        {
            if (handle.Consumed) continue;

            BibUiEvent ev = handle.Event;

            // Addressed to one box by name. A by-name dispatch has already decided
            // which box it means, so the point it carries is only how the event
            // travels; letting whoever else covers that point take it would press
            // the button drawn in the middle of the card the caller named.
            if (ev.TargetId is not null && ev.TargetId != id) continue;

            int button = ButtonIndex(ev.Button);
            InteractionKinds kind = KindOf(ev);
            Vector2 pos = new(ev.X, ev.Y);
            // An open dropdown's rect is the dropdown's: what is not under it
            // treats a point there as outside itself.
            bool inBounds = Hit(pos, geometry, rect, size, transforms) && !IsUnderDropDown(box, pos);

            // Per button, not "claims some press": a box that took only the
            // right one must leave a left press for whoever did claim it.
            if ((claims & kind & Pressable) != 0 && inBounds && ev.Kind == BibUiEventKind.MouseDown)
            {
                // A press makes its box hot as well as active — the pointer is
                // demonstrably over it, whatever the last move said.
                _hotId = id;
                _activeId[button] = id;
                _pressPos[button] = pos;
                handle.Eat();
                PressTaken(box);


                interaction.Pressed |= kind.HasFlag(InteractionKinds.LeftPress);
                if (kind.HasFlag(InteractionKinds.LeftPress)) _leftPressCount = Math.Max(1, ev.ClickCount);
                interaction.MiddlePressed |= kind.HasFlag(InteractionKinds.MiddlePress);
                interaction.RightPressed |= kind.HasFlag(InteractionKinds.RightPress);

                // What was held when it happened. Read afterwards, a modifier
                // let go since would make ctrl-click a click.
                interaction.Modifiers |= ev.Modifiers;
            }

            if (ev.Kind == BibUiEventKind.MouseUp && _activeId[button] == id)
            {
                if (!inBounds)
                {
                    _hotId = null;
                }
                _activeId[button] = null;

                // The button that came up, not "a button did" — a right-drag
                // that could start had no way to end.
                if (button == Left) interaction.Released = true;
                else if (button == Right) interaction.RightReleased = true;
                else interaction.MiddleReleased = true;

                if (inBounds && (claims & Pressable) != 0)
                {
                    interaction.ClickCount = 1;
                }

                interaction.Modifiers |= ev.Modifiers;
                handle.Eat();
            }

            // Not gated on being hot: hotness needs a press claim, and a box
            // that only scrolls never has one. Ask order and Eat are what keep
            // the wheel from reaching two boxes at once.
            //
            // The wheel is an amount. Scroll reports all of it and moves
            // nothing. ViewScroll moves the view along the axes claimed; a box
            // claiming one takes the other axis's travel when the event has
            // none on its own. What is not taken stays on the event for
            // whoever asks next, and the event is eaten once nothing is left.
            InteractionKinds wheel = claims & kind & (InteractionKinds.ViewScroll | InteractionKinds.Scroll);
            if (ev.Kind == BibUiEventKind.Scroll && wheel != 0 && inBounds)
            {
                float dx = ev.ScrollX, dy = ev.ScrollY;
                if ((wheel & InteractionKinds.ViewScroll) == 0) (ev.ScrollX, ev.ScrollY) = (0f, 0f);
                else
                {
                    if ((wheel & InteractionKinds.ViewScrollX) == 0) { if (dy == 0f) dy = dx; dx = 0f; }
                    if ((wheel & InteractionKinds.ViewScrollY) == 0) { if (dx == 0f) dx = dy; dy = 0f; }
                    // A one-axis event is taken whole; a two-axis one gives up only what was taken.
                    if (ev.ScrollX == 0f || ev.ScrollY == 0f) (ev.ScrollX, ev.ScrollY) = (0f, 0f);
                    else { ev.ScrollX -= dx; ev.ScrollY -= dy; }
                    box.TargetViewOffset += new Vector2(dx, dy);
                }
                interaction.ScrollX += dx;
                interaction.ScrollY += dy;
                interaction.Modifiers |= ev.Modifiers;
                if (ev.ScrollX == 0f && ev.ScrollY == 0f) handle.Eat();
            }
        }

        ClampViewOffset(box, geometry);

        Vector2 mousePos = new Vector2(mouseX, mouseY);

        // TODO(K): Maybe also scrollable
        if ((claims & Pressable) != 0
            && Hit(mousePos, geometry, rect, size, transforms)
            && !IsUnderDropDown(box, mousePos)
            && (_hotId is null || _hotId == id)
            && NoOtherBoxActive(id))
        {
            // This means first ask wins. This requires builder code to invent their own z-ordering.
            _hotId = id;
            interaction.Hovering = true;
        }

        interaction.X = mouseX;
        interaction.Y = mouseY;

        //  Handdle dragging
        interaction.Dragging = _activeId[Left] == id;

        // The streak, for the press and for as long as it is held: a double
        // press dragged selects by words on every frame, not only the first.
        if (interaction.Pressed || interaction.Dragging) interaction.PressCount = _leftPressCount;
        interaction.RightDragging = _activeId[Right] == id;
        interaction.MiddleDragging = _activeId[Middle] == id;
        interaction.DragStartX = float.NaN;
        interaction.DragStartY = float.NaN;

        int held = -1;
        if (interaction.Dragging)
        {
            held = Left;
        }
        else if (interaction.RightDragging)
        {
            held = Right;
        }
        else if (interaction.MiddleDragging)
        {
            held = Middle;
        }

        // A drag that ended this frame still says where it began.
        //
        // The release above cleared _activeId, so by the time the anchor is read
        // the box is no longer dragging and there would be nothing to answer
        // with — the one frame in which knowing the travel matters most is the
        // one frame it used to be missing. That is not hypothetical: a press and
        // its release arrive together whenever they are delivered faster than
        // frames, which is what a real pointer does between renders and what the
        // test server's `gesture` command does by construction. A box driven that
        // way was told its drag had ended, at a position, with no way left to ask
        // where it started.
        //
        // _pressPos still holds it — nothing overwrites it until the next press —
        // so the release names the anchor when no button is still down. A button
        // still held wins, because that drag is the live one.
        int anchor = held;
        if (anchor < 0)
        {
            if (interaction.Released) anchor = Left;
            else if (interaction.RightReleased) anchor = Right;
            else if (interaction.MiddleReleased) anchor = Middle;
        }

        // One anchor for three buttons: hold two and the leftmost answers.
        if (anchor >= 0)
        {
            interaction.DragStartX = _pressPos[anchor].X;
            interaction.DragStartY = _pressPos[anchor].Y;
            interaction.DragDeltaX = mouseX - _pressPos[anchor].X;
            interaction.DragDeltaY = mouseY - _pressPos[anchor].Y;
        }

        return interaction;
    }

    /// <summary>
    /// Whether nothing else holds a button. A loop rather than a Linq All: this
    /// runs once per ask per frame, and the lambda would capture
    /// <paramref name="id"/> into a fresh closure every time.
    /// </summary>
    private bool NoOtherBoxActive(string id)
    {
        for (int b = 0; b < 3; b++)
            if (_activeId[b] is not null && _activeId[b] != id) return false;
        return true;
    }

    // Where each interactive box was drawn last frame, for the boxes a
    // transform moved: its own transform and its ancestors', and the clipping
    // ancestors it was drawn inside, each with the transform it was drawn
    // through. Null when nothing in the frame had a transform, which keeps the
    // common case to one rect test.
    private Dictionary<string, Drawn>? _pointerTransforms;

    /// <summary>A moved box as it was drawn: layout space to the surface, and the clips it was cut by.</summary>
    private sealed record Drawn(Matrix3x2 World, Matrix3x2 Inverse, DrawnClip? Clips);

    /// <summary>A clipping ancestor: its laid-out rect, and the surface back to its layout space.</summary>
    private sealed record DrawnClip(Vector2 Min, Vector2 Max, Matrix3x2 Inverse, DrawnClip? Next);

    /// <summary>The transform the box named <paramref name="id"/> was drawn through last frame, or null for none.</summary>
    private Matrix3x2? PointerTransformOf(string id)
    {
        lock (_syncRoot)
            return _pointerTransforms is not null && _pointerTransforms.TryGetValue(id, out var d) ? d.World : null;
    }

    /// <summary>
    /// Every moved interactive box as it was drawn, composed as the painter
    /// composes transforms. Run after layout, only for a frame that has a
    /// transform somewhere.
    /// </summary>
    private static Dictionary<string, Drawn> PointerTransforms(UiNode root)
    {
        var map = new Dictionary<string, Drawn>();
        Walk(root, Matrix3x2.Identity, Matrix3x2.Identity, moved: false, clips: null);
        return map;

        void Walk(UiNode node, Matrix3x2 parent, Matrix3x2 parentInverse, bool moved, DrawnClip? clips)
        {
            var world = parent;
            var inverse = parentInverse;
            if (node.Style.HasTransform)
            {
                world = node.Style.TransformFor(node.Rect, node.Extent) * parent;
                // Scaled to nothing has no inverse, and nothing to hit.
                inverse = Matrix3x2.Invert(world, out var inv) ? inv : new Matrix3x2(float.NaN, 0, 0, float.NaN, 0, 0);
                moved = true;
            }
            if (node is Box { Id: { } id, Claims: var claims } && claims != InteractionKinds.None)
            {
                if (moved) map[id] = new Drawn(world, inverse, clips);
                // A box that clips cuts what is under it, drawn where it is drawn.
                if ((claims & InteractionKinds.Clip) != 0)
                    clips = new DrawnClip(node.Rect, node.Rect + node.Extent, inverse, clips);
            }
            for (UiNode? c = node.FirstChild; c is not null; c = c.Next == c ? null : c.Next)
                Walk(c, world, inverse, moved, clips);
        }
    }

    /// <summary>
    /// Whether <paramref name="at"/> lands on <paramref name="geometry"/> as it
    /// was drawn: inside its own rect and every clipping ancestor's, each
    /// tested in its own layout space. A box no transform moved is tested as
    /// laid out, against <paramref name="rect"/> already cut to its clips.
    /// </summary>
    private static bool Hit(Vector2 at, Box geometry, Vector2 rect, Vector2 size, Dictionary<string, Drawn>? transforms)
    {
        if (transforms is null || geometry.Id is not { } id || !transforms.TryGetValue(id, out var drawn)) return InBounds(at, rect, size);
        if (!InBounds(Vector2.Transform(at, drawn.Inverse), geometry.Rect, geometry.Extent)) return false;
        for (var clip = drawn.Clips; clip is not null; clip = clip.Next)
            if (!InBounds(Vector2.Transform(at, clip.Inverse), clip.Min, clip.Max - clip.Min)) return false;
        return true;
    }

    /// <summary>Half-open on both axes, so two boxes sharing an edge do not both own it.</summary>
    private static bool InBounds(Vector2 pos, Vector2 rect, Vector2 size) =>
        pos.X >= rect.X && pos.X < rect.X + size.X &&
        pos.Y >= rect.Y && pos.Y < rect.Y + size.Y;

    /// <summary>Which slot of <c>_activeId</c>/<c>_pressPos</c> a button owns. Matches <see cref="KindOf"/>.</summary>
    private static int ButtonIndex(MouseButton button) => button switch
    {
        MouseButton.Left => Left,
        MouseButton.Right => Right,
        _ => Middle,
    };

    /// <summary>Which claim an event answers to.</summary>
    private static InteractionKinds KindOf(BibUiEvent ev) => ev.Kind switch
    {
        BibUiEventKind.MouseDown or BibUiEventKind.MouseUp => ev.Button switch
        {
            MouseButton.Left => InteractionKinds.LeftPress,
            MouseButton.Right => InteractionKinds.RightPress,
            _ => InteractionKinds.MiddlePress,
        },
        // Any wheel claim: which axis the travel goes to is decided when it is taken.
        BibUiEventKind.Scroll => ev.ScrollX != 0f || ev.ScrollY != 0f ? InteractionKinds.ViewScroll | InteractionKinds.Scroll : InteractionKinds.None,
        _ => InteractionKinds.None,
    };


    /// <summary>
    /// Where the pointer is, for a frame that carries no move of its own.
    ///
    /// TODO(interactions): to be deleted. The press anchor it used to keep
    /// belongs to whoever *takes* the press, not to the moment the event
    /// arrived, so it moves into <see cref="InteractionsFor"/>; the position
    /// is then folded from the frame's own events. It cannot be folded on
    /// consumption — nothing eats a MouseMove once hovering is a rect test —
    /// so the fold runs over every pointer event, eaten or not.
    /// </summary>
    private void NotePointer(BibUiEvent ev)
    {
        if (!IsPointerKind(ev.Kind)) return;
        lock (_syncRoot) { _mouseX = ev.X; _mouseY = ev.Y; }
    }

    private static bool IsPointerKind(BibUiEventKind kind) =>
        kind is BibUiEventKind.MouseDown or BibUiEventKind.MouseUp
             or BibUiEventKind.MouseMove or BibUiEventKind.Scroll;
}
