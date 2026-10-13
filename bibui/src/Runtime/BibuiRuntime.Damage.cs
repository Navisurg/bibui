using System.Runtime.CompilerServices;
using Bibui.Theming;
using Bibui.Raster;

namespace Bibui.Runtime;

// Damage tracking — redraw and present only what changed.
//
// The mechanism is the renderer's (BibuiRenderer.Record, FrameRecord): a frame
// is recorded instead of drawn, every box signed by the draws it makes, two
// records diffed into the rects where their pixels can differ, and the frame
// replayed onto a surface that still holds an older one, through those rects
// only. The policy is here: when a partial repaint can be trusted, what the
// target surface holds, and when to give up and paint the whole frame.
//
// The target may be any of a ring of buffers (Wayland's wl_shm pool), so it
// may hold a frame several frames old. Each frame's damage is kept for a short
// history and a target is repainted with the union of the damage since the
// frame it last showed — buffer age, counted by the runtime rather than asked
// of the host.
public sealed partial class BibuiRuntime
{
    /// <summary>
    /// Damage tracking. When set, <see cref="RenderTo(IRasterSurface)"/> records each
    /// frame, works out which pixels differ from the frame the target surface
    /// already holds, and repaints only those (<see cref="LastRepaint"/>);
    /// <see cref="LastDamage"/> says what changed for the host to present.
    /// Pixels are byte-identical to a full repaint. Off by default; the
    /// environment variable <c>BIBUI_DAMAGE=1</c> turns it on.
    /// </summary>
    public bool DamageTracking { get; set; } = Environment.GetEnvironmentVariable("BIBUI_DAMAGE") == "1";

    /// <summary>
    /// What changed in the last frame drawn by <see cref="RenderTo(IRasterSurface)"/>,
    /// relative to the frame drawn before it, in physical px: the rects a host
    /// tells its compositor about (<c>wl_surface.damage_buffer</c>). Null means
    /// the whole surface; empty means nothing changed.
    /// </summary>
    public IReadOnlyList<PixelRect>? LastDamage { get; private set; }

    /// <summary>
    /// The rects the last <see cref="RenderTo(IRasterSurface)"/> repainted on its
    /// target, in physical px. Null means the whole surface was painted.
    /// </summary>
    public IReadOnlyList<PixelRect>? LastRepaint { get; private set; }

    /// <summary>Why the last frame was painted whole, or null when it was not.</summary>
    public string? LastFullRepaintReason { get; private set; }

    /// <summary>
    /// Above this share of the surface, a partial repaint is not worth its
    /// bookkeeping and the frame is painted whole.
    /// </summary>
    public float DamageFullRepaintThreshold { get; set; } = 0.5f;

    // BIBUI_DAMAGE_DEBUG=1 tints what each frame repainted (green) or marks a
    // whole repaint with a red border, for a person to watch the damage live.
    // The tint is itself a change, so the next frame repaints under it.
    private static readonly bool s_damageDebug = Environment.GetEnvironmentVariable("BIBUI_DAMAGE_DEBUG") == "1";

    private static readonly bool s_trace = Environment.GetEnvironmentVariable("BIBUI_DAMAGE_TRACE") == "1";

    private const int DamageHistory = 8;

    private sealed class TargetState
    {
        public long Frame;                      // the frame this target holds
        public List<PixelRect>? Tinted;         // debug tint drawn over it, null = none
    }

    private FrameRecord? _dmgPrev, _dmgSpare;
    private readonly ConditionalWeakTable<IRasterSurface, TargetState> _dmgTargets = new();
    private readonly List<PixelRect>?[] _dmgHistory = new List<PixelRect>?[DamageHistory];
    private long _dmgFrame;
    private SurfaceOptions? _dmgSurface;
    private ThemeTokens? _dmgTheme;
    private List<PixelRect>? _dmgLastTint;

    /// <summary>Forgets what every target holds: the next damage-tracked frame is painted whole.</summary>
    private void ForgetDamageState()
    {
        _dmgPrev = null;
        _dmgFrame += DamageHistory + 1;   // every target's frame is now too old to count
        LastDamage = null;
        LastRepaint = null;
    }

    private void RenderDamaged(IRasterSurface target, bool retained, UiNode tree,
        SurfaceOptions surface, ThemeTokens theme, FrameInput input)
    {
        var rec = _dmgSpare ?? new FrameRecord();
        _dmgSpare = null;
        long frame = ++_dmgFrame;
        var state = _dmgTargets.GetOrCreateValue(target);

        // Why the frame differs from the last one everywhere, as far as is known
        // before painting it: then there is nothing to diff.
        string? frameReason = null;
        if (_dmgPrev is null) frameReason = "first frame";
        else if (_dmgSurface is not { } s || s.Width != surface.Width || s.Height != surface.Height || s.ScaleFactor != surface.ScaleFactor)
            frameReason = "resize or scale";
        else if (!ReferenceEquals(_dmgTheme, theme)) frameReason = "theme";

        // Why this target must be painted whole whatever changed, as far as is
        // known before painting.
        int physW = SurfaceOptions.PhysicalSize(surface.Width, surface.ScaleFactor);
        int physH = SurfaceOptions.PhysicalSize(surface.Height, surface.ScaleFactor);
        string? targetReason = null;
        if (!retained) targetReason = "target keeps no pixels";
        else if (Rgba.Parse(theme.Colors.Background).A != 255) targetReason = "translucent background";
        else if (state.Frame <= 0 || frame - state.Frame > DamageHistory - 1 || target.Width != physW || target.Height != physH)
            targetReason = "target holds an unknown frame";
        else
        {
            for (long f = state.Frame + 1; f < frame; f++)
                if (_dmgHistory[f % DamageHistory] is null) { targetReason = "a full repaint since the target's frame"; break; }
        }

        // Known to be whole: paint it as it is recorded (the recorder passes every
        // call straight on to the target), rather than record it and replay all of it.
        string? whole = frameReason ?? targetReason;
        long p0 = _profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _renderer.Record(rec, tree, surface, theme, input, whole is null ? null : target.Canvas);
        long p1 = _profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        // What changed since the last recorded frame; null for everywhere.
        List<PixelRect>? changed = null;
        if (frameReason is null)
        {
            changed = [];
            if (!rec.DamageSince(_dmgPrev!, changed)) { changed = null; frameReason = "unsigned content"; }
        }
        _dmgHistory[frame % DamageHistory] = changed;

        // What this target needs repainted: everything that changed since the
        // frame it holds, and any debug tint drawn over it.
        List<PixelRect>? repaint = null;
        string? reason = whole ?? frameReason;
        if (reason is null)
        {
            repaint = [];
            for (long f = state.Frame + 1; f <= frame; f++) repaint.AddRange(_dmgHistory[f % DamageHistory]!);
            if (state.Tinted is { } t) repaint.AddRange(t);
            if (s_trace) Console.WriteLine($"[damage] changed {Rects(repaint)}");
            // Settling only grows the rects: damage already over the line is whole
            // without paying for it (a scroll: every box in the view moved).
            if (FrameRecord.AreaOf(repaint) <= DamageFullRepaintThreshold * rec.Width * (double)rec.Height || repaint.Count <= 8)
                rec.Settle(repaint);
            if (s_trace) Console.WriteLine($"[damage] settled {Rects(repaint)}");
            if (FrameRecord.AreaOf(repaint) > DamageFullRepaintThreshold * rec.Width * (double)rec.Height)
            {
                repaint = null;
                reason = "damage over threshold";
            }
        }

        long p2 = _profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (whole is null) rec.Replay(target, repaint);
        if (_profile)
        {
            long p3 = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            Console.WriteLine($"[Runtime.Damage] record={(p1 - p0) / ms:F2}ms  diff+settle={(p2 - p1) / ms:F2}ms  " +
                              $"replay={(p3 - p2) / ms:F2}ms  ops={rec.OpCountTotal}  " +
                              (repaint is null ? $"whole: {reason}" : $"{repaint.Count} rects"));
        }

        List<PixelRect>? tint = null;
        if (s_damageDebug) tint = DrawDamageTint(target, repaint, rec.Width, rec.Height);
        state.Frame = frame;
        state.Tinted = tint;

        // The compositor is told what differs from the frame shown before this
        // one: what changed, and the tint that went or came.
        List<PixelRect>? damage = changed is null ? null : [.. changed];
        if (damage is not null && s_damageDebug)
        {
            if (_dmgLastTint is null || tint is null) damage = null;
            else { damage.AddRange(_dmgLastTint); damage.AddRange(tint); }
        }
        _dmgLastTint = tint;

        LastDamage = damage;
        LastRepaint = repaint;
        LastFullRepaintReason = repaint is null ? reason : null;

        _dmgSpare = _dmgPrev;
        _dmgPrev = rec;
        _dmgSurface = surface;
        _dmgTheme = theme;

        static string Rects(List<PixelRect> rs) => string.Join(" ", rs.Select(r => $"({r.Left},{r.Top})-({r.Right},{r.Bottom})"));
    }

    /// <summary>Tints the repainted rects, or borders a whole repaint; returns the pixels it drew over.</summary>
    private static List<PixelRect> DrawDamageTint(IRasterSurface target, List<PixelRect>? repaint, int w, int h)
    {
        var c = target.Canvas;
        List<PixelRect> drawn;
        Rgba colour;
        if (repaint is null)
        {
            const int b = 4;
            drawn = [new(0, 0, w, b), new(0, h - b, w, h), new(0, b, b, h - b), new(w - b, b, w, h - b)];
            colour = new Rgba(255, 40, 40, 160);
        }
        else
        {
            drawn = [.. repaint];
            colour = new Rgba(40, 255, 90, 70);
        }
        var paint = new RasterPaint(new PaintSpec { Color = colour, Style = PaintStyle.Fill, IsAntialias = false });
        foreach (var r in drawn)
            c.DrawRoundRect(new RectF(r.Left, r.Top, r.Right, r.Bottom), 0f, 0f, paint);
        return drawn;
    }
}
