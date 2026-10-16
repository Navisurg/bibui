using System.Diagnostics;

namespace Bibui.Runtime;

/// <summary>
/// Per-frame phase samples for a live <see cref="BibuiWindow"/>, collected only
/// under <c>BIBUI_PROFILE=1</c> and printed as one percentile table when the
/// loop exits. Exists so a frame can be split into what the runtime does
/// (build + layout + raster or GL command recording), what hands the frame to
/// the GPU (flush, or texture upload + quad on the CPU path), and what the
/// swap to the compositor costs — the part only a real window can measure.
///
/// <c>BIBUI_PROFILE_FRAMES=N</c> turns it on without the rest of BIBUI_PROFILE
/// and closes the window after N steady-state frames, so a run is
/// repeatable without a person timing it. Combine with
/// <c>BIBUI_ALWAYS_RENDER=1</c>, or an idle window draws no frames at all.
/// </summary>
internal sealed class WindowFrameProfile
{
    internal static readonly int CloseAfterFrames =
        int.TryParse(Environment.GetEnvironmentVariable("BIBUI_PROFILE_FRAMES"), out var n) ? n : 0;

    private readonly List<double> _render = new();
    private readonly List<double> _submit = new();
    private readonly List<double> _swap = new();
    private readonly List<double> _frame = new();
    private readonly List<double> _interval = new();

    private readonly long _runStart = Stopwatch.GetTimestamp();
    private long _lastFrameEnd;
    private double _firstFrameMs = -1;
    private double _firstFrameSinceProcessMs = -1;

    // Written by the render handler, read when DoRender returns.
    internal double RenderMs;
    internal double SubmitMs;

    internal int Frames => _frame.Count;

    /// <summary>One frame finished: <paramref name="doRenderTicks"/> is the whole
    /// DoRender call, which is the render handler followed by SwapBuffers.</summary>
    internal void EndFrame(long doRenderTicks)
    {
        long now = Stopwatch.GetTimestamp();
        double total = Ms(doRenderTicks);
        if (_firstFrameMs < 0)
        {
            // The first frame pays one-off costs (input wiring, first swap); it
            // is reported as cold start and kept out of the steady-state table.
            _firstFrameMs = Ms(now - _runStart);
            _firstFrameSinceProcessMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            _lastFrameEnd = now;
            return;
        }
        _render.Add(RenderMs);
        _submit.Add(SubmitMs);
        _swap.Add(Math.Max(0, total - RenderMs - SubmitMs));
        _frame.Add(total);
        _interval.Add(Ms(now - _lastFrameEnd));
        _lastFrameEnd = now;
    }

    internal void Print(bool gpu, int fbWidth, int fbHeight)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  BibuiWindow frame profile  ({(gpu ? "GPU" : "CPU fallback")} path, {fbWidth}×{fbHeight} px, {_frame.Count} frames)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  first frame presented: {_firstFrameMs:F1} ms after Run(), {_firstFrameSinceProcessMs:F0} ms after process start");
        if (_frame.Count == 0)
        {
            Console.WriteLine("  (no steady-state frames — set BIBUI_ALWAYS_RENDER=1)");
            return;
        }
        Console.WriteLine($"{"Phase",-44} {"min",7} {"p50",7} {"p99",7} {"max",7}");
        Console.WriteLine(new string('─', 79));
        Row(gpu ? "render  (build+layout+record+GL record)" : "render  (build+layout+record+raster)", _render);
        Row(gpu ? "submit  (Skia flush → GL)" : "submit  (texture upload + quad)", _submit);
        Row("present (SwapBuffers)", _swap);
        Row("frame   (DoRender total)", _frame);
        Row("interval between frames (paced)", _interval);
        Console.WriteLine(new string('─', 79));
        Console.WriteLine($"  RSS {Environment.WorkingSet / 1048576.0:F1} MB, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F1} MB, managed heap {GC.GetTotalMemory(false) / 1048576.0:F1} MB");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════");

        static void Row(string label, List<double> xs)
        {
            var s = xs.Order().ToArray();
            Console.WriteLine($"{label,-44} {s[0],7:F2} {P(s, 0.50),7:F2} {P(s, 0.99),7:F2} {s[^1],7:F2}");
        }
    }

    internal static double P(double[] sorted, double q) =>
        sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

    internal static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
