using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed.Backend.Damage;

// Damage tracking — rasterise only what changed — as a policy over the
// mechanism in DamageFrame and DamageRecorder.
//
// A frame is what a canvas is told between Clear (the painter starts every
// frame with one) and the backend's EndFrame (the painter ends every frame with
// one). It is recorded instead of drawn, compared with the frame before it,
// and replayed onto its surface only where the two differ. Everything above the
// rasterizer still builds, lays out and issues every draw of every frame.
//
// A surface may be any of a ring of buffers (Wayland's wl_shm pool), so it may
// hold a frame several frames old. Each frame's damage is kept for a short
// history and a surface is repainted with the union of the damage since the
// frame it last held — buffer age, counted here rather than asked of the host.
internal sealed class DamageTracker(ManagedRasterBackend backend)
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

    private readonly DamageRecorder _recorder = new(backend);
    private readonly ConditionalWeakTable<ManagedRasterCanvas, TargetState> _targets = new();
    private readonly List<PixelRect>?[] _history = new List<PixelRect>?[History];
    private DamageFrame? _prev, _spare;
    private long _frame;

    // The frame being recorded.
    private ManagedRasterCanvas? _active;
    private DamageFrame? _cur;
    private TargetState? _state;
    private string? _frameReason, _targetReason;

    internal IReadOnlyList<PixelRect>? LastRepaint { get; private set; }
    internal string? LastFullRepaintReason { get; private set; }

    /// <summary>
    /// <paramref name="canvas"/> starts a frame (its Clear). Returns the recorder
    /// it is to send the frame to; a teed recorder wants every call drawn too.
    /// </summary>
    internal DamageRecorder Begin(ManagedRasterCanvas canvas, Rgba background)
    {
        // A frame that never reached EndFrame (it threw) leaves the canvas
        // holding who knows what: no target is trusted after it.
        if (_active is not null) Abandon();

        long frame = ++_frame;
        var state = _targets.GetOrCreateValue(canvas);
        int w = canvas.Width, h = canvas.Height;

        // Why the frame differs from the last one everywhere, as far as is
        // known before recording it: then there is nothing to compare.
        _frameReason = null;
        if (_prev is null) _frameReason = "first frame";
        else if (_prev.Width != w || _prev.Height != h) _frameReason = "resize";
        else if (_prev.Background.Argb != background.Argb) _frameReason = "background";

        // Why this surface must be painted whole whatever changed.
        _targetReason = null;
        if (background.A != 255) _targetReason = "translucent background";
        else if (state.Frame <= 0 || frame - state.Frame > History - 1) _targetReason = "surface holds an unknown frame";
        else
        {
            for (long f = state.Frame + 1; f < frame; f++)
                if (_history[f % History] is null) { _targetReason = "a whole repaint since the surface's frame"; break; }
        }

        _cur = _spare ?? new DamageFrame();
        _spare = null;
        _active = canvas;
        _state = state;
        // Known to be whole: drawn as it is recorded, rather than recorded and replayed all of it.
        _recorder.Begin(_cur, w, h, tee: (_frameReason ?? _targetReason) is not null);
        return _recorder;
    }

    /// <summary>The painter's EndFrame: compares the frame with the last and repaints its surface where they differ.</summary>
    internal void End()
    {
        if (_active is not { } canvas) return;
        var cur = _cur!;
        var state = _state!;
        long frame = _frame;
        _recorder.End();
        canvas.StopRecording();

        // What changed since the last recorded frame; null for everywhere.
        List<PixelRect>? changed = null;
        if (_frameReason is null)
        {
            changed = [];
            cur.DamageSince(_prev!, changed);
        }
        _history[frame % History] = changed;

        // What this surface needs repainted: everything that changed since the
        // frame it holds, and any debug tint drawn over it.
        string? reason = _frameReason ?? _targetReason;
        List<PixelRect>? repaint = null;
        if (reason is null)
        {
            repaint = [];
            for (long f = state.Frame + 1; f <= frame; f++) repaint.AddRange(_history[f % History]!);
            if (state.Tinted is { } t) repaint.AddRange(t);
            if (s_trace) Console.WriteLine($"[damage] changed {Rects(repaint)}");
            cur.Settle(repaint);
            if (s_trace) Console.WriteLine($"[damage] settled {Rects(repaint)}");
            if (DamageFrame.AreaOf(repaint) > WholeThreshold * cur.Width * (double)cur.Height)
            {
                repaint = null;
                reason = "damage over threshold";
            }
            cur.Replay(canvas, repaint);
        }
        _recorder.Release();

        state.Tinted = s_debug ? DrawTint(canvas, repaint, cur.Width, cur.Height) : null;
        state.Frame = frame;

        LastRepaint = repaint;
        LastFullRepaintReason = repaint is null ? reason : null;

        _spare = _prev;
        _prev = cur;
        _active = null;
        _cur = null;
        _state = null;
    }

    /// <summary>
    /// Forgets every surface's frame and the last record: the next frame on
    /// any surface is painted whole. For a frame drawn without tracking, and
    /// for one that never ended.
    /// </summary>
    internal void Forget()
    {
        if (_active is not null) Abandon();
        if (_prev is null) return;
        _spare ??= _prev;
        _prev = null;
        _frame += History + 1;   // every surface's frame is now too old to count
        LastRepaint = null;
        LastFullRepaintReason = null;
    }

    private void Abandon()
    {
        _active!.StopRecording();
        _recorder.Release();
        _active = null;
        _spare ??= _cur;
        _cur = null;
        _state = null;
        _prev = null;
        _frame += History + 1;
    }

    /// <summary>Tints the repainted rects, or borders a whole repaint; returns the pixels it drew over.</summary>
    private static List<PixelRect> DrawTint(ManagedRasterCanvas c, List<PixelRect>? repaint, int w, int h)
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
        var paint = new RasterPaint(new PaintSpec { Color = colour, Style = Bibui.PaintStyle.Fill, IsAntialias = false });
        foreach (var r in drawn)
            c.DrawRoundRect(new RectF(r.Left, r.Top, r.Right, r.Bottom), 0f, 0f, paint);
        return drawn;
    }

    private static string Rects(List<PixelRect> rs) => string.Join(" ", rs.Select(r => $"({r.Left},{r.Top})-({r.Right},{r.Bottom})"));
}
