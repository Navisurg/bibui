using Bibui.Theming;

namespace Bibui.Runtime;

public sealed partial class BibuiRuntime
{
    // ── Eased values for build functions ──────────────────────────────────────
    // Say where a value should be; get back where it is. The renderer has had
    // this for its own drawing (DrawContext.Animate) but a build function could
    // not reach it, so anything a component wanted to ease — a handle gliding to
    // a new value, a toggle's knob crossing its track — it had to ease itself:
    // its own dictionary, its own curve, its own RequestFrame to stop the loop
    // parking half way. Three things to get right per component, and three
    // places to get them wrong.
    //
    // Keyed by (id, property) rather than held on the box, so a node can ease as
    // many values as it likes without the box growing a field per affordance,
    // and so something with no box can ease too. HotT/ActiveT stay where they
    // are: they are driven by input the runtime already tracks, not by a target
    // the caller names.

    private sealed class Eased
    {
        public float Current;
        public float Target;
        public float Speed;
        public float Epsilon;
    }

    // Two frames, not sixty: a value nobody asks for has stopped being on screen.
    private readonly ResourceCache<AnimKey, Eased> _eased = new(keepFrames: 2);

    /// <summary>
    /// Where <paramref name="property"/> of <paramref name="id"/> has got to on
    /// its way to <paramref name="target"/>. <paramref name="speed"/> is
    /// halvings per second, so the curve does not depend on the frame rate, and
    /// <paramref name="epsilon"/> is what lets it arrive — exponential approach
    /// never actually does.
    ///
    /// Asked for the first time, a value *is* its target rather than travelling
    /// to it: otherwise every screen would slide into place on the frame it
    /// first appears, and a one-shot offscreen render would be a frame of
    /// half-finished animation. Same rule <c>DrawContext.Animate</c> follows.
    ///
    /// Asking does not advance it — the step happens once, at the end of the
    /// frame — so a build function that runs twice for one input reads the same
    /// answer both times, and a value asked for twice in one frame cannot move
    /// twice. The loop is kept awake for as long as anything is still moving.
    ///
    /// A <paramref name="speed"/> of <see cref="float.PositiveInfinity"/> closes
    /// the whole gap in one step, for the frames where a value is driven
    /// directly rather than animated — a slider handle under the cursor should
    /// not ease toward it. That step is still the end-of-frame one, so the value
    /// read *this* frame is where the target was last frame; the next frame
    /// naming a finite speed eases on from wherever it was left.
    ///
    /// With no <paramref name="speed"/>, the theme's
    /// <see cref="Bibui.Theming.MotionTokens.Default"/>. Whatever the speed, a
    /// theme whose <see cref="Bibui.Theming.MotionTokens.Easing"/> is
    /// <see cref="Bibui.Theming.MotionEasing.None"/> closes the gap in one step.
    /// </summary>
    public float Animate(
        string id,
        string property,
        float target,
        float? speed = null,
        float epsilon = 0.001f)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(property);
        var cell = _eased.FromKey(AnimKey.For(id, property), target,
            static t => new Eased { Current = t, Target = t });
        cell.Target = target;
        cell.Speed = speed ?? _theme.Motion.Default;
        cell.Epsilon = epsilon;

        return cell.Current;
    }

    /// <summary>
    /// Steps every value asked for this frame, and says whether any is still
    /// moving. Runs before the cache's own
    /// EndFrame, which is what <c>Touched</c> is still comparable against.
    /// </summary>
    private bool StepEased()
    {
        if (_eased.Count == 0) return false;

        bool instant = Instant;
        bool animating = false;
        foreach (var (_, entry) in _eased.Entries)
        {
            // Only what this frame actually asked for. One that scrolled off
            // screen stops being driven rather than racing to its target unseen.
            if (entry.Touched != _eased.Frame) continue;

            var cell = entry.Value;
            float rate = instant ? 1f : Ease.ExponentialRate(cell.Speed, Dt);
            float next = cell.Current + (cell.Target - cell.Current) * rate;
            if (MathF.Abs(cell.Target - next) <= cell.Epsilon)
            {
                next = cell.Target;
            }
            else
            {
                animating = true;
            }
            cell.Current = next;
        }
        return animating;
    }
}
