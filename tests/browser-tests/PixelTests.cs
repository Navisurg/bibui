using System.Runtime.InteropServices;
using Bibui.Platform.Browser;

namespace Bibui.BrowserTests;

/// <summary>A frame as the canvas takes it: BGRA premultiplied in, RGBA straight out.</summary>
internal static class PixelTests
{
    public static void Register(Harness t)
    {
        t.Run("pixels: an opaque pixel swaps red and blue", () =>
        {
            Assert.Eq(Rgba(0x10, 0x20, 0x30, 0xFF), Convert(Bgra(0x30, 0x20, 0x10, 0xFF)));
        });

        t.Run("pixels: a transparent pixel is transparent black", () =>
        {
            Assert.Eq(0u, Convert(Bgra(0, 0, 0, 0)));
        });

        t.Run("pixels: a translucent pixel is divided by its alpha, rounded", () =>
        {
            // 64 of 128 is half: 127.5, rounded to 128.
            Assert.Eq(Rgba(128, 0, 255, 128), Convert(Bgra(128, 0, 64, 128)));
            // Every straight value survives premultiplying at every alpha within one step.
            for (int a = 1; a < 255; a += 7)
            for (int c = 0; c <= 255; c += 5)
            {
                byte premul = (byte)((c * a + 127) / 255);
                uint back = Convert(Bgra(premul, premul, premul, (byte)a));
                int r = (int)(back & 0xFF);
                Assert.True(Math.Abs(r - c) <= (255 + a - 1) / a, $"c {c} at alpha {a} came back as {r}");
            }
        });

        t.Run("pixels: a premultiplied value above its alpha clamps at 255", () =>
        {
            Assert.Eq(Rgba(255, 255, 255, 10), Convert(Bgra(200, 200, 200, 10)));
        });

        t.Run("pixels: a frame converts whole, and a short destination is refused", () =>
        {
            var bgra = new byte[4 * 3] { 1, 2, 3, 255, 0, 0, 0, 0, 10, 20, 30, 255 };
            var rgba = new byte[bgra.Length];
            CanvasPixels.BgraPremultipliedToRgba(bgra, rgba);
            Assert.Eq("3,2,1,255,0,0,0,0,30,20,10,255", string.Join(",", rgba));
            bool threw = false;
            try { CanvasPixels.BgraPremultipliedToRgba(bgra, new byte[4]); } catch (ArgumentException) { threw = true; }
            Assert.True(threw, "a destination shorter than the frame throws");
        });
    }

    private static uint Bgra(byte b, byte g, byte r, byte a) => (uint)(b | g << 8 | r << 16 | a << 24);
    private static uint Rgba(byte r, byte g, byte b, byte a) => (uint)(r | g << 8 | b << 16 | a << 24);

    private static uint Convert(uint bgra)
    {
        var src = MemoryMarshal.AsBytes(new[] { bgra }.AsSpan()).ToArray();
        var dst = new byte[4];
        CanvasPixels.BgraPremultipliedToRgba(src, dst);
        return MemoryMarshal.Read<uint>(dst);
    }
}
