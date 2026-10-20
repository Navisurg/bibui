using System.Numerics;
using Bibui.Core.Theming;

namespace Bibui.Core.Runtime;

public sealed partial class BibuiRuntime
{
    // ── Motion ────────────────────────────────────────────────────────────────
    // Animate(id, property, target, rate) eases by exponential approach and
    // nothing else. This is the rest: a value that takes a set time along a
    // named curve, or rides a spring that keeps its speed when the target
    // moves; a colour crossfading in Oklab; something that has to stay on
    // screen while it leaves; a list whose rows grow in and collapse out; a
    // theme swap that fades instead of cutting.
    //
    // It keeps every rule Animate keeps. Keyed by (id, property), evicted when
    // nobody asks. A value asked for the first time is at its target. Asking
    // does not advance it: everything steps once, at the end of the frame, on
    // the frame's Dt, which the window sets before the build — so a frame's two
    // builds read one answer, and a headless run is deterministic. And whatever the transition, ReducedMotion (or a theme whose
    // easing is None) closes every gap in one step.

    /// <summary>
    /// When true, everything that would animate arrives in one step instead:
    /// <see cref="Animate(string, string, float, float?, float)"/>, transitions,
    /// presence, hover and press, theme crossfades and the renderer's own
    /// eased values. For people who ask their system for less motion, and for
    /// tests that want settled frames. Starts from <c>BIBUI_REDUCED_MOTION=1</c>.
    /// </summary>
    public bool ReducedMotion
    {
        get => _reducedMotion;
        set
        {
            _reducedMotion = value;
            RequestFrame();
        }
    }

    private bool _reducedMotion = Environment.GetEnvironmentVariable("BIBUI_REDUCED_MOTION") is "1" or "true";

    /// <summary>
    /// This frame's rates, one per kind of motion, computed once as the build
    /// begins: step a value of that kind with <see cref="Ease.Exponential(float, float, float)"/>
    /// and it moves as the runtime's own do. All 1 when motion is instant.
    /// </summary>
    public MotionRates Rates { get; private set; }

    /// <summary>
    /// The rate for a speed of the caller's own (halvings of the gap a second)
    /// this frame: 1 when motion is instant, as every rate in <see cref="Rates"/> is.
    /// </summary>
    public float Rate(float speed) => Instant ? 1f : Ease.Rate(speed, Dt);

    /// <summary>Whether this frame snaps rather than travels.</summary>
    private bool Instant => _reducedMotion || _theme.Motion.Easing == MotionEasing.None;

    /// <summary>
    /// One travelling value, up to four channels wide: a float uses X, a point
    /// X and Y, a colour all four as Oklab and alpha. Every channel follows the
    /// same transition, so a colour's hue and lightness arrive together.
    /// </summary>
    private sealed class MotionCell
    {
        public Vector4 Current;
        public Vector4 Target;
        public Vector4 From;       // where the current leg began, for a timed curve
        public Vector4 Velocity;   // per second, for a spring; kept across targets
        public float Elapsed;      // seconds since the current leg began, delay included
        public float Epsilon;
        public Transition Transition;
    }

    private readonly ResourceCache<AnimKey, MotionCell> _motion = new(keepFrames: 2);

    /// <summary>
    /// The cell for <paramref name="key"/>, retargeted at
    /// <paramref name="target"/>. A new one starts at <paramref name="initial"/>,
    /// which is the target itself unless the caller wants it to travel there.
    /// </summary>
    private MotionCell Drive(AnimKey key, Vector4 target, Vector4 initial, in Transition transition, float epsilon)
    {
        var cell = _motion.FromKey(key, initial,
            static v => new MotionCell { Current = v, Target = v, From = v });
        if (cell.Target != target)
        {
            // A new leg from wherever it has got to. Velocity is kept: a spring
            // retargeted mid-flight bends toward the new target, it does not stop.
            cell.From = cell.Current;
            cell.Target = target;
            cell.Elapsed = 0f;
        }
        cell.Transition = transition.Kind switch
        {
            // A transition left at its defaults still has to move.
            TransitionKind.Exponential when transition.Speed <= 0f => transition with { Speed = _theme.Motion.Default },
            TransitionKind.Spring when transition.Spring.Stiffness <= 0f || transition.Spring.Mass <= 0f => transition with { Spring = Bibui.Core.Spring.Snappy },
            _ => transition,
        };
        cell.Epsilon = epsilon;
        return cell;
    }

    /// <summary>
    /// Where <paramref name="property"/> of <paramref name="id"/> has got to on
    /// its way to <paramref name="target"/>, travelling by
    /// <paramref name="transition"/>. The same contract as
    /// <see cref="Animate(string, string, float, float?, float)"/>: at its
    /// target when first asked, stepped at the end of the frame, kept while
    /// asked for. A new target starts a new leg from wherever the value is,
    /// and its <see cref="Transition.Delay"/> counts again from there.
    /// </summary>
    public float Animate(string id, string property, float target, Transition transition, float epsilon = 0.001f)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(property);
        var t = new Vector4(target, 0f, 0f, 0f);
        return Drive(AnimKey.For(id, property), t, t, transition, epsilon).Current.X;
    }

    /// <summary>
    /// A point travelling to <paramref name="target"/>: a selection bar gliding
    /// between rows, a panel's position. Both coordinates share one leg.
    /// <paramref name="epsilon"/> is in px.
    /// </summary>
    public Vector2 Animate(string id, string property, Vector2 target, Transition transition, float epsilon = 0.05f)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(property);
        var t = new Vector4(target, 0f, 0f);
        var c = Drive(AnimKey.For(id, property), t, t, transition, epsilon).Current;
        return new Vector2(c.X, c.Y);
    }

    /// <summary>
    /// A colour crossfading to <paramref name="target"/> in Oklab, which keeps
    /// the middle of a fade looking like the colours on either side of it. A
    /// token is resolved against the theme as it is now, so swapping the theme
    /// fades this colour to the new one's. By <paramref name="transition"/>, or
    /// a short timed fade.
    /// </summary>
    public UiColor AnimateColor(string id, string property, UiColor target, Transition? transition = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(property);
        var (r, g, b, a) = target.ResolveColor(_theme.Colors);
        Vector4 lab = ColorMix.ToOklab(new Rgba(r, g, b, a));
        var key = AnimKey.For(id, property);

        // Fading to or from nothing keeps the hue of the end that is there,
        // so it gains or loses opacity instead of passing through grey.
        if (_motion.Entries.TryGetValue(key, out var existing))
        {
            var c = existing.Value;
            if (a == 0)
                lab = c.Target.W == 0f ? c.Target : new Vector4(c.Current.X, c.Current.Y, c.Current.Z, 0f);
            else if (c.Current.W == 0f && c.Target != lab)
                c.Current = c.From = new Vector4(lab.X, lab.Y, lab.Z, 0f);
        }

        var cell = Drive(key, lab, lab, transition ?? DefaultColorTransition, 0.0005f);
        var rgba = ColorMix.FromOklab(cell.Current);
        return UiColor.Rgba(rgba.R, rgba.G, rgba.B, rgba.A);
    }

    private static readonly Transition DefaultColorTransition = Transition.Timed(0.18f, Easing.Standard);
    private static readonly Transition DefaultEnter = Transition.Timed(0.22f, Easing.Emphasized);
    private static readonly Transition DefaultExit = Transition.Timed(0.16f, Easing.EmphasizedAccelerate);

    /// <summary>
    /// Whether to build <paramref name="id"/> this frame, and how far in it is.
    /// Say whether it should be there; while it leaves, the answer stays
    /// <see cref="Presence.Mounted"/> until its exit has run, so a view, a
    /// palette or a toast fades out without the caller keeping it alive.
    ///
    /// <para>Ask every frame, present or not — the asking is what keeps it
    /// stepping. Asked for the first time, it is where it should be: present
    /// and at rest, or gone, unless <paramref name="appear"/> asks a first
    /// present to enter from nothing (a view opened for the first time).</para>
    ///
    /// <para><paramref name="enter"/> and <paramref name="exit"/> default to a
    /// short emphasized ease in and a quicker accelerating one out.</para>
    /// </summary>
    public Presence Presence(string id, bool present, Transition? enter = null, Transition? exit = null, bool appear = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        float target = present ? 1f : 0f;
        var initial = new Vector4(present && appear ? 0f : target, 0f, 0f, 0f);
        var cell = Drive(AnimKey.For(id, "presence"), new Vector4(target, 0f, 0f, 0f), initial,
            present ? enter ?? DefaultEnter : exit ?? DefaultExit, 0.001f);
        return new Presence(Math.Clamp(cell.Current.X, 0f, 1f), StateOf(present, cell.Current.X));
    }

    private static PresenceState StateOf(bool present, float progress) => present
        ? (progress >= 1f ? PresenceState.Present : PresenceState.Entering)
        : (progress <= 0f ? PresenceState.Gone : PresenceState.Exiting);

    /// <summary>A list's rows, the ones leaving included, in the order they are drawn.</summary>
    private sealed class PresenceListCell
    {
        public readonly List<string> Order = new();
        public readonly Dictionary<string, Row> Rows = new();
        public object? Result;   // List<PresenceItem<T>>, reused from frame to frame
        public bool Seeded;

        public sealed class Row
        {
            public readonly MotionCell Motion = new();
            public object? Item;
            public bool Present;
        }
    }

    private readonly ResourceCache<string, PresenceListCell> _lists = new(keepFrames: 2);

    /// <summary>
    /// <paramref name="items"/> as rows that grow in when they are added and
    /// collapse out when they are removed — the rows that are leaving included,
    /// in the place they were, with the item they were built from, so the
    /// caller can go on dropping items from its own list the moment they are
    /// gone from its point of view.
    ///
    /// <para>Rows added in the same frame start <paramref name="stagger"/>
    /// seconds apart, in order (counted up to the fifteenth, so a long list does
    /// not keep its last rows waiting). The first ask puts every row at rest,
    /// unless <paramref name="appear"/> asks them all to enter — a list shown
    /// as its view opens.</para>
    ///
    /// <para>The list handed back is reused by the next ask for the same
    /// <paramref name="id"/>: read it in this frame's build, don't keep it.
    /// Pair each row with <see cref="CollapseBox"/> to animate its height.</para>
    /// </summary>
    public IReadOnlyList<PresenceItem<T>> PresenceList<T>(
        string id,
        IReadOnlyList<T> items,
        Func<T, string> keyOf,
        Transition? enter = null,
        Transition? exit = null,
        float stagger = 0f,
        bool appear = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keyOf);
        var enterT = enter ?? DefaultEnter;
        var exitT = exit ?? DefaultExit;

        var cell = _lists.FromKey(id, 0, static _ => new PresenceListCell());
        bool first = !cell.Seeded;
        cell.Seeded = true;

        // Who is here now, and which of them are new.
        var keys = new string[items.Count];
        var present = new HashSet<string>(items.Count);
        int entering = 0;
        for (int i = 0; i < items.Count; i++)
        {
            string key = keyOf(items[i]) ?? throw new InvalidOperationException($"PresenceList '{id}': an item's key is null.");
            if (!present.Add(key)) throw new InvalidOperationException($"PresenceList '{id}': two items share the key '{key}'. Keys name rows, so they must be distinct.");
            keys[i] = key;

            if (!cell.Rows.TryGetValue(key, out var row))
            {
                float start = first && !appear ? 1f : 0f;
                row = new PresenceListCell.Row();
                row.Motion.Current = row.Motion.From = row.Motion.Target = new Vector4(start, 0f, 0f, 0f);
                cell.Rows[key] = row;
            }
            row.Item = items[i];
            row.Present = true;
            row.Motion.Epsilon = 0.001f;
            if (row.Motion.Target.X != 1f)
            {
                // New, or coming back while it was leaving: a leg in from
                // wherever it is, behind the rows that started before it.
                row.Motion.From = row.Motion.Current;
                row.Motion.Target = Vector4.UnitX;
                row.Motion.Elapsed = 0f;
                row.Motion.Transition = stagger > 0f
                    ? enterT.WithDelay(enterT.Delay + Math.Min(entering, 15) * stagger)
                    : enterT;
                entering++;
            }
        }

        // Who has left: still drawn, on their way out, where they were.
        foreach (var (key, row) in cell.Rows)
        {
            if (present.Contains(key)) continue;
            row.Present = false;
            if (row.Motion.Target.X != 0f)
            {
                row.Motion.From = row.Motion.Current;
                row.Motion.Target = Vector4.Zero;
                row.Motion.Elapsed = 0f;
                row.Motion.Transition = exitT;
            }
        }

        MergeOrder(cell.Order, keys, present);

        var result = cell.Result as List<PresenceItem<T>> ?? new List<PresenceItem<T>>();
        cell.Result = result;
        result.Clear();
        foreach (string key in cell.Order)
        {
            var row = cell.Rows[key];
            float p = Math.Clamp(row.Motion.Current.X, 0f, 1f);
            result.Add(new PresenceItem<T>(key, (T)row.Item!, p, StateOf(row.Present, row.Motion.Current.X)));
        }
        return result;
    }

    /// <summary>
    /// Rewrites <paramref name="order"/> (last frame's rows, leaving ones
    /// included) as <paramref name="keys"/> (this frame's), with every row that
    /// is no longer in <paramref name="keys"/> kept right after the row it
    /// followed. One pass, so a long list costs what it is long.
    /// </summary>
    private static void MergeOrder(List<string> order, string[] keys, HashSet<string> present)
    {
        // A leaving row hangs off the nearest row before it that stays, or off
        // the front. Rows that left together keep their order.
        Dictionary<string, List<string>>? after = null;
        List<string>? front = null;
        string? anchor = null;
        foreach (string key in order)
        {
            if (present.Contains(key)) { anchor = key; continue; }
            if (anchor is null) (front ??= new()).Add(key);
            else
            {
                after ??= new();
                if (!after.TryGetValue(anchor, out var list)) after[anchor] = list = new();
                list.Add(key);
            }
        }

        order.Clear();
        if (front is not null) order.AddRange(front);
        foreach (string key in keys)
        {
            order.Add(key);
            if (after is not null && after.TryGetValue(key, out var leaving)) order.AddRange(leaving);
        }
    }

    /// <summary>
    /// A box whose height is <paramref name="progress"/> of what its children
    /// need, clipping them: a list row growing in or collapsing out. At 1 it is
    /// sized by its children like any other box, so a row at rest follows its
    /// content at once. Below 1 it is that share of the height its children
    /// had last frame (a row on its very first frame has none yet, and grows
    /// from the second).
    ///
    /// <para>Retained by <paramref name="id"/>, and claims
    /// <see cref="InteractionKinds.Clip"/> so a press on the part cut off
    /// reaches nothing inside. Give it its width; push the row into it.</para>
    /// </summary>
    public Box CollapseBox(string id, float progress, UiNode? parent = null)
    {
        var box = BoxFromId(id, InteractionKinds.Clip, parent);
        float p = Math.Clamp(progress, 0f, 1f);
        box.ChildLayoutAxis = LayoutAxis.Y;
        box.AllowOverflowY = true;
        box.SizeY = p >= 1f ? SemanticSize.ChildrenSum() : SemanticSize.Pixels(box.ContentSize.Y * p);
        return box;
    }

    // ── Theme crossfade ───────────────────────────────────────────────────────

    private ColorTokens? _themeFrom;   // what the fade started from; null when none is running
    private readonly MotionCell _themeFade = new() { Epsilon = 0.001f };

    /// <summary>
    /// Replaces the active theme, crossfading every colour from the old one to
    /// the new one in Oklab by <paramref name="crossfade"/> — an Omarchy theme
    /// switch that dissolves instead of cutting. Everything else in the theme
    /// (sizes, radii, motion) takes effect at once. Swapping again mid-fade
    /// starts from the colours on screen.
    /// </summary>
    public void SetTheme(ThemeTokens theme, Transition crossfade)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _themeFrom = PaintedTheme(_theme).Colors;
        _themeFade.Current = _themeFade.From = Vector4.Zero;
        _themeFade.Target = Vector4.UnitX;
        _themeFade.Velocity = Vector4.Zero;
        _themeFade.Elapsed = 0f;
        _themeFade.Transition = crossfade;
        _theme = theme;
        _lastTree = null;
        RequestFrame();
    }

    /// <summary>
    /// The theme a frame is painted with: <paramref name="theme"/>, or while a
    /// crossfade runs, it with every colour part way from the old theme's.
    /// </summary>
    private ThemeTokens PaintedTheme(ThemeTokens theme)
    {
        if (_themeFrom is null) return theme;
        float t = Math.Clamp(_themeFade.Current.X, 0f, 1f);
        return theme with { Colors = ThemeBlend.Colors(_themeFrom, theme.Colors, t) };
    }

    // ── Stepping ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Steps every value, presence and list row asked for this frame, and the
    /// theme fade, and says whether any is still moving. Runs before the
    /// caches' EndFrame, which is what
    /// <c>Touched</c> is still comparable against.
    /// </summary>
    private bool StepMotion()
    {
        bool instant = Instant;
        float dt = Dt;
        bool animating = false;

        foreach (var (_, entry) in _motion.Entries)
            if (entry.Touched == _motion.Frame)
                animating |= Step(entry.Value, dt, instant);

        foreach (var (_, entry) in _lists.Entries)
        {
            if (entry.Touched != _lists.Frame) continue;
            var cell = entry.Value;
            List<string>? gone = null;
            foreach (var (key, row) in cell.Rows)
            {
                animating |= Step(row.Motion, dt, instant);
                if (!row.Present && row.Motion.Current.X <= 0f) (gone ??= new()).Add(key);
            }
            if (gone is not null)
            {
                foreach (string key in gone) cell.Rows.Remove(key);
                cell.Order.RemoveAll(k => !cell.Rows.ContainsKey(k));
                animating = true;   // one more frame, to build the list without them
            }
        }

        if (_themeFrom is not null)
        {
            bool moving = Step(_themeFade, dt, instant);
            if (!moving) _themeFrom = null;   // arrived: paint the theme itself again
            animating = true;                 // one frame at the end, the theme as it is
        }

        return animating;
    }

    /// <summary>Moves <paramref name="cell"/> one frame along its transition. True while it has not arrived.</summary>
    private static bool Step(MotionCell cell, float dt, bool instant)
    {
        if (cell.Current == cell.Target && cell.Velocity == Vector4.Zero) return false;

        var tr = cell.Transition;
        if (instant || tr.Kind == TransitionKind.Instant)
        {
            Arrive(cell);
            return false;
        }

        cell.Elapsed += dt;
        float moving = cell.Elapsed - tr.Delay;
        if (moving <= 0f) return true;   // still holding for its delay

        switch (tr.Kind)
        {
            case TransitionKind.Timed:
            {
                float p = moving / tr.Duration;
                if (p >= 1f) { Arrive(cell); return false; }
                cell.Current = cell.From + (cell.Target - cell.From) * tr.Easing.Evaluate(p);
                return true;
            }

            case TransitionKind.Spring:
            {
                Ease.Spring(ref cell.Current, ref cell.Velocity, cell.Target, tr.Spring, Math.Min(dt, moving));
                if (MaxAbs(cell.Current - cell.Target) <= cell.Epsilon && MaxAbs(cell.Velocity) <= cell.Epsilon * 10f)
                {
                    Arrive(cell);
                    return false;
                }
                return true;
            }

            default:   // Exponential
            {
                cell.Current = Ease.Exponential(cell.Current, cell.Target, Ease.Rate(tr.Speed, dt));
                if (MaxAbs(cell.Target - cell.Current) <= cell.Epsilon) { Arrive(cell); return false; }
                return true;
            }
        }
    }

    private static void Arrive(MotionCell cell)
    {
        cell.Current = cell.Target;
        cell.From = cell.Target;
        cell.Velocity = Vector4.Zero;
    }

    private static float MaxAbs(Vector4 v)
    {
        var a = Vector4.Abs(v);
        return MathF.Max(MathF.Max(a.X, a.Y), MathF.Max(a.Z, a.W));
    }
}

/// <summary>Colours of one theme part way to another's, role by role and step by step.</summary>
internal static class ThemeBlend
{
    // Every colour role ColorTokens has, read and written by name. The list is
    // built once; a theme fade builds one blended ColorTokens a frame, and only
    // while it runs.
    private static readonly System.Reflection.PropertyInfo[] Roles = typeof(ColorTokens)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string) && p.CanWrite)
        .ToArray();

    internal static ColorTokens Colors(ColorTokens from, ColorTokens to, float t)
    {
        if (t >= 1f) return to;
        var blended = new ColorTokens
        {
            Mode = to.Mode,
            Ramps = new ColorRamps { Neutral = Ramp(from.Ramps.Neutral, to.Ramps.Neutral, t), Accent = Ramp(from.Ramps.Accent, to.Ramps.Accent, t) },
            Extra = Extra(from.Extra, to.Extra, t),
        };
        foreach (var role in Roles)
            role.SetValue(blended, Mix((string)role.GetValue(from)!, (string)role.GetValue(to)!, t));
        return blended;
    }

    private static ColorRamp Ramp(ColorRamp from, ColorRamp to, float t)
    {
        var hex = new string[ColorRamp.Steps.Count];
        for (int i = 0; i < hex.Length; i++) hex[i] = Mix(from[ColorRamp.Steps[i]], to[ColorRamp.Steps[i]], t);
        return new ColorRamp(hex);
    }

    private static Dictionary<string, string> Extra(Dictionary<string, string> from, Dictionary<string, string> to, float t)
    {
        var mixed = new Dictionary<string, string>(to.Count);
        foreach (var (k, v) in to)
            mixed[k] = from.TryGetValue(k, out var old) && IsHex(old) && IsHex(v) ? Mix(old, v, t) : v;
        return mixed;
    }

    private static bool IsHex(string s) => s.Length is 7 or 9 && s[0] == '#';

    private static string Mix(string from, string to, float t)
    {
        var c = ColorMix.Oklab(Rgba.Parse(from), Rgba.Parse(to), t);
        return c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";
    }
}
