using Bibui.Core.Imaging;

namespace Bibui.Imaging;

/// <summary>The formats <see cref="Images.Decode(ReadOnlySpan{byte})"/> knows, by their first bytes.</summary>
public enum ImageFormat
{
    /// <summary>None of the others: not a picture bibui decodes.</summary>
    Unknown,

    /// <summary>PNG, decoded by <see cref="Png"/>, in managed code.</summary>
    Png,

    /// <summary>JPEG, decoded by <see cref="Jpeg"/>, through the system's libjpeg-turbo.</summary>
    Jpeg,

    /// <summary>WebP, decoded by <see cref="WebP"/>, through the system's libwebp.</summary>
    WebP,
}

/// <summary>
/// A picture of any format bibui decodes, told apart by its first bytes: what an app
/// that is handed a file (an attachment, something pasted, a download) calls, not
/// knowing what it is. The result is the format's own decode (<see cref="Png"/>,
/// <see cref="Jpeg"/>, <see cref="WebP"/>), straight 8-bit RGBA, ready for
/// <c>CreateTexture</c> as <c>RGBA8</c>.
/// </summary>
public static class Images
{
    /// <summary>The format the bytes begin as; <see cref="ImageFormat.Unknown"/> if none bibui decodes.</summary>
    public static ImageFormat FormatOf(ReadOnlySpan<byte> data) =>
        Png.IsPng(data) ? ImageFormat.Png
        : Jpeg.IsJpeg(data) ? ImageFormat.Jpeg
        : WebP.IsWebP(data) ? ImageFormat.WebP
        : ImageFormat.Unknown;

    /// <summary>
    /// True if a picture of <paramref name="format"/> can be decoded here: PNG always,
    /// JPEG and WebP when the system has their library.
    /// </summary>
    public static bool CanDecode(ImageFormat format) => format switch
    {
        ImageFormat.Png => true,
        ImageFormat.Jpeg => Jpeg.IsAvailable,
        ImageFormat.WebP => WebP.IsAvailable,
        _ => false,
    };

    /// <summary>
    /// Decodes a PNG, JPEG or WebP, whichever the bytes are, to straight 8-bit RGBA;
    /// null if it cannot: an unknown format, a corrupt or truncated file, an animated
    /// WebP, or a format whose system library is not installed. It never throws for
    /// what is in the bytes. Use the format's own <c>Decode</c> to learn why.
    /// </summary>
    public static RgbaImage? Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            return FormatOf(data) switch
            {
                ImageFormat.Png => Png.Decode(data),
                ImageFormat.Jpeg => Jpeg.Decode(data),
                ImageFormat.WebP => WebP.Decode(data),
                _ => null,
            };
        }
        catch (Exception e) when (e is FormatException or DllNotFoundException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="Decode(ReadOnlySpan{byte})"/> on a file's bytes; null as well when the
    /// file cannot be read.
    /// </summary>
    public static RgbaImage? DecodeFile(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        return Decode(data);
    }
}
