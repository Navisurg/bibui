using System.Numerics;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;
using Bibui.Core.Theming;
using Bibui.Raster.Skia;

/// <summary>
/// Motion: timed curves and springs beside Animate, colour in Oklab, presence
/// and list presence, collapsing rows, paint-time transforms and the pointer
/// test that follows them, theme crossfades, and reduced motion.
/// </summary>
static class MotionTests
{

    static void Eq<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
    }

    static void Near(float expected, float actual, float tolerance, string message)
    {
        if (MathF.Abs(expected - actual) > tolerance) throw new Exception($"{message}: expected {expected} ± {tolerance} but got {actual}");
    }

    static void IsTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static Box Root() => new Box { ChildLayoutAxis = LayoutAxis.Y }.Sized(Size.Fill, Size.Fill);

    /// <summary>A runtime whose build asks <paramref name="ask"/> and draws nothing else.</summary>
    static BibuiRuntime Asking(Action<BibuiRuntime> ask, float hz = 60f)
    {
        var runtime = BibuiRuntime.Create();
        runtime.Dt = 1f / hz;
        runtime.SetBuild(() => { ask(runtime); return Root(); }, new SurfaceOptions { Width = 200, Height = 100 });
        return runtime;
    }

    static void Frames(BibuiRuntime runtime, int n)
    {
        for (int i = 0; i < n; i++) runtime.Step();
    }

    static (byte R, byte G, byte B) Pixel(Pixels frame, int x, int y)
    {
        int i = (y * frame.Width + x) * 4;   // BGRA
        return (frame.RawBgraBytes[i + 2], frame.RawBgraBytes[i + 1], frame.RawBgraBytes[i]);
    }

    public static void Register(Action<string, Action> run)
    {
        // ── Easing ──────────────────────────────────────────────────────────

        run("Easing: every preset starts at 0, ends at exactly 1 and never goes back", () =>
        {
            foreach (var e in new[] { Easing.Linear, Easing.Ease, Easing.EaseIn, Easing.EaseOut, Easing.EaseInOut, Easing.Standard, Easing.Emphasized, Easing.EmphasizedAccelerate })
            {
                Eq(0f, e.Evaluate(0f), $"{e} at 0");
                Eq(1f, e.Evaluate(1f), $"{e} at 1");
                float last = 0f;
                for (int i = 1; i < 100; i++)
                {
                    float v = e.Evaluate(i / 100f);
                    IsTrue(v >= last - 1e-4f, $"{e} goes back at {i / 100f}: {last} then {v}");
                    last = v;
                }
            }
        });

        run("Easing: matches CSS cubic-bezier where it is known", () =>
        {
            Near(0.5f, Easing.Linear.Evaluate(0.5f), 1e-5f, "linear is the identity");
            Near(0.5f, Easing.EaseInOut.Evaluate(0.5f), 1e-3f, "ease-in-out is symmetric");
            // ease-out arrives early, ease-in late.
            IsTrue(Easing.EaseOut.Evaluate(0.5f) > 0.6f, "ease-out is past half way at half time");
            IsTrue(Easing.EaseIn.Evaluate(0.5f) < 0.4f, "ease-in is short of half way at half time");
        });

        // ── Timed ───────────────────────────────────────────────────────────

        run("Timed: asked for the first time, a value is at its target", () =>
        {
            var runtime = Asking(rt => rt.Animate("t", "x", 5f, Transition.Timed(0.2f)));
            runtime.Step();
            Eq(5f, runtime.Animate("t", "x", 5f, Transition.Timed(0.2f)), "arrived, not travelling");
        });

        run("Timed: follows its curve and arrives in exactly its duration", () =>
        {
            float target = 0f;
            var tr = Transition.Timed(0.2f, Easing.Linear);
            var runtime = Asking(rt => rt.Animate("t", "x", target, tr));
            runtime.Step();

            target = 100f;
            Frames(runtime, 6);   // 6 of the 12 frames 0.2 s is at 60 Hz
            Near(50f, runtime.Animate("t", "x", 100f, tr), 0.01f, "half way at half time on a linear curve");

            Frames(runtime, 5);
            IsTrue(runtime.Animate("t", "x", 100f, tr) < 100f, "not there one frame early");
            Frames(runtime, 1);
            Eq(100f, runtime.Animate("t", "x", 100f, tr), "exactly there at 0.2 s");
        });

        run("Timed: holds still for its delay, then runs", () =>
        {
            float target = 0f;
            var tr = Transition.Timed(0.1f, Easing.Linear, delay: 0.1f);
            var runtime = Asking(rt => rt.Animate("t", "x", target, tr));
            runtime.Step();
            target = 1f;
            Frames(runtime, 6);
            Eq(0f, runtime.Animate("t", "x", 1f, tr), "still waiting at the end of the delay");
            Frames(runtime, 3);
            Near(0.5f, runtime.Animate("t", "x", 1f, tr), 0.01f, "half way through the run after it");
        });

        run("Timed: retargeted mid-flight, it starts the new leg from where it is", () =>
        {
            float target = 0f;
            var tr = Transition.Timed(0.2f, Easing.Linear);
            var runtime = Asking(rt => rt.Animate("t", "x", target, tr));
            runtime.Step();
            target = 100f;
            Frames(runtime, 6);
            float mid = runtime.Animate("t", "x", 100f, tr);
            target = 0f;
            Frames(runtime, 1);
            float after = runtime.Animate("t", "x", 0f, tr);
            IsTrue(after < mid && after > mid - 20f, $"turned back from {mid}, not jumped (got {after})");
        });

        run("Timed: the same time takes it the same distance at 30 Hz and 120 Hz", () =>
        {
            float At(float hz)
            {
                float target = 0f;
                var tr = Transition.Timed(0.4f, Easing.Standard);
                var runtime = Asking(rt => rt.Animate("t", "x", target, tr), hz);
                runtime.Step();
                target = 1f;
                Frames(runtime, (int)(0.2f * hz));
                return runtime.Animate("t", "x", 1f, tr);
            }
            Near(At(30f), At(120f), 1e-4f, "half the duration at either rate");
        });

        // ── Ease: the steps, as pure functions ──────────────────────────────

        run("Ease: exponential approach closes the rate's share of the gap; the rate halves it speed times a second", () =>
        {
            Near(0.5f, Ease.Rate(20f, 1f / 20f), 1e-6f, "one halving");
            Near(0.75f, Ease.Rate(20f, 2f / 20f), 1e-6f, "two halvings");
            Near(0.5f, Ease.Exponential(0f, 1f, Ease.Rate(20f, 1f / 20f)), 1e-6f, "the step closes the rate's share");
            Eq(7f, Ease.Exponential(3f, 7f, 1f), "a rate of 1 lands on the target");
            float half = Ease.Rate(20f, 1f / 120f);
            Near(Ease.Exponential(0f, 1f, Ease.Rate(20f, 1f / 60f)), Ease.Exponential(Ease.Exponential(0f, 1f, half), 1f, half), 1e-6f, "two half frames reach where one frame does");
        });

        run("Ease: the vector overloads ease each component as the float one does", () =>
        {
            float rate = Ease.Rate(12f, 1f / 60f);
            float x = Ease.Exponential(1f, 5f, rate), y = Ease.Exponential(-2f, 3f, rate);
            Eq(new Vector2(x, y), Ease.Exponential(new Vector2(1f, -2f), new Vector2(5f, 3f), rate), "Vector2");
            Eq(new Vector4(x, y, x, y), Ease.Exponential(new Vector4(1f, -2f, 1f, -2f), new Vector4(5f, 3f, 5f, 3f), rate), "Vector4");
        });

        run("Rates: one per kind of motion from the theme's speeds, and all 1 when motion is instant", () =>
        {
            var motion = new MotionTokens { Hover = 60f, Follow = 30f, Toggle = 22f, Default = 20f };
            var rates = MotionRates.From(motion, 1f / 60f, instant: false);
            Eq(new MotionRates(Ease.Rate(60f, 1f / 60f), Ease.Rate(30f, 1f / 60f), Ease.Rate(22f, 1f / 60f), Ease.Rate(20f, 1f / 60f)), rates, "each from its speed");
            Eq(new MotionRates(1f, 1f, 1f, 1f), MotionRates.From(motion, 1f / 60f, instant: true), "instant");

            var runtime = Asking(_ => { });
            runtime.Step();
            Eq(MotionRates.From(runtime.Theme.Motion, runtime.Dt, instant: false), runtime.Rates, "the runtime's, from its theme");
            Eq(Ease.Rate(18f, runtime.Dt), runtime.Rate(18f), "a speed of one's own");
            runtime.ReducedMotion = true;
            runtime.Step();
            Eq(new MotionRates(1f, 1f, 1f, 1f), runtime.Rates, "reduced motion");
            Eq(1f, runtime.Rate(18f), "reduced motion, for a speed of one's own");
        });

        run("Rates: easing by them allocates nothing, asked or stepped", () =>
        {
            float target = 0f;
            var runtime = Asking(rt =>
            {
                rt.Animate("a", "toggle", target, rt.Rates.Toggle);
                rt.Animate("a", "own", target, rt.Rate(18f));
                rt.Animate("a", "default", target);
            });
            Frames(runtime, 3);

            long before = GC.GetAllocatedBytesForCurrentThread();
            float x = 0f, v = 0f;
            Vector2 p = Vector2.Zero;
            Vector4 q = Vector4.Zero, qv = Vector4.Zero;
            for (int i = 0; i < 1000; i++)
            {
                var rates = runtime.Rates;
                x = Ease.Exponential(x, 1f, rates.Follow) + runtime.Rate(18f) * 0f;
                p = Ease.Exponential(p, Vector2.One, rates.Hover);
                q = Ease.Exponential(q, Vector4.One, rates.Toggle);
                Ease.Spring(ref x, ref v, 1f, Spring.Snappy, runtime.Dt);
                Ease.Spring(ref q, ref qv, Vector4.One, Spring.Gentle, runtime.Dt);
            }
            Eq(0L, GC.GetAllocatedBytesForCurrentThread() - before, "bytes allocated by a thousand steps");

            // Frames that ease (the values travelling) against frames that do not.
            Frames(runtime, 5);
            before = GC.GetAllocatedBytesForCurrentThread();
            Frames(runtime, 10);
            long settled = GC.GetAllocatedBytesForCurrentThread() - before;
            target = 1f;
            Frames(runtime, 1);
            before = GC.GetAllocatedBytesForCurrentThread();
            Frames(runtime, 10);
            long moving = GC.GetAllocatedBytesForCurrentThread() - before;
            IsTrue(moving <= settled, $"easing frames allocate no more than settled ones ({moving} vs {settled} bytes over ten frames)");
        });

        run("Ease: a spring settles on its target; critically damped it never overshoots, bouncy it does", () =>
        {
            float Peak(Spring spring, out float end)
            {
                float x = 0f, v = 0f, peak = 0f;
                for (int i = 0; i < 120; i++) { Ease.Spring(ref x, ref v, 1f, spring, 1f / 60f); peak = MathF.Max(peak, x); }
                end = x;
                return peak;
            }
            IsTrue(Peak(Spring.Snappy, out float snappy) <= 1.0001f, "snappy stays at or under its target");
            Near(1f, snappy, 1e-3f, "snappy has settled after two seconds");
            IsTrue(Peak(Spring.Bouncy, out _) > 1.01f, "bouncy goes past it");
        });

        run("Ease: the float spring is the vector spring's first component", () =>
        {
            float x = 0f, v = 0f;
            Vector4 xs = Vector4.Zero, vs = Vector4.Zero;
            for (int i = 0; i < 10; i++)
            {
                Ease.Spring(ref x, ref v, 100f, Spring.Gentle, 1f / 60f);
                Ease.Spring(ref xs, ref vs, new Vector4(100f), Spring.Gentle, 1f / 60f);
            }
            Eq(xs.X, x, "position");
            Eq(vs.X, v, "velocity");
        });

        // ── Spring ──────────────────────────────────────────────────────────

        run("Spring: settles exactly on its target and stops asking for frames", () =>
        {
            float target = 0f;
            var tr = Transition.OnSpring(Spring.Snappy);
            var runtime = Asking(rt => rt.Animate("s", "x", target, tr, epsilon: 0.01f));
            runtime.Step();
            target = 100f;
            Frames(runtime, 120);
            Eq(100f, runtime.Animate("s", "x", 100f, tr, epsilon: 0.01f), "arrived on the number");
        });

        run("Spring: a critically damped spring never overshoots; a bouncy one does", () =>
        {
            float Peak(Spring spring)
            {
                float target = 0f, peak = 0f;
                var tr = Transition.OnSpring(spring);
                var runtime = Asking(rt => peak = MathF.Max(peak, rt.Animate("s", "x", target, tr)));
                runtime.Step();
                target = 1f;
                Frames(runtime, 120);
                return peak;
            }
            IsTrue(Peak(Spring.Snappy) <= 1.0001f, $"snappy stays at or under its target (peak {Peak(Spring.Snappy)})");
            IsTrue(Peak(Spring.Bouncy) > 1.01f, $"bouncy goes past it (peak {Peak(Spring.Bouncy)})");
        });

        run("Spring: retargeted mid-flight, it keeps its speed rather than starting from rest", () =>
        {
            float target = 0f;
            var tr = Transition.OnSpring(Spring.Gentle);
            var runtime = Asking(rt => rt.Animate("s", "x", target, tr));
            runtime.Step();
            target = 100f;
            Frames(runtime, 6);
            float a = runtime.Animate("s", "x", 100f, tr);
            Frames(runtime, 1);
            float b = runtime.Animate("s", "x", 100f, tr);
            float moving = b - a;

            target = 200f;   // further on, the same way
            Frames(runtime, 1);
            float c = runtime.Animate("s", "x", 200f, tr);
            IsTrue(c - b >= moving * 0.9f, $"carried its speed into the new leg ({moving:F2}/frame before, {c - b:F2} after)");
        });

        run("Spring: the same time takes it about the same distance at 30 Hz and 144 Hz", () =>
        {
            float At(float hz)
            {
                float target = 0f;
                var tr = Transition.OnSpring(Spring.Gentle);
                var runtime = Asking(rt => rt.Animate("s", "x", target, tr), hz);
                runtime.Step();
                target = 1f;
                Frames(runtime, (int)MathF.Round(0.1f * hz));
                return runtime.Animate("s", "x", 1f, tr);
            }
            Near(At(30f), At(144f), 0.03f, "within a few percent after 0.1 s");
        });

        // ── Points and colours ──────────────────────────────────────────────

        run("Animate(Vector2): both coordinates share one leg", () =>
        {
            var target = Vector2.Zero;
            var tr = Transition.Timed(0.2f, Easing.Linear);
            var runtime = Asking(rt => rt.Animate("bar", "at", target, tr));
            runtime.Step();
            target = new Vector2(100f, 40f);
            Frames(runtime, 6);
            var at = runtime.Animate("bar", "at", target, tr);
            Near(50f, at.X, 0.01f, "x half way");
            Near(20f, at.Y, 0.01f, "y half way, on the same clock");
        });

        run("AnimateColor: first ask is the colour; a change fades through Oklab and lands exactly", () =>
        {
            UiColor target = "#FF0000";
            var tr = Transition.Timed(0.2f, Easing.Linear);
            var runtime = Asking(rt => rt.AnimateColor("row", "bg", target, tr));
            runtime.Step();
            Eq("#FF0000FF", runtime.AnimateColor("row", "bg", target, tr).Resolve(runtime.Theme.Colors), "starts as asked");

            target = "#0000FF";
            Frames(runtime, 6);
            var mid = Rgba.Parse(runtime.AnimateColor("row", "bg", target, tr).Resolve(runtime.Theme.Colors));
            var oklab = ColorMix.Oklab(Rgba.Parse("#FF0000"), Rgba.Parse("#0000FF"), 0.5f);
            Eq(oklab, mid, "half way is Oklab's half way");
            IsTrue(mid.R + mid.B > 2 * 128, $"brighter than sRGB's muddy (128, 0, 128): {mid.R},{mid.G},{mid.B}");

            Frames(runtime, 6);
            Eq("#0000FFFF", runtime.AnimateColor("row", "bg", target, tr).Resolve(runtime.Theme.Colors), "lands on the new colour");
        });

        run("AnimateColor: fading out to transparent keeps the hue and arrives", () =>
        {
            UiColor target = "#3366CC";
            var tr = Transition.Timed(0.1f, Easing.Linear);
            var runtime = Asking(rt => rt.AnimateColor("chip", "bg", target, tr));
            runtime.Step();
            target = "#3366CC00";
            Frames(runtime, 3);
            var mid = Rgba.Parse(runtime.AnimateColor("chip", "bg", target, tr).Resolve(runtime.Theme.Colors));
            Eq((byte)0x33, mid.R, "red held"); Eq((byte)0x66, mid.G, "green held"); Eq((byte)0xCC, mid.B, "blue held");
            IsTrue(mid.A is > 0 and < 255, $"part transparent (alpha {mid.A})");
            Frames(runtime, 4);
            Eq((byte)0, Rgba.Parse(runtime.AnimateColor("chip", "bg", target, tr).Resolve(runtime.Theme.Colors)).A, "gone");
        });

        run("ColorMix.Oklab: exact at both ends", () =>
        {
            var a = new Rgba(12, 200, 99, 255);
            var b = new Rgba(250, 3, 77, 128);
            Eq(a, ColorMix.Oklab(a, b, 0f), "0 is from");
            Eq(b, ColorMix.Oklab(a, b, 1f), "1 is to");
            Eq(a, ColorMix.FromOklab(ColorMix.ToOklab(a)), "a colour survives the round trip");
        });

        // ── Presence ────────────────────────────────────────────────────────

        run("Presence: present when first asked is there at once; absent is gone", () =>
        {
            Presence shown = default, hidden = default;
            var runtime = Asking(rt =>
            {
                shown = rt.Presence("view", present: true);
                hidden = rt.Presence("palette", present: false);
            });
            runtime.Step();
            Eq(PresenceState.Present, shown.State, "the view is simply there");
            Eq(1f, shown.Progress, "at rest");
            Eq(PresenceState.Gone, hidden.State, "the palette is not");
            IsTrue(!hidden.Mounted, "and is not built");
        });

        run("Presence: leaving stays mounted through its exit, then is gone", () =>
        {
            bool open = true;
            Presence p = default;
            var exit = Transition.Timed(0.1f, Easing.Linear);
            var runtime = Asking(rt => p = rt.Presence("palette", open, exit: exit));
            runtime.Step();

            // A build reads where the value got to by the end of the frame
            // before, so the fourth frame's build sees three steps: half of six.
            open = false;
            Frames(runtime, 4);
            Eq(PresenceState.Exiting, p.State, "on its way out");
            IsTrue(p.Mounted, "still built while it leaves");
            Near(0.5f, p.Progress, 0.01f, "half way out half way through");

            Frames(runtime, 3);
            Eq(PresenceState.Gone, p.State, "gone once its exit has run");
        });

        run("Presence: opened later, it enters from nothing; reopened mid-exit, it turns back", () =>
        {
            bool open = false;
            Presence p = default;
            var tr = Transition.Timed(0.1f, Easing.Linear);
            var runtime = Asking(rt => p = rt.Presence("palette", open, enter: tr, exit: tr));
            runtime.Step();

            open = true;
            Frames(runtime, 1);
            Eq(PresenceState.Entering, p.State, "entering");
            Frames(runtime, 6);
            Eq(PresenceState.Present, p.State, "and there");

            open = false;
            Frames(runtime, 3);
            float leaving = p.Progress;
            open = true;
            Frames(runtime, 2);
            float back = p.Progress;
            Frames(runtime, 1);
            IsTrue(p.Entering && back > 0.2f && p.Progress > back,
                $"turned back from about {leaving}, not restarted from 0 ({back}, then {p.Progress})");
        });

        run("Presence: appear makes a first ask enter from nothing", () =>
        {
            Presence p = default;
            var runtime = Asking(rt => p = rt.Presence("view", true, enter: Transition.Timed(0.1f), appear: true));
            runtime.Step();
            Eq(PresenceState.Entering, p.State, "entering on its first frame");
            Eq(0f, p.Progress, "from nothing");
        });

        run("PresenceStyle: takes a style from its start to rest, multiplying into what is there", () =>
        {
            var style = new Style { Opacity = 0.8f, Translate = new Vector2(5f, 0f) };
            PresenceStyle.FadeUp(20f).ApplyTo(style, 0.5f);
            Near(0.4f, style.Opacity, 1e-5f, "half faded, times the 0.8 it had");
            Eq(new Vector2(5f, 10f), style.Translate, "half the rise, added to its own offset");

            var pop = new Style();
            PresenceStyle.Pop(0.9f).ApplyTo(pop, 0f);
            Eq(new Vector2(0.9f, 0.9f), pop.Scale, "at the start of a pop");
            Eq(0f, pop.Opacity, "and invisible");

            var rest = new Style();
            PresenceStyle.Pop().ApplyTo(rest, 1f);
            IsTrue(rest.Translate == Vector2.Zero && rest.Scale == Vector2.One && rest.Opacity == 1f, "at rest it leaves the style alone");
        });

        // ── PresenceList ────────────────────────────────────────────────────

        run("PresenceList: the first ask is at rest; an added row enters, a removed one leaves where it was", () =>
        {
            var items = new List<string> { "a", "b", "c" };
            IReadOnlyList<PresenceItem<string>> rows = [];
            var tr = Transition.Timed(0.1f, Easing.Linear);
            var runtime = Asking(rt => rows = rt.PresenceList("inbox", items, s => s, tr, tr));
            runtime.Step();
            Eq("a,b,c", string.Join(",", rows.Select(r => r.Key)), "every row");
            IsTrue(rows.All(r => r.State == PresenceState.Present && r.Progress == 1f), "all at rest");

            items = ["a", "c", "d"];   // b accepted, d arrived
            runtime.Step();
            Eq("a,b,c,d", string.Join(",", rows.Select(r => r.Key)), "b still there, in its place");
            Eq(PresenceState.Exiting, rows[1].State, "b leaving");
            Eq("b", rows[1].Item, "with the item it was built from");
            Eq(PresenceState.Entering, rows[3].State, "d entering");

            Frames(runtime, 7);
            Eq("a,c,d", string.Join(",", rows.Select(r => r.Key)), "b gone once collapsed");
            IsTrue(rows.All(r => r.State == PresenceState.Present), "and the rest at rest");
        });

        run("PresenceList: rows added together stagger in", () =>
        {
            var items = new List<string>();
            IReadOnlyList<PresenceItem<string>> rows = [];
            var tr = Transition.Timed(0.1f, Easing.Linear);
            var runtime = Asking(rt => rows = rt.PresenceList("inbox", items, s => s, tr, tr, stagger: 0.05f));
            runtime.Step();
            items = ["a", "b", "c"];
            Frames(runtime, 8);   // 0.1 s each, starting 0.05 s apart
            IsTrue(rows[0].Progress > rows[1].Progress && rows[1].Progress > rows[2].Progress,
                $"each behind the one before ({rows[0].Progress:F2}, {rows[1].Progress:F2}, {rows[2].Progress:F2})");
        });

        run("PresenceList: appear makes the first ask enter", () =>
        {
            IReadOnlyList<PresenceItem<string>> rows = [];
            var runtime = Asking(rt => rows = rt.PresenceList("inbox", new[] { "a" }, s => s, appear: true));
            runtime.Step();
            Eq(PresenceState.Entering, rows[0].State, "entering");
        });

        run("PresenceList: two items with one key are refused", () =>
        {
            var runtime = Asking(rt => rt.PresenceList("inbox", new[] { "a", "a" }, s => s));
            try { runtime.Step(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("expected a refusal");
        });

        // ── CollapseBox ─────────────────────────────────────────────────────

        run("CollapseBox: a share of its row's height, clipping it; at 1 it follows the row", () =>
        {
            float progress = 1f;
            Box holder = null!;
            var runtime = BibuiRuntime.Create();
            runtime.SetBuild(() =>
            {
                var root = Root();
                using (runtime.PushParent(root))
                {
                    holder = runtime.CollapseBox("row-1", progress).Sized(Size.Fill);
                    UiNode.PushIntoParent(holder, new Box().Sized(Size.Px(200f), Size.Px(40f)));
                }
                return root;
            }, new SurfaceOptions { Width = 200, Height = 100 });
            Frames(runtime, 2);
            Eq(40f, holder.Extent.Y, "at rest it is its row");

            progress = 0.25f;
            Frames(runtime, 1);
            Eq(10f, holder.Extent.Y, "a quarter of it");

            progress = 0f;
            Frames(runtime, 1);
            Eq(0f, holder.Extent.Y, "and none of it");
        });

        // ── Transforms ──────────────────────────────────────────────────────

        run("Transform: paints moved, scaled and turned without moving its layout slot", () =>
        {
            Box b = null!;
            var style = new Style { BackgroundColor = "#FF0000", Translate = new Vector2(100f, 0f) };
            var runtime = BibuiRuntime.Create();
            runtime.SetBuild(() =>
            {
                var root = new Box { Style = new Style { BackgroundColor = "#000000" } }.Sized(Size.Fill, Size.Fill);
                b = new Box { Style = style }.Sized(Size.Px(40f), Size.Px(40f)).At(10f, 10f);
                UiNode.PushIntoParent(root, b);
                return root;
            }, new SurfaceOptions { Width = 200, Height = 100 });
            var frame = runtime.Frame();
            Eq(new Vector2(10f, 10f), b.Rect, "laid out where it was put");
            Eq(((byte)0, (byte)0, (byte)0), Pixel(frame, 30, 30), "not drawn there");
            Eq(((byte)255, (byte)0, (byte)0), Pixel(frame, 130, 30), "but 100 px to the right");

            style.Translate = Vector2.Zero;
            style.Scale = new Vector2(2f, 2f);
            frame = runtime.Frame();
            Eq(((byte)255, (byte)0, (byte)0), Pixel(frame, 5, 5), "doubled about its centre reaches past its corner");
            Eq(((byte)255, (byte)0, (byte)0), Pixel(frame, 65, 65), "on both sides");
        });

        run("Transform: the pointer finds a box where it is drawn, not where it was laid out", () =>
        {
            bool pressed = false;
            var runtime = BibuiRuntime.Create();
            runtime.SetBuild(() =>
            {
                var root = Root();
                var card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
                pressed |= runtime.InteractionsFor(card).Pressed;
                card.Sized(Size.Px(40f), Size.Px(40f)).At(10f, 10f);
                card.Style.Translate = new Vector2(100f, 0f);
                UiNode.PushIntoParent(root, card);
                return root;
            }, new SurfaceOptions { Width = 200, Height = 100 });
            Frames(runtime, 2);

            runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 30f, Y = 30f });
            runtime.Step();
            IsTrue(!pressed, "a press where it was laid out misses");
            runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 30f, Y = 30f });
            runtime.Step();

            runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 130f, Y = 30f });
            runtime.Step();
            IsTrue(pressed, "a press where it is drawn lands");
        });

        run("Transform: a turned box is hit inside its turned outline only", () =>
        {
            bool pressed = false;
            var runtime = BibuiRuntime.Create();
            runtime.SetBuild(() =>
            {
                var root = Root();
                var bar = runtime.BoxFromId("bar", InteractionKinds.LeftPress);
                pressed |= runtime.InteractionsFor(bar).Pressed;
                bar.Sized(Size.Px(100f), Size.Px(10f)).At(50f, 45f);    // a wide bar centred on (100, 50)
                bar.Style.Rotate = 90f;             // now tall
                UiNode.PushIntoParent(root, bar);
                return root;
            }, new SurfaceOptions { Width = 200, Height = 100 });
            Frames(runtime, 2);

            void Press(float x, float y)
            {
                runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y });
                runtime.Step();
                runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = x, Y = y });
                runtime.Step();
            }
            Press(60f, 50f);
            IsTrue(!pressed, "the end of the bar as laid out is empty now");
            Press(100f, 10f);
            IsTrue(pressed, "the top of the bar as turned is the bar");
        });

        run("Transform: a box moved out of its clipping parent is not hit outside it", () =>
        {
            bool pressed = false;
            var runtime = BibuiRuntime.Create();
            runtime.SetBuild(() =>
            {
                var root = Root();
                var list = runtime.BoxFromId("list", InteractionKinds.Clip);
                var row = runtime.BoxFromId("row", InteractionKinds.LeftPress);
                pressed |= runtime.InteractionsFor(row).Pressed;
                runtime.InteractionsFor(list);
                list.Sized(Size.Px(100f), Size.Px(50f));
                row.Sized(Size.Px(100f), Size.Px(20f));
                row.Style.Translate = new Vector2(80f, 0f);   // half of it past the list's edge
                UiNode.PushIntoParent(list, row);
                UiNode.PushIntoParent(root, list);
                return root;
            }, new SurfaceOptions { Width = 200, Height = 100 });
            Frames(runtime, 2);

            runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 150f, Y = 10f });
            runtime.Step();
            IsTrue(!pressed, "the part cut off takes nothing");
            runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = 150f, Y = 10f });
            runtime.Step();

            runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = 90f, Y = 10f });
            runtime.Step();
            IsTrue(pressed, "the part inside does");
        });

        run("Transform: a click by name lands on the box as drawn", () =>
        {
            bool pressed = false;
            var runtime = BibuiRuntime.Create();
            runtime.SetBuild(() =>
            {
                var root = Root();
                var card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
                pressed |= runtime.InteractionsFor(card).Pressed;
                card.Sized(Size.Px(40f), Size.Px(40f)).At(10f, 10f);
                card.Style.Translate = new Vector2(100f, 0f);
                card.Style.Scale = new Vector2(0.5f, 0.5f);
                UiNode.PushIntoParent(root, card);
                return root;
            }, new SurfaceOptions { Width = 200, Height = 100 });
            Frames(runtime, 2);
            runtime.DispatchClick("card");
            runtime.Step();
            IsTrue(pressed, "pressed");
        });

        // ── Theme crossfade ─────────────────────────────────────────────────

        run("SetTheme with a crossfade: the page fades from one theme's colours to the other's", () =>
        {
            var dark = new ThemeTokens { Colors = new ColorTokens { Background = "#000000" } };
            var light = new ThemeTokens { Colors = new ColorTokens { Background = "#FFFFFF" } };
            var runtime = BibuiRuntime.Create();
            runtime.SetBuild(() => new Box { Style = new Style { BackgroundColor = UiColor.Background } }.Sized(Size.Fill, Size.Fill),
                dark, new SurfaceOptions { Width = 20, Height = 20 });
            runtime.Step();

            runtime.SetTheme(light, Transition.Timed(0.1f, Easing.Linear));
            var start = Pixel(runtime.Frame(), 5, 5);
            Eq(((byte)0, (byte)0, (byte)0), start, "the first frame is still the old theme");
            Frames(runtime, 2);
            var mid = Pixel(runtime.Frame(), 5, 5);
            IsTrue(mid.R is > 0 and < 255, $"part way ({mid.R})");
            Frames(runtime, 6);
            Eq(((byte)255, (byte)255, (byte)255), Pixel(runtime.Frame(), 5, 5), "and arrives at the new one");
            Eq(light, runtime.Theme, "the theme asked for is the theme");
        });

        // ── Reduced motion ──────────────────────────────────────────────────

        run("ReducedMotion: timed, spring, presence, list, colour and plain Animate all arrive in one frame", () =>
        {
            float target = 0f;
            bool open = true;
            var items = new List<string> { "a" };
            float timed = 0f, spring = 0f, plain = 0f;
            Presence p = default;
            IReadOnlyList<PresenceItem<string>> rows = [];
            UiColor color = default;
            var runtime = Asking(rt =>
            {
                timed = rt.Animate("m", "timed", target, Transition.Timed(1f));
                spring = rt.Animate("m", "spring", target, Transition.OnSpring(Spring.Gentle));
                plain = rt.Animate("m", "plain", target, Ease.Rate(1f, rt.Dt));
                p = rt.Presence("m", open, exit: Transition.Timed(1f));
                rows = rt.PresenceList("m", items, s => s, exit: Transition.Timed(1f));
                color = rt.AnimateColor("m", "c", target > 0 ? "#FFFFFF" : "#000000", Transition.Timed(1f));
            });
            runtime.ReducedMotion = true;
            runtime.Step();

            target = 1f; open = false; items = [];
            Frames(runtime, 2);   // one to ask, one to read the step back
            Eq(1f, timed, "timed");
            Eq(1f, spring, "spring");
            Eq(1f, plain, "plain Animate");
            Eq(PresenceState.Gone, p.State, "presence");
            Eq(0, rows.Count, "list");
            Eq("#FFFFFFFF", color.Resolve(runtime.Theme.Colors), "colour");
        });
    }
}
