using System.Numerics;
using Bibui.Theming;

namespace Bibui.Runtime;

public sealed partial class BibuiRuntime
{
    // ── Retained boxes ────────────────────────────────────────────────────────
    // A box named by an id outlives the frame that described it, so it has
    // somewhere to keep what a build function cannot: how hovered it is, how
    // pressed. A retained box, held in a hash table across frames for
    // the same reason.
    //
    // The store is the renderer's ResourceCache, so lifetime is the same
    // touch-and-evict rule everything else here follows: asking for a box
    // touches it, and one nobody asks for is dropped after keepFrames. Nothing
    // to register, nothing to release.
    //
    // Identity is the id, so two BoxFromId calls with one id are one box —
    // the point when the id names something that persists, a bug when it is
    // reused for two things at once. Give siblings distinct ids.

    private readonly ResourceCache<string, Box> _retainedBoxes = new(keepFrames: 60);

    /// <summary>
    /// The box named <paramref name="id"/>, the same instance every frame:
    /// <see cref="Box.HotT"/>, <see cref="Box.ActiveT"/>, <see cref="Box.Rect"/>
    /// and <see cref="UiNode.Extent"/> carry forward. Everything else is cleared,
    /// because "not set this frame" has to be expressible.
    ///
    /// <para><paramref name="claims"/> is taken here rather than assigned
    /// afterwards, so there is no moment at which a box exists without having
    /// said what it takes. <see cref="InteractionsFor(Box)"/> then answers from
    /// a box it was handed, which is one it can be sure was built — the two
    /// together are what make "describe, then ask" structural instead of a
    /// convention that can be broken by asking too early.</para>
    ///
    /// <para>The box is linked under <paramref name="parent"/>, or under the
    /// top of <see cref="PushParent"/>'s stack when none is given.</para>
    /// </summary>
    public Box BoxFromId(string id, InteractionKinds claims = InteractionKinds.None, UiNode? parent = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        // The cache is not thread-safe by design (the render lock single-flights
        // frames); _syncRoot is what makes an ask from outside a build safe too.
        lock (_syncRoot)
        {
            var box = _retainedBoxes.FromKey(id, id, static key => new Box { Id = key });
            // Acquired again this frame: out of where it was first linked, so
            // that parent's children do not still name it.
            if (box.Parent is not null && box.Parent.FirstChild is not null) UiNode.Unlink(box);
            ResetForBuild(box);
            box.Claims = claims;
            box.ChildLayoutAxis = _axisStack.Count > 0 ? _axisStack[^1] : LayoutAxis.X;
            if (_sizeXStack.Count > 0) box.SizeX = _sizeXStack[^1];
            if (_sizeYStack.Count > 0) box.SizeY = _sizeYStack[^1];

            LinkUnder(box, parent);
            return box;
        }
    }

    /// <summary>
    /// The box named <paramref name="id"/> as of the last frame, without
    /// creating one and without clearing it.
    ///
    /// <see cref="BoxFromId"/> is the build function's door and resets the box
    /// on the way through. An ask is not a describe: <c>InteractionsFor</c>
    /// wants to read the claims the build just set, so going through that door
    /// a second time would wipe them.
    ///
    /// Retained first, then whatever the render collected. A box built fresh
    /// each frame is answerable too — the renderer stamped its Rect and Size
    /// like any other, and a press is a fact about one frame. What it cannot
    /// have is <see cref="Box.HotT"/>: nothing of it survives to ease.
    /// </summary>
    private Box? PeekBox(string id)
    {
        lock (_syncRoot)
        {
            if (_retainedBoxes.Entries.TryGetValue(id, out var entry)) return entry.Value;
            return _interactiveBoxes.TryGetValue(id, out var box) ? box : null;
        }
    }

    /// <summary>
    /// Clears everything a build function sets, keeping only what has to outlive
    /// the frame: <see cref="UiNode.Id"/> (it is the cache key), and
    /// <see cref="Box.HotT"/>/<see cref="Box.ActiveT"/> (the state a build
    /// function has no way to hold itself).
    ///
    /// The rule is to zero out per-frame state — style, child links, claims, the draw callback —
    /// while what eases across frames carries on. Without it, "not set this
    /// frame" is
    /// unexpressible: a field assigned under a condition would linger into every
    /// later frame whose condition is false, and no build function could see it
    /// or undo it.
    ///
    /// The Style is cleared in place rather than replaced, so the retained box
    /// keeps one Style instance for its whole life.
    /// </summary>
    private static void ResetForBuild(Box box)
    {
        ResetNode(box);
        box.Draw = null;
    }

    /// <summary>What every node a build describes again starts from: no style, no links, no size.</summary>
    private static void ResetNode(UiNode node)
    {
        node.Style.Clear();
        node.Parent = null;
        node.Prev = null;
        node.Next = null;
        node.FirstChild = null;
        node.LastChild = null;
        node.SizeX = default;
        node.SizeY = default;
        node.AllowOverflowX = node.AllowOverflowY = false;
        node.FloatingX = node.FloatingY = false;
        node.FixedPosition = default;
    }

    /// <summary>Links <paramref name="node"/> last under <paramref name="parent"/>, or under the parent in scope.</summary>
    private void LinkUnder(UiNode node, UiNode? parent)
    {
        parent ??= _parentStack.Count > 0 ? _parentStack[^1] : null;
        // A box acquired again inside its own scope is not its own child.
        if (parent is null || parent == node) return;
        node.Parent = parent;
        node.Prev = parent.LastChild;
        if (parent.LastChild is not null) parent.LastChild.Next = node;
        else parent.FirstChild = node;
        parent.LastChild = node;
    }

    /// <summary>Where an ease is close enough to its target to be called arrived.</summary>
    private const float BoxAnimEpsilon = 0.01f;

    /// <summary>The same, for <see cref="Box.ViewOffset"/>, which is in pixels rather than 0..1.</summary>
    private const float ViewOffsetEpsilon = 0.5f;

    /// <summary>
    /// The frame's closing marker: hot/active and every eased value are stepped,
    /// and everything whose lifetime is "until nobody wants it any more" is swept.
    ///
    /// At the *end* of the frame — so
    /// a build function reads the value the previous frame settled on, the same
    /// one-frame lag the bounds it hit-tests against already have. Once per
    /// render, so a build that runs twice cannot step anything twice.
    /// </summary>
    private void SweepFrame()
    {
        bool animating;
        lock (_syncRoot)
        {
            // A scope left open would link next frame's boxes under this
            // frame's parent.
            bool unbalanced = _parentStack.Count > 0 || _axisStack.Count > 0
                || _sizeXStack.Count > 0 || _sizeYStack.Count > 0 || _fontSizeStack.Count > 0 || _faceStack.Count > 0;
            _parentStack.Clear();
            _axisStack.Clear();
            _sizeXStack.Clear();
            _sizeYStack.Clear();
            _fontSizeStack.Clear();
            _faceStack.Clear();
            if (unbalanced)
                throw new InvalidOperationException(
                    "PushParent, PushAxis, PushFontSize, PushFontFace or a size scope was not disposed before the build returned; use them with `using`.");

            animating = StepHotAndActive();
            animating |= StepEased();
            animating |= StepMotion();

            // A box that has gone disabled, or left the tree, cannot release
            // the button itself: the release block needs it to ask, and it
            // never will. While it holds one,
            // the hover guard below fails for every box and nothing can go hot
            // again — so the button has to be taken off it here.
            //
            // Against _interactiveBoxes, which the render rebuilds whole, so a
            // box that stopped being built is gone from it at once. Not against
            // _retainedBoxes: that keeps entries for 60 frames, and a box that
            // stopped being built keeps its last Claims the whole time, because
            // only BoxFromId clears them.
            for (int b = 0; b < 3; b++)
                if (_activeId[b] is string activeId
                    && (!_interactiveBoxes.TryGetValue(activeId, out var activeBox)
                        || activeBox.Claims == InteractionKinds.Disabled))
                    _activeId[b] = null;

            // Cleared once a frame so the asks that come next decide it
            // afresh — but only while no button is held: a drag that wanders
            // off its box stays the engaged control. Swept first, so a
            // button freed just above lets hot clear on the same frame.
            if (_activeId[Left] is null && _activeId[Middle] is null && _activeId[Right] is null)
                _hotId = null;
            _retainedBoxes.EndFrame();
            RewindLeaves();
            _eased.EndFrame();
            _motion.EndFrame();
            _lists.EndFrame();
        }
        // Outside the lock: RequestFrame wakes the host, and a wake handler is
        // not ours to run with _syncRoot held.
        if (animating) RequestFrame();
    }

    /// <summary>
    /// Keeps <see cref="Box.TargetViewOffset"/> inside the content along each
    /// axis <paramref name="box"/> claims <see cref="InteractionKinds.ClampX"/>
    /// or <see cref="InteractionKinds.ClampY"/> for: from 0 to how far the
    /// content runs past the box. Measured off <paramref name="geometry"/>,
    /// the box as it was last laid out.
    /// </summary>
    private static void ClampViewOffset(Box box, Box geometry)
    {
        Vector2 max = Vector2.Max(Vector2.Zero, geometry.ContentSize - geometry.Extent);
        Vector2 target = box.TargetViewOffset;
        if ((box.Claims & InteractionKinds.ClampX) != 0) target.X = Math.Clamp(target.X, 0f, max.X);
        if ((box.Claims & InteractionKinds.ClampY) != 0) target.Y = Math.Clamp(target.Y, 0f, max.Y);
        box.TargetViewOffset = target;
    }

    /// <summary>
    /// Eases every retained box toward what this frame's input says it is, and
    /// reports whether anything is still moving. Caller holds <c>_syncRoot</c>.
    ///
    /// Hot eases both ways. Active does not: a press lands on 1 at once,
    /// because a button that fades in under the finger feels unresponsive, and
    /// only letting go eases back.
    ///
    /// Both snap to the target once inside the epsilon rather than approaching
    /// it forever, so a settled box reports exactly 0 or 1 and a geometry
    /// driven from it lands on a whole number.
    /// </summary>
    private bool StepHotAndActive()
    {
        if (_retainedBoxes.Count == 0) return false;

        // The theme's hover speed, 60 halvings a second by default: deliberately
        // faster than DrawContext.Animate's 12, since a hover that takes half a
        // second to arrive reads as lag, not as easing.
        var motion = _theme.Motion;
        float rate = Instant ? 1f : 1f - MathF.Pow(2f, -motion.Hover * Dt);
        bool animating = false;

        foreach (var (id, entry) in _retainedBoxes.Entries)
        {
            var box = entry.Value;

            float hotTarget = id == _hotId ? 1f : 0f;
            float hot = box.HotT + rate * (hotTarget - box.HotT);
            if (MathF.Abs(hotTarget - hot) <= BoxAnimEpsilon) hot = hotTarget;
            else animating = true;
            box.HotT = hot;

            if (id == _activeId[Left])
            {
                box.ActiveT = 1f;
            }
            else
            {
                float active = box.ActiveT + rate * (0f - box.ActiveT);
                if (active <= BoxAnimEpsilon) active = 0f;
                else animating = true;
                box.ActiveT = active;
            }

            // NOTE(K): Update view offsets for the boxes: ViewOffset->TargetViewOffset
            // using our exponential decay
            ClampViewOffset(box, box);
            // TODO(k): Use the call to animate
            Vector2 view = box.ViewOffset + rate * (box.TargetViewOffset - box.ViewOffset);
            if (Vector2.Distance(view, box.TargetViewOffset) <= ViewOffsetEpsilon) view = box.TargetViewOffset;
            else animating = true;
            box.ViewOffset = view;
        }

        return animating;
    }
}
