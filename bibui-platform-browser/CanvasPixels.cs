using System.Runtime.InteropServices;

namespace Bibui.Platform.Browser;

/// <summary>
/// A frame's pixels as a canvas takes them. Renderers draw BGRA with
/// premultiplied alpha (<see cref="Bibui.Core.Draw.RasterPixels"/>);
/// <c>ImageData</c> is RGBA with straight alpha.
/// </summary>
internal static class CanvasPixels
{
    /// <summary>
    /// <paramref name="bgra"/> (BGRA8888, premultiplied) as RGBA8888 with
    /// straight alpha, into <paramref name="rgba"/>, which is at least as long.
    /// An opaque pixel only swaps red and blue; a translucent one is divided by
    /// its alpha, rounded to nearest.
    /// </summary>
    public static void BgraPremultipliedToRgba(ReadOnlySpan<byte> bgra, Span<byte> rgba)
    {
        if (rgba.Length < bgra.Length) throw new ArgumentException("The RGBA buffer is shorter than the BGRA one.", nameof(rgba));
        var src = MemoryMarshal.Cast<byte, uint>(bgra);
        var dst = MemoryMarshal.Cast<byte, uint>(rgba);
        for (int i = 0; i < src.Length; i++)
        {
            // Little-endian: a BGRA pixel reads as 0xAARRGGBB, an RGBA one as 0xAABBGGRR.
            uint p = src[i];
            uint a = p >> 24;
            if (a == 255) { dst[i] = (p & 0xFF00FF00) | ((p >> 16) & 0xFF) | ((p & 0xFF) << 16); continue; }
            if (a == 0) { dst[i] = 0; continue; }
            uint half = a / 2;
            uint r = Math.Min(255u, (((p >> 16) & 0xFF) * 255 + half) / a);
            uint g = Math.Min(255u, (((p >> 8) & 0xFF) * 255 + half) / a);
            uint b = Math.Min(255u, ((p & 0xFF) * 255 + half) / a);
            dst[i] = (a << 24) | (b << 16) | (g << 8) | r;
        }
    }
}
