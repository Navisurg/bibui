using System.Runtime.InteropServices;
using Bibui.Core.Imaging;

namespace Bibui.Imaging;

/// <summary>A WebP that cannot be decoded: not a WebP, truncated, corrupt, or animated.</summary>
public sealed class WebPException(string message) : FormatException(message);

/// <summary>
/// WebP decode, through the system's libwebp (<c>libwebp.so.7</c>). The library is
/// loaded by hand on the first decode rather than imported, so a machine without
/// it, or a platform that is not Linux, has no WebP decoder (<see cref="IsAvailable"/>
/// is false) instead of failing to start.
///
/// <para>
/// Lossy, lossless, and either with an alpha channel all decode, to straight
/// 8-bit RGBA (an image without alpha is opaque). Lossless is exact. An animated
/// WebP is refused rather than shown as its first frame. Colour management is not
/// applied: an ICC profile is ignored, as <see cref="Png"/> ignores one.
/// </para>
/// </summary>
public static unsafe class WebP
{
    private const string Library = "libwebp.so.7";

    /// <summary>True if the bytes begin with a WebP's RIFF header.</summary>
    public static bool IsWebP(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>
    /// True if the system has libwebp, so <see cref="Decode"/> can work. Asked the
    /// first time it is read, then kept for the process.
    /// </summary>
    public static bool IsAvailable => Native.Load();

    public static RgbaImage DecodeFile(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>
    /// Decodes a WebP to straight 8-bit RGBA. Throws <see cref="WebPException"/> if it
    /// cannot, and <see cref="DllNotFoundException"/> if libwebp is not installed
    /// (<see cref="IsAvailable"/>).
    /// </summary>
    public static RgbaImage Decode(ReadOnlySpan<byte> webp)
    {
        if (!IsWebP(webp)) throw new WebPException("Not a WebP: the RIFF/WEBP header is missing.");
        // VP8X, the extended header, says in its flags whether the file is an animation.
        if (webp.Length >= 21 && webp.Slice(12, 4).SequenceEqual("VP8X"u8) && (webp[20] & 0x02) != 0)
            throw new WebPException("An animated WebP: only still images are decoded.");
        if (!Native.Load()) throw new DllNotFoundException($"WebP needs {Library} (libwebp), which is not installed.");

        fixed (byte* src = webp)
        {
            int width, height;
            if (Native.GetInfo(src, (nuint)webp.Length, &width, &height) == 0)
                throw new WebPException("Unreadable header: not a WebP libwebp can read.");
            if (width <= 0 || height <= 0) throw new WebPException($"A WebP of {width}x{height} has no pixels.");
            if ((long)width * height * RgbaImage.BytesPerPixel > Array.MaxLength)
                throw new WebPException($"{width}x{height} is larger than an image can hold.");

            var image = new RgbaImage(width, height);
            fixed (byte* dst = image.Pixels)
            {
                if (Native.DecodeRgbaInto(src, (nuint)webp.Length, dst, (nuint)image.Pixels.Length, width * 4) == null)
                    throw new WebPException("Corrupt: libwebp could not decode the image data.");
            }
            return image;
        }
    }

    // ── libwebp ──────────────────────────────────────────────────────────────

    private static class Native
    {
        private static int s_state; // 0 not tried, 1 loaded, -1 not there
        private static delegate* unmanaged<byte*, nuint, int*, int*, int> s_getInfo;
        private static delegate* unmanaged<byte*, nuint, byte*, nuint, int, byte*> s_decodeRgbaInto;

        // Two threads that both find it untried both load it, and find the same functions.
        public static bool Load()
        {
            int state = Volatile.Read(ref s_state);
            if (state != 0) return state > 0;
            bool ok = false;
            if (NativeLibrary.TryLoad(Library, out IntPtr lib)
                && NativeLibrary.TryGetExport(lib, "WebPGetInfo", out IntPtr getInfo)
                && NativeLibrary.TryGetExport(lib, "WebPDecodeRGBAInto", out IntPtr decode))
            {
                s_getInfo = (delegate* unmanaged<byte*, nuint, int*, int*, int>)getInfo;
                s_decodeRgbaInto = (delegate* unmanaged<byte*, nuint, byte*, nuint, int, byte*>)decode;
                ok = true;
            }
            Volatile.Write(ref s_state, ok ? 1 : -1);
            return ok;
        }

        public static int GetInfo(byte* data, nuint size, int* width, int* height) => s_getInfo(data, size, width, height);
        public static byte* DecodeRgbaInto(byte* data, nuint size, byte* output, nuint outputSize, int stride)
            => s_decodeRgbaInto(data, size, output, outputSize, stride);
    }
}
