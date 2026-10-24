using Bibui.Platform.Browser;
using Bibui.Core.Runtime.Platform;

namespace Bibui.BrowserTests;

/// <summary>The frame's size at the canvas's device pixels, and the refresh rate measured from requestAnimationFrame.</summary>
internal static class SizeTests
{
    public static void Register(Harness t)
    {
        t.Run("size: the frame is the canvas's device size, at fractional ratios too", () =>
        {
            var size = new CanvasSize();
            Assert.True(size.Update(792, 401, 1.25f), "the first size is a change");
            // 792 / 1.25 is 633.6: 634 logical, which scaled back would be 793.
            Assert.Eq(792, size.Width, "frame width");
            Assert.Eq(401, size.Height, "frame height");
            var frame = new WindowFrame(size.Width, size.Height, size.Ratio, 60f);
            Assert.Eq(634, frame.LogicalWidth, "logical width");
            Assert.Eq(321, frame.LogicalHeight, "logical height");
            Assert.Eq(1.25f, frame.Scale, "scale");
        });

        t.Run("size: the same size again is no change; a new ratio is", () =>
        {
            var size = new CanvasSize();
            size.Update(1280, 720, 2f);
            Assert.True(!size.Update(1280, 720, 2f), "unchanged");
            Assert.True(size.Update(1280, 720, 1.5f), "a new ratio at the same device size");
            Assert.Eq(853, new WindowFrame(size.Width, size.Height, size.Ratio, 60f).LogicalWidth, "logical width at 1.5");
            Assert.True(size.Update(1281, 720, 1.5f), "a pixel wider");
        });

        t.Run("size: a zero or bad report is a 1 px canvas at ratio 1", () =>
        {
            var size = new CanvasSize();
            size.Update(0, -3, float.NaN);
            Assert.Eq(1, size.Width);
            Assert.Eq(1, size.Height);
            Assert.Eq(1f, size.Ratio);
        });

        t.Run("refresh: 60 until measured, then the shortest gap between consecutive callbacks", () =>
        {
            var clock = new RefreshClock();
            Assert.Near(60f, clock.Hz, 1e-6f, "before any callback");
            double now = 1000;
            for (int i = 0; i <= 30; i++)
            {
                clock.Tick(now);
                // A dropped frame now and then: twice the gap, which is not the refresh.
                now += i % 7 == 3 ? 2 * 1000.0 / 120 : 1000.0 / 120;
            }
            Assert.Near(120f, clock.Hz, 0.01f, "a 120 Hz display");
            clock.Tick(now + 5000);
            Assert.Near(120f, clock.Hz, 0.01f, "the loop starting again after a stop measures nothing");
        });
    }
}
