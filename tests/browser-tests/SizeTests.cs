using Bibui.Platform.Browser;
using Bibui.Core.Runtime;

namespace Bibui.BrowserTests;

/// <summary>The frame's size at the canvas's device pixels, and the frame clock.</summary>
internal static class SizeTests
{
    public static void Register(Harness t)
    {
        t.Run("size: the frame is the canvas's device size, at fractional ratios too", () =>
        {
            var runtime = BibuiRuntime.Create();
            var size = new CanvasSize();
            Assert.True(size.Update(runtime, 792, 401, 1.25f), "the first size is a change");
            // 792 / 1.25 is 633.6: 634 logical, which scaled back would be 793.
            Assert.Eq(792, size.Width, "frame width");
            Assert.Eq(401, size.Height, "frame height");
            Assert.Eq(634, runtime.SurfaceWidth, "logical width");
            Assert.Eq(321, runtime.SurfaceHeight, "logical height");
            Assert.Eq(1.25f, runtime.ScaleFactor, "scale");
        });

        t.Run("size: the same size again is no change; a new ratio is", () =>
        {
            var runtime = BibuiRuntime.Create();
            var size = new CanvasSize();
            size.Update(runtime, 1280, 720, 2f);
            Assert.True(!size.Update(runtime, 1280, 720, 2f), "unchanged");
            Assert.True(size.Update(runtime, 1280, 720, 1.5f), "a new ratio at the same device size");
            Assert.Eq(853, runtime.SurfaceWidth, "logical width at 1.5");
            Assert.True(size.Update(runtime, 1281, 720, 1.5f), "a pixel wider");
        });

        t.Run("size: a zero or bad report is a 1 px canvas at ratio 1", () =>
        {
            var runtime = BibuiRuntime.Create();
            var size = new CanvasSize();
            size.Update(runtime, 0, -3, float.NaN);
            Assert.Eq(1, size.Width);
            Assert.Eq(1, size.Height);
            Assert.Eq(1f, size.Ratio);
        });

        t.Run("clock: Dt is the time since the last frame, one frame after a wait", () =>
        {
            var clock = new FrameClock();
            Assert.Near(1f / 60f, clock.Step(1000, waited: false, 60f), 1e-6f, "the first frame");
            Assert.Near(0.0167f, clock.Step(1016.7, waited: false, 60f), 1e-4f, "a frame later");
            Assert.Near(1f / 120f, clock.Step(5000, waited: true, 120f), 1e-6f, "after a wait for input");
            Assert.Near(FrameClock.MaxDt, clock.Step(9000, waited: false, 60f), 1e-6f, "after the tab was hidden");
            Assert.Near(1f / 60f, clock.Step(8000, waited: false, 60f), 1e-6f, "a timestamp that went back");
        });
    }
}
