using System.Numerics;

namespace Bibui;

// ── Motion: how a value travels, and how a thing arrives and leaves ──────────
// The runtime has always eased by exponential approach — a fixed share of the
// remaining gap per unit of time. That suits a hover, which has no start or end
// anyone notices, and nothing else: a view sliding in should take a fixed time
// along a curve a designer can name, and a selection bar that is retargeted
// half way should keep its momentum rather than start over. These are the
// values that say which, and they are only descriptions: the runtime steps
// them (BibuiRuntime.Animate with a Transition), at the end of the frame, on
// the same nominal clock as everything else, so a run is deterministic.

/// <summary>
/// A cubic Bézier timing curve from (0,0) to (1,1), as CSS's
/// <c>cubic-bezier(x1, y1, x2, y2)</c> spells it. Maps how far through its time
/// an animation is to how far through its distance.
/// </summary>
public readonly record struct Easing(float X1, float Y1, float X2, float Y2)
{
    /// <summary>Constant speed.</summary>
    public static Easing Linear => new(0f, 0f, 1f, 1f);

    /// <summary>CSS <c>ease</c>.</summary>
    public static Easing Ease => new(0.25f, 0.1f, 0.25f, 1f);

    /// <summary>CSS <c>ease-in</c>: leaves slowly. For things on their way out.</summary>
    public static Easing EaseIn => new(0.42f, 0f, 1f, 1f);

    /// <summary>CSS <c>ease-out</c>: arrives softly. For things coming in.</summary>
    public static Easing EaseOut => new(0f, 0f, 0.58f, 1f);

    /// <summary>CSS <c>ease-in-out</c>.</summary>
    public static Easing EaseInOut => new(0.42f, 0f, 0.58f, 1f);

    /// <summary>Material's standard curve: quick to start, a long soft landing. The default.</summary>
    public static Easing Standard => new(0.2f, 0f, 0f, 1f);

    /// <summary>Material's emphasized decelerate: for a view or panel entering.</summary>
    public static Easing Emphasized => new(0.05f, 0.7f, 0.1f, 1f);

    /// <summary>Material's emphasized accelerate: for a view or panel leaving.</summary>
    public static Easing EmphasizedAccelerate => new(0.3f, 0f, 0.8f, 0.15f);

    /// <summary>
    /// How far along the distance the curve is at <paramref name="t"/> (0..1
    /// of the time). Clamped at both ends, and exact there: 0 gives 0 and 1
    /// gives 1, so a finished animation lands on its target and not a hair off.
    /// </summary>
    public float Evaluate(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        if (X1 == Y1 && X2 == Y2) return t;   // a straight line, whatever the handles

        // Solve x(s) = t for the curve parameter s: Newton from s = t, which
        // converges in two or three steps on any curve a designer draws, and
        // bisection when the slope is too flat for Newton to be trusted.
        float s = t;
        for (int i = 0; i < 8; i++)
        {
            float x = Bezier(s, X1, X2) - t;
            if (MathF.Abs(x) < 1e-6f) return Bezier(s, Y1, Y2);
            float dx = Slope(s, X1, X2);
            if (MathF.Abs(dx) < 1e-6f) break;
            s -= x / dx;
            if (s is < 0f or > 1f) break;
        }

        float lo = 0f, hi = 1f;
        s = t;
        for (int i = 0; i < 32; i++)
        {
            float x = Bezier(s, X1, X2);
            if (MathF.Abs(x - t) < 1e-6f) break;
            if (x < t) lo = s; else hi = s;
            s = (lo + hi) / 2f;
        }
        return Bezier(s, Y1, Y2);
    }

    // One coordinate of the curve, with its end points fixed at 0 and 1.
    private static float Bezier(float s, float p1, float p2)
    {
        float u = 1f - s;
        return 3f * u * u * s * p1 + 3f * u * s * s * p2 + s * s * s;
    }

    private static float Slope(float s, float p1, float p2)
    {
        float u = 1f - s;
        return 3f * u * u * p1 + 6f * u * s * (p2 - p1) + 3f * s * s * (1f - p2);
    }
}

/// <summary>
/// A damped spring, for motion that is retargeted while it moves — a selection
/// bar following the keyboard, a card thrown by a drag. It carries its velocity
/// from one target to the next, so a change of mind bends the path instead of
/// restarting it.
/// </summary>
/// <param name="Stiffness">How hard it pulls toward the target, per unit of distance.</param>
/// <param name="Damping">How hard it resists its own speed.</param>
/// <param name="Mass">How heavy the thing moved is.</param>
public readonly record struct Spring(float Stiffness, float Damping, float Mass = 1f)
{
    /// <summary>Critically damped and quick: no overshoot, about a fifth of a second. For UI that follows input.</summary>
    public static Spring Snappy => FromResponse(0.22f, 1f);

    /// <summary>Critically damped and unhurried: no overshoot, about a third of a second.</summary>
    public static Spring Gentle => FromResponse(0.35f, 1f);

    /// <summary>Under-damped: overshoots a little and settles. For something that should feel thrown.</summary>
    public static Spring Bouncy => FromResponse(0.4f, 0.6f);

    /// <summary>
    /// A spring by how it feels rather than by its physics, as SwiftUI spells
    /// it: <paramref name="response"/> is roughly how long one swing takes, in
    /// seconds, and <paramref name="dampingRatio"/> is 1 for no overshoot,
    /// less for a bounce.
    /// </summary>
    public static Spring FromResponse(float response, float dampingRatio)
    {
        if (response <= 0f) throw new ArgumentOutOfRangeException(nameof(response), response, "A spring's response is a time, above zero.");
        float omega = 2f * MathF.PI / response;
        return new Spring(omega * omega, 2f * dampingRatio * omega);
    }
}

/// <summary>
/// The steps the runtime's animations take each frame, as pure functions: a
/// value and its target in, the value <paramref name="dt"/> seconds on out.
/// <c>Animate</c>, hover and press, view scrolling and transitions all step
/// through these, so a value eased by hand moves exactly as one the runtime
/// eases, and needs nothing kept by the runtime.
///
/// None of them arrives on its own: exponential approach only nears its
/// target. Snap to the target once the gap is under what anyone can see (0.01
/// of a 0..1 fade, half a pixel of an offset), as the runtime does. Reduced
/// motion and <see cref="Theming.MotionEasing.None"/> are the caller's to
/// honour, by taking the target at once.
/// </summary>
public static class Ease
{
    /// <summary>
    /// The share of the gap exponential approach closes in <paramref name="dt"/>
    /// seconds at <paramref name="speed"/> halvings of the gap a second:
    /// 1 − 2^(−speed·dt). The same share every frame of the same length, so
    /// the motion depends on time, not on how many frames it took.
    /// </summary>
    public static float ExponentialRate(float speed, float dt) => 1f - MathF.Pow(2f, -speed * dt);

    /// <summary>
    /// <paramref name="current"/> moved towards <paramref name="target"/> by
    /// exponential approach: <paramref name="speed"/> halvings of the gap a
    /// second, for <paramref name="dt"/> seconds.
    /// </summary>
    public static float Exponential(float current, float target, float speed, float dt) =>
        current + (target - current) * ExponentialRate(speed, dt);

    /// <inheritdoc cref="Exponential(float, float, float, float)"/>
    public static Vector2 Exponential(Vector2 current, Vector2 target, float speed, float dt) =>
        current + (target - current) * ExponentialRate(speed, dt);

    /// <inheritdoc cref="Exponential(float, float, float, float)"/>
    public static Vector4 Exponential(Vector4 current, Vector4 target, float speed, float dt) =>
        current + (target - current) * ExponentialRate(speed, dt);

    // Springs are integrated in steps no longer than this, so a stiff spring
    // stays stable at 30 Hz as well as at 144.
    internal const float SpringStep = 1f / 240f;

    /// <summary>
    /// <paramref name="current"/> and its <paramref name="velocity"/> moved
    /// <paramref name="dt"/> seconds along <paramref name="spring"/>, pulled
    /// towards <paramref name="target"/>, each component on its own. The
    /// velocity is the spring's state: keep it with the value, and a new target
    /// bends the path instead of restarting it.
    /// </summary>
    public static void Spring(ref Vector4 current, ref Vector4 velocity, Vector4 target, Spring spring, float dt)
    {
        if (dt <= 0f) return;
        int n = Math.Max(1, (int)MathF.Ceiling(dt / SpringStep));
        float h = dt / n;
        for (int i = 0; i < n; i++)
        {
            // Semi-implicit Euler: velocity first, then position from it.
            Vector4 force = -spring.Stiffness * (current - target) - spring.Damping * velocity;
            velocity += force / spring.Mass * h;
            current += velocity * h;
        }
    }

    /// <inheritdoc cref="Spring(ref Vector4, ref Vector4, Vector4, Bibui.Spring, float)"/>
    public static void Spring(ref float current, ref float velocity, float target, Spring spring, float dt)
    {
        var c = new Vector4(current, 0f, 0f, 0f);
        var v = new Vector4(velocity, 0f, 0f, 0f);
        Spring(ref c, ref v, new Vector4(target, 0f, 0f, 0f), spring, dt);
        (current, velocity) = (c.X, v.X);
    }
}

/// <summary>Which of the ways a <see cref="Transition"/> travels.</summary>
public enum TransitionKind
{
    /// <summary>A fixed share of the gap per unit of time: what <c>Animate</c> without a transition does.</summary>
    Exponential,

    /// <summary>A fixed time along an <see cref="Easing"/> curve.</summary>
    Timed,

    /// <summary>A damped <see cref="Bibui.Spring"/>, keeping its velocity across targets.</summary>
    Spring,

    /// <summary>No travel: the value is its target from the next frame.</summary>
    Instant,
}

/// <summary>
/// How a value travels to a new target: along a timed curve, on a spring, or by
/// exponential approach, after an optional delay. A value, so a view can keep
/// its transitions in static fields and share them.
/// </summary>
public readonly record struct Transition
{
    /// <summary>Which way it travels.</summary>
    public TransitionKind Kind { get; init; }

    /// <summary>For <see cref="TransitionKind.Timed"/>: seconds from start to target.</summary>
    public float Duration { get; init; }

    /// <summary>For <see cref="TransitionKind.Timed"/>: the curve it follows.</summary>
    public Easing Easing { get; init; }

    /// <summary>For <see cref="TransitionKind.Spring"/>: the spring.</summary>
    public Spring Spring { get; init; }

    /// <summary>For <see cref="TransitionKind.Exponential"/>: halvings of the gap per second.</summary>
    public float Speed { get; init; }

    /// <summary>
    /// Seconds to hold still after the target changes before moving: how a list
    /// staggers its rows in. Counted again from every new target.
    /// </summary>
    public float Delay { get; init; }

    /// <summary><paramref name="seconds"/> along <paramref name="easing"/>, <see cref="Easing.Standard"/> when none is given.</summary>
    public static Transition Timed(float seconds, Easing? easing = null, float delay = 0f)
    {
        if (seconds <= 0f) throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A timed transition lasts longer than zero; Transition.Instant is the one that does not.");
        return new() { Kind = TransitionKind.Timed, Duration = seconds, Easing = easing ?? Easing.Standard, Delay = Math.Max(0f, delay) };
    }

    /// <summary>On <paramref name="spring"/>, <see cref="Bibui.Spring.Snappy"/> when none is given.</summary>
    public static Transition OnSpring(Spring? spring = null, float delay = 0f)
        => new() { Kind = TransitionKind.Spring, Spring = spring ?? Bibui.Spring.Snappy, Delay = Math.Max(0f, delay) };

    /// <summary>Exponential approach at <paramref name="speed"/> halvings a second.</summary>
    public static Transition Exponential(float speed = 20f)
        => new() { Kind = TransitionKind.Exponential, Speed = speed };

    /// <summary>No travel.</summary>
    public static Transition Instant => new() { Kind = TransitionKind.Instant };

    /// <summary>The same transition, starting <paramref name="seconds"/> later.</summary>
    public Transition WithDelay(float seconds) => this with { Delay = Math.Max(0f, seconds) };
}

/// <summary>Where something is in its life on screen.</summary>
public enum PresenceState
{
    /// <summary>Not there, and not leaving: build nothing.</summary>
    Gone,

    /// <summary>Arriving: build it, at <see cref="Presence.Progress"/>.</summary>
    Entering,

    /// <summary>There, at rest.</summary>
    Present,

    /// <summary>Leaving, but still to be built until it has gone.</summary>
    Exiting,
}

/// <summary>
/// Whether to build something, and how far into the screen it is. The answer
/// <c>BibuiRuntime.Presence</c> gives: build while <see cref="Mounted"/>, and
/// drive opacity, offset or height from <see cref="Progress"/>.
/// </summary>
public readonly record struct Presence(float Progress, PresenceState State)
{
    /// <summary>Whether to build it this frame: everything but <see cref="PresenceState.Gone"/>.</summary>
    public bool Mounted => State != PresenceState.Gone;

    /// <summary>Whether it is on its way in.</summary>
    public bool Entering => State == PresenceState.Entering;

    /// <summary>Whether it is on its way out — still drawn, but no longer to be acted on.</summary>
    public bool Exiting => State == PresenceState.Exiting;
}

/// <summary>
/// One row of a list as <c>BibuiRuntime.PresenceList</c> hands it back: the
/// item (kept for a row that is leaving, after the caller's own list has
/// dropped it), its key, and how far into the list it is.
/// </summary>
public readonly record struct PresenceItem<T>(string Key, T Item, float Progress, PresenceState State)
{
    /// <summary>Whether it is on its way in.</summary>
    public bool Entering => State == PresenceState.Entering;

    /// <summary>Whether it is on its way out: drawn, collapsing, not to be acted on.</summary>
    public bool Exiting => State == PresenceState.Exiting;
}

/// <summary>
/// What something looks like when it is not there yet: faded, moved and scaled.
/// <see cref="ApplyTo"/> takes a style from there to rest by a presence's
/// progress, so an enter and an exit are one line each.
/// </summary>
public readonly record struct PresenceStyle
{
    /// <summary>The opacity it starts from.</summary>
    public float Opacity { get; init; }

    /// <summary>Where it starts from, in logical px, relative to where it rests.</summary>
    public Vector2 Offset { get; init; }

    /// <summary>The scale it starts from.</summary>
    public float Scale { get; init; }

    public PresenceStyle()
    {
        Opacity = 0f;
        Offset = Vector2.Zero;
        Scale = 1f;
    }

    /// <summary>A plain fade.</summary>
    public static PresenceStyle Fade => new();

    /// <summary>Faded and <paramref name="distance"/> px below: rises into place.</summary>
    public static PresenceStyle FadeUp(float distance = 12f) => new() { Offset = new Vector2(0f, distance) };

    /// <summary>Faded and <paramref name="distance"/> px above: drops into place.</summary>
    public static PresenceStyle FadeDown(float distance = 12f) => new() { Offset = new Vector2(0f, -distance) };

    /// <summary>Faded and <paramref name="distance"/> px to the right: slides in leftwards.</summary>
    public static PresenceStyle SlideFromRight(float distance = 24f) => new() { Offset = new Vector2(distance, 0f) };

    /// <summary>Faded and <paramref name="distance"/> px to the left: slides in rightwards.</summary>
    public static PresenceStyle SlideFromLeft(float distance = 24f) => new() { Offset = new Vector2(-distance, 0f) };

    /// <summary>Faded and a little small: grows into place.</summary>
    public static PresenceStyle Pop(float scale = 0.96f) => new() { Scale = scale };

    /// <summary>
    /// Takes <paramref name="style"/> from this toward rest by
    /// <paramref name="progress"/> (0 = this, 1 = at rest). Multiplies into
    /// what the style already has rather than replacing it, so a presence
    /// composes with an opacity or offset the build set for its own reasons.
    /// </summary>
    public void ApplyTo(Style style, float progress)
    {
        ArgumentNullException.ThrowIfNull(style);
        float p = Math.Clamp(progress, 0f, 1f);
        if (p >= 1f) return;
        style.Opacity *= Opacity + (1f - Opacity) * p;
        style.Translate += Offset * (1f - p);
        float s = Scale + (1f - Scale) * p;
        style.Scale *= s;
    }
}

/// <summary>
/// Colour mixing in Oklab, where equal steps look equal: a crossfade between
/// two colours passes through what the eye expects instead of the muddy middle
/// a channel-by-channel blend of sRGB gives.
/// </summary>
public static class ColorMix
{
    /// <summary>
    /// <paramref name="from"/> toward <paramref name="to"/> by
    /// <paramref name="t"/>, in Oklab, alpha linearly. Exact at the ends. A
    /// fully transparent end takes the other end's hue, so a colour fading in
    /// from nothing holds its hue and only gains opacity.
    /// </summary>
    public static Rgba Oklab(Rgba from, Rgba to, float t)
    {
        if (t <= 0f) return from;
        if (t >= 1f) return to;
        if (from.A == 0) from = to.WithAlpha(0);
        else if (to.A == 0) to = from.WithAlpha(0);
        Vector4 a = ToOklab(from), b = ToOklab(to);
        return FromOklab(a + (b - a) * t);
    }

    /// <summary>A colour as (L, a, b, alpha), alpha 0..1.</summary>
    public static Vector4 ToOklab(Rgba c)
    {
        float r = ToLinear(c.R / 255f), g = ToLinear(c.G / 255f), bl = ToLinear(c.B / 255f);
        float l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * bl);
        float m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * bl);
        float s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * bl);
        return new Vector4(
            0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s,
            c.A / 255f);
    }

    /// <summary>Back from (L, a, b, alpha) to a colour, clamped into sRGB.</summary>
    public static Rgba FromOklab(Vector4 lab)
    {
        float l = lab.X + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
        float m = lab.X - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
        float s = lab.X - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
        l = l * l * l; m = m * m * m; s = s * s * s;
        float r = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        float g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        float b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
        return new Rgba(Byte(FromLinear(r)), Byte(FromLinear(g)), Byte(FromLinear(b)), Byte(lab.W));
    }

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float FromLinear(float c) => c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
