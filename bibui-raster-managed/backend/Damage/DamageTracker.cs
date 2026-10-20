using System.Runtime.CompilerServices;
using Bibui.Core;
using Bibui.Core.Draw;

namespace Bibui.Raster.Managed.Backend.Damage;

// Damage tracking — rasterise only what changed — as a policy over the
// mechanism in DamageFrame.
//
// A frame is one DrawList. It is signed before anything is drawn, compared
// with the frame before it, and drawn onto its surface only where the two
// differ. Everything above the rasterizer still builds, lays out and records
// every draw of every frame.
//
// A surface may be any of a ring of buffers (Wayland's wl_shm pool), so it may
// hold a frame several frames old. Each frame's damage is kept for a short
// history and a surface is repainted with the union of the damage since the
// frame it last held — buffer age, counted here rather than asked of the host.
internal sealed class DamageTracker
{
    private const int History = 8;

    // Above this share of the surface, a partial repaint is not worth its
    // bookkeeping and the frame is painted whole.
    private const double WholeThreshold = 0.5;

    // BIBUI_DAMAGE_DEBUG=1 tints what each frame repainted (green) or marks a
    // whole repaint with a red border, for a person to watch the damage live.
    // The tint is itself a change, so the next frame repaints under it.
    private static readonly bool s_debug = Environment.GetEnvironmentVariable("BIBUI_DAMAGE_DEBUG") == "1";

    private static readonly bool s_trace = Environment.GetEnvironmentVariable("BIBUI_DAMAGE_TRACE") == "1";

    private sealed class TargetState
    {
        public long Frame;                      // the frame this surface holds
        public List<PixelRect>? Tinted;         // debug tint drawn over it, null = none
    }

    private readonly ConditionalWeakTable<Canvas, TargetState> _targets = new();
    private readonly List<PixelRect>?[] _history = new List<PixelRect>?[History];
    private DamageFrame? _prev, _spare;
    private long _frame;

    internal IReadOnlyList<PixelRect>? LastRepaint { get; private set; }
    internal string? LastFullRepaintReason { get; private set; }

    /// <summary>
    /// Draws <paramref name="list"/> onto <paramref name="canvas"/>, a surface of
    /// <paramref name="w"/> × <paramref name="h"/> physical px: signed, compared
    /// with the last frame drawn, and repainted only where they differ.
    /// </summary>
    internal void Draw(ManagedRenderer renderer, DrawList list, Canvas canvas, int w, int h)
    {
        long frame = ++_frame;
        var state = _targets.GetOrCreateValue(canvas);
        var cur = _spare ?? new DamageFrame();
        _spare = null;
        cur.Sign(renderer, list, w, h);

        // Why the frame differs from the last one everywhere: then there is
        // nothing to compare.
        string? frameReason = null;
        if (_prev is null) frameReason = "first frame";
        else if (_prev.Width != w || _prev.Height != h) frameReason = "resize";
        else if (_prev.Background.Argb != cur.Background.Argb) frameReason = "background";

        // Why this surface must be painted whole whatever changed.
        string? targetReason = null;
        if (cur.Background.A != 255) targetReason = "translucent background";
        else if (state.Frame <= 0 || frame - state.Frame > History - 1) targetReason = "surface holds an unknown frame";
        else
        {
            for (long f = state.Frame + 1; f < frame; f++)
                if (_history[f % History] is null) { targetReason = "a whole repaint since the surface's frame"; break; }
        }

        // What changed since the last frame; null for everywhere.
        List<PixelRect>? changed = null;
        if (frameReason is null)
        {
            changed = [];
            cur.DamageSince(_prev!, changed);
        }
        _history[frame % History] = changed;

        // What this surface needs repainted: everything that changed since the
        // frame it holds, and any debug tint drawn over it.
        string? reason = frameReason ?? targetReason;
        List<PixelRect>? repaint = null;
        if (reason is null)
        {
            repaint = [];
            for (long f = state.Frame + 1; f <= frame; f++) repaint.AddRange(_history[f % History]!);
            if (state.Tinted is { } t) repaint.AddRange(t);
            if (s_trace) Console.WriteLine($"[damage] changed {Rects(repaint)}");
            cur.Settle(list, repaint);
            if (s_trace) Console.WriteLine($"[damage] settled {Rects(repaint)}");
            if (DamageFrame.AreaOf(repaint) > WholeThreshold * w * (double)h)
            {
                repaint = null;
                reason = "damage over threshold";
            }
        }

        if (repaint is null)
            for (int k = 0; k < list.Ops.Length; k++) renderer.Exec(list, k, canvas);
        else cur.Replay(renderer, list, canvas, repaint);

        state.Tinted = s_debug ? DrawTint(canvas, repaint, w, h) : null;
        state.Frame = frame;

        LastRepaint = repaint;
        LastFullRepaintReason = repaint is null ? reason : null;

        _spare = _prev;
        _prev = cur;
    }

    /// <summary>
    /// Forgets every surface's frame and the last frame: the next frame on any
    /// surface is painted whole. For a frame drawn without tracking.
    /// </summary>
    internal void Forget()
    {
        if (_prev is null) return;
        _spare ??= _prev;
        _prev = null;
        _frame += History + 1;   // every surface's frame is now too old to count
        LastRepaint = null;
        LastFullRepaintReason = null;
    }

    /// <summary>Tints the repainted rects, or borders a whole repaint; returns the pixels it drew over.</summary>
    private static List<PixelRect> DrawTint(Canvas c, List<PixelRect>? repaint, int w, int h)
    {
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
        var paint = new Paint { Color = colour.Argb, Style = PaintStyle.Fill, Aliased = true };
        foreach (var r in drawn)
            c.DrawRoundRect(new Rect(r.Left, r.Top, r.Right, r.Bottom), 0f, 0f, paint);
        return drawn;
    }

    private static string Rects(List<PixelRect> rs) => string.Join(" ", rs.Select(r => $"({r.Left},{r.Top})-({r.Right},{r.Bottom})"));
}
