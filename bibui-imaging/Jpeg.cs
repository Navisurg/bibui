using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Bibui.Core.Imaging;

namespace Bibui.Imaging;

/// <summary>A JPEG that cannot be decoded: not a JPEG, truncated before its first scan, corrupt, or a form libjpeg-turbo refuses.</summary>
public sealed class JpegException(string message) : FormatException(message);

/// <summary>
/// JPEG decode, through the system's libjpeg-turbo (<c>libturbojpeg.so.0</c>, its
/// TurboJPEG 2 API, which every release since 2.0 keeps). The library is loaded by
/// hand on the first decode rather than imported, so a machine without it, or a
/// platform that is not Linux, has no JPEG decoder (<see cref="IsAvailable"/> is
/// false) instead of failing to start.
///
/// <para>
/// The result is opaque 8-bit RGBA. Baseline and progressive, every chroma
/// subsampling, grey, and CMYK/YCCK (as Photoshop writes it, inverted, converted
/// with Skia's naive formula) all decode. The DCT is libjpeg's accurate integer
/// one with smooth upsampling, as the Skia backend decodes, so the two agree to
/// within a level or two. Colour management is not applied: an ICC profile is
/// ignored, as <see cref="Png"/> ignores one.
/// </para>
///
/// <para>
/// The EXIF orientation is honoured: a phone photo taken in portrait, stored
/// sideways with an orientation tag, comes back upright, at its upright width and
/// height. A file that is cut short after its first scan has started decodes as
/// far as it goes, the rest grey, as libjpeg's own tools and browsers show it.
/// </para>
/// </summary>
public static unsafe class Jpeg
{
    private const string Library = "libturbojpeg.so.0";

    // TurboJPEG's enums and flags, from turbojpeg.h.
    private const int PixelRgba = 7;          // TJPF_RGBA
    private const int PixelCmyk = 11;         // TJPF_CMYK
    private const int ColorspaceCmyk = 3;     // TJCS_CMYK
    private const int ColorspaceYcck = 4;     // TJCS_YCCK
    private const int FlagLimitScans = 32768; // TJFLAG_LIMITSCANS: refuse a progressive file of more than 500 scans
    private const int ErrorWarning = 0;       // TJERR_WARNING

    /// <summary>True if the bytes begin with a JPEG's start-of-image marker.</summary>
    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    /// <summary>
    /// True if the system has libjpeg-turbo, so <see cref="Decode"/> can work. Asked
    /// the first time it is read, then kept for the process.
    /// </summary>
    public static bool IsAvailable => Native.Load();

    public static RgbaImage DecodeFile(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>
    /// Decodes a JPEG to opaque 8-bit RGBA, turned upright by its EXIF orientation.
    /// Throws <see cref="JpegException"/> if it cannot, and <see cref="DllNotFoundException"/>
    /// if libjpeg-turbo is not installed (<see cref="IsAvailable"/>).
    /// </summary>
    public static RgbaImage Decode(ReadOnlySpan<byte> jpeg)
    {
        if (!IsJpeg(jpeg)) throw new JpegException("Not a JPEG: the start-of-image marker is missing.");
        if (!Native.Load()) throw new DllNotFoundException($"JPEG needs {Library} (libjpeg-turbo), which is not installed.");

        IntPtr handle = Native.InitDecompress();
        if (handle == 0) throw new JpegException("libjpeg-turbo could not make a decompressor.");
        try
        {
            int width, height, subsampling, colorspace;
            fixed (byte* src = jpeg)
            {
                if (Native.DecompressHeader3(handle, src, (nuint)jpeg.Length, &width, &height, &subsampling, &colorspace) != 0)
                    throw new JpegException($"Unreadable header: {Native.ErrorText(handle)}.");
                if (width <= 0 || height <= 0) throw new JpegException($"A JPEG of {width}x{height} has no pixels.");
                if ((long)width * height * RgbaImage.BytesPerPixel > Array.MaxLength)
                    throw new JpegException($"{width}x{height} is larger than an image can hold.");

                // libjpeg converts every colour space to RGB but CMYK, which it hands back as is.
                bool cmyk = colorspace is ColorspaceCmyk or ColorspaceYcck;
                var image = new RgbaImage(width, height);
                fixed (byte* dst = image.Pixels)
                {
                    int format = cmyk ? PixelCmyk : PixelRgba;
                    if (Native.Decompress2(handle, src, (nuint)jpeg.Length, dst, width, width * 4, height, format, FlagLimitScans) != 0
                        && Native.ErrorCode(handle) != ErrorWarning)
                        throw new JpegException($"Corrupt: {Native.ErrorText(handle)}.");
                }
                if (cmyk) CmykToRgba(image.Pixels);
                int orientation = Orientation(jpeg);
                return orientation == 1 ? image : Orient(image, orientation);
            }
        }
        finally
        {
            Native.Destroy(handle);
        }
    }

    // ── Colour ───────────────────────────────────────────────────────────────

    // Adobe's CMYK, stored inverted (255 is no ink), to RGB in place: each channel
    // is the inverted ink times the inverted black, as Skia and browsers do it.
    private static void CmykToRgba(Span<byte> pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int k = pixels[i + 3];
            pixels[i] = MulDiv255(pixels[i], k);
            pixels[i + 1] = MulDiv255(pixels[i + 1], k);
            pixels[i + 2] = MulDiv255(pixels[i + 2], k);
            pixels[i + 3] = 255;
        }
    }

    // a * b / 255, rounded: Skia's SkMulDiv255Round.
    private static byte MulDiv255(int a, int b)
    {
        int product = a * b + 128;
        return (byte)((product + (product >> 8)) >> 8);
    }

    // ── EXIF orientation ─────────────────────────────────────────────────────

    /// <summary>
    /// The EXIF orientation (1-8) from the APP1 segment before the first scan; 1
    /// (as stored) when there is none or it cannot be read.
    /// </summary>
    internal static int Orientation(ReadOnlySpan<byte> jpeg)
    {
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) return 1;
            byte marker = jpeg[pos + 1];
            if (marker == 0xFF) { pos++; continue; }                          // fill byte
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { pos += 2; continue; } // no length
            if (marker is 0xDA or 0xD9) return 1;                              // first scan, or the end
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos + 2));
            if (length < 2 || pos + 2 + length > jpeg.Length) return 1;
            var segment = jpeg.Slice(pos + 4, length - 2);
            if (marker == 0xE1 && segment.StartsWith("Exif\0\0"u8)) return TiffOrientation(segment[6..]);
            pos += 2 + length;
        }
        return 1;
    }

    // Tag 0x0112 of the TIFF structure's first IFD.
    private static int TiffOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 1;
        bool little = tiff[0] == 'I' && tiff[1] == 'I';
        if (!little && !(tiff[0] == 'M' && tiff[1] == 'M')) return 1;
        if (U16(tiff, 2, little) != 42) return 1;
        uint ifd = U32(tiff, 4, little);
        if (ifd > tiff.Length - 2) return 1;
        int count = U16(tiff, (int)ifd, little);
        for (int i = 0; i < count; i++)
        {
            long entry = ifd + 2 + i * 12L;
            if (entry + 12 > tiff.Length) return 1;
            if (U16(tiff, (int)entry, little) != 0x0112) continue;
            int value = U16(tiff, (int)entry + 2, little) == 3 ? U16(tiff, (int)entry + 8, little) : 0; // a SHORT, in the value field
            return value is >= 1 and <= 8 ? value : 1;
        }
        return 1;
    }

    private static ushort U16(ReadOnlySpan<byte> d, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(at)) : BinaryPrimitives.ReadUInt16BigEndian(d.Slice(at));

    private static uint U32(ReadOnlySpan<byte> d, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(at)) : BinaryPrimitives.ReadUInt32BigEndian(d.Slice(at));

    /// <summary>
    /// The image as it is meant to be seen, for an EXIF orientation of 2-8: mirrored,
    /// turned, or both. Orientations 5-8 swap width and height.
    /// </summary>
    internal static RgbaImage Orient(RgbaImage stored, int orientation)
    {
        int w = stored.Width, h = stored.Height;
        bool swap = orientation >= 5;
        var upright = new RgbaImage(swap ? h : w, swap ? w : h, stored.Alpha);
        var src = MemoryMarshal.Cast<byte, uint>(stored.Pixels);
        var dst = MemoryMarshal.Cast<byte, uint>(upright.Pixels);
        int dw = upright.Width, dh = upright.Height;
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                // Where the stored pixel shown at (x, y) is.
                (int sx, int sy) = orientation switch
                {
                    2 => (w - 1 - x, y),         // mirrored left to right
                    3 => (w - 1 - x, h - 1 - y), // turned 180°
                    4 => (x, h - 1 - y),         // mirrored top to bottom
                    5 => (y, x),                 // transposed
                    6 => (y, h - 1 - x),         // stored turned 90° anticlockwise: turn it clockwise
                    7 => (w - 1 - y, h - 1 - x), // transversed
                    _ => (w - 1 - y, x),         // 8: stored turned 90° clockwise: turn it back
                };
                dst[y * dw + x] = src[sy * w + sx];
            }
        }
        return upright;
    }

    // ── libturbojpeg ─────────────────────────────────────────────────────────

    // TurboJPEG's functions, found once. unsigned long is pointer-sized on LP64
    // Linux and wasm32 alike, so nuint.
    private static class Native
    {
        private static int s_state; // 0 not tried, 1 loaded, -1 not there
        private static delegate* unmanaged<IntPtr> s_initDecompress;
        private static delegate* unmanaged<IntPtr, byte*, nuint, int*, int*, int*, int*, int> s_decompressHeader3;
        private static delegate* unmanaged<IntPtr, byte*, nuint, byte*, int, int, int, int, int, int> s_decompress2;
        private static delegate* unmanaged<IntPtr, int> s_getErrorCode;
        private static delegate* unmanaged<IntPtr, byte*> s_getErrorStr2;
        private static delegate* unmanaged<IntPtr, int> s_destroy;

        // Two threads that both find it untried both load it, and find the same functions.
        public static bool Load()
        {
            int state = Volatile.Read(ref s_state);
            if (state != 0) return state > 0;
            bool ok = false;
            if (NativeLibrary.TryLoad(Library, out IntPtr lib)
                && NativeLibrary.TryGetExport(lib, "tjInitDecompress", out IntPtr init)
                && NativeLibrary.TryGetExport(lib, "tjDecompressHeader3", out IntPtr header)
                && NativeLibrary.TryGetExport(lib, "tjDecompress2", out IntPtr decompress)
                && NativeLibrary.TryGetExport(lib, "tjGetErrorCode", out IntPtr errorCode)
                && NativeLibrary.TryGetExport(lib, "tjGetErrorStr2", out IntPtr errorStr)
                && NativeLibrary.TryGetExport(lib, "tjDestroy", out IntPtr destroy))
            {
                s_initDecompress = (delegate* unmanaged<IntPtr>)init;
                s_decompressHeader3 = (delegate* unmanaged<IntPtr, byte*, nuint, int*, int*, int*, int*, int>)header;
                s_decompress2 = (delegate* unmanaged<IntPtr, byte*, nuint, byte*, int, int, int, int, int, int>)decompress;
                s_getErrorCode = (delegate* unmanaged<IntPtr, int>)errorCode;
                s_getErrorStr2 = (delegate* unmanaged<IntPtr, byte*>)errorStr;
                s_destroy = (delegate* unmanaged<IntPtr, int>)destroy;
                ok = true;
            }
            Volatile.Write(ref s_state, ok ? 1 : -1);
            return ok;
        }

        public static IntPtr InitDecompress() => s_initDecompress();
        public static int DecompressHeader3(IntPtr h, byte* src, nuint size, int* width, int* height, int* subsampling, int* colorspace)
            => s_decompressHeader3(h, src, size, width, height, subsampling, colorspace);
        public static int Decompress2(IntPtr h, byte* src, nuint size, byte* dst, int width, int pitch, int height, int format, int flags)
            => s_decompress2(h, src, size, dst, width, pitch, height, format, flags);
        public static int ErrorCode(IntPtr h) => s_getErrorCode(h);
        public static string ErrorText(IntPtr h) => Marshal.PtrToStringUTF8((IntPtr)s_getErrorStr2(h)) ?? "unknown error";
        public static void Destroy(IntPtr h) => s_destroy(h);
    }
}
