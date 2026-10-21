using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace Bibui.Core.Imaging;

/// <summary>A PNG that cannot be decoded: not a PNG, truncated, corrupt, or a form PNG does not define.</summary>
public sealed class PngException(string message) : FormatException(message);

/// <summary>
/// Managed PNG encode and decode, with no native code: inflate and deflate are
/// <see cref="ZLibStream"/>.
///
/// <para>
/// <b>Decode</b> reads every form the PNG specification defines: colour types 0
/// (grey), 2 (RGB), 3 (palette), 4 (grey + alpha) and 6 (RGBA), at every bit depth
/// each allows (1, 2, 4, 8, 16), all five row filters, Adam7 interlacing, and
/// <c>tRNS</c> transparency. The result is always straight 8-bit RGBA. A 16-bit
/// sample keeps its high byte (libpng's strip_16), and low bit depths are scaled
/// up exactly (a 4-bit 15 is 255). A 16-bit <c>tRNS</c> key is still
/// matched against the full sample. Colour management is not applied: <c>gAMA</c>, <c>iCCP</c>, <c>sRGB</c>,
/// <c>cHRM</c> and every other ancillary chunk are skipped, so the bytes are the
/// stored samples. This is exactly what the Skia backend's decode to a bitmap with
/// no target colour space gives, so an image draws the same on either backend;
/// the tests hold it to that pixel for pixel.
/// </para>
///
/// <para>
/// <b>Encode</b> always writes 8-bit RGBA (colour type 6), not interlaced, choosing
/// a filter per row by the usual minimum-sum-of-absolute-differences rule, and no
/// ancillary chunks. It is deterministic: the same pixels give the same bytes,
/// whatever the stride or the memory they sit in. The bytes depend on the zlib
/// that .NET ships, so a different runtime may compress differently; the pixels
/// never change.
/// </para>
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private const uint ChunkIhdr = 0x49484452; // IHDR
    private const uint ChunkPlte = 0x504C5445; // PLTE
    private const uint ChunkIdat = 0x49444154; // IDAT
    private const uint ChunkIend = 0x49454E44; // IEND
    private const uint ChunkTrns = 0x74524E53; // tRNS

    /// <summary>Largest IDAT chunk the encoder writes.</summary>
    private const int IdatChunkSize = 1 << 18;

    // Adam7: x0, y0, dx, dy for each of the seven passes.
    private static ReadOnlySpan<byte> Adam7 =>
    [
        0, 0, 8, 8,
        4, 0, 8, 8,
        0, 4, 4, 8,
        2, 0, 4, 4,
        0, 2, 2, 4,
        1, 0, 2, 2,
        0, 1, 1, 2,
    ];

    /// <summary>True if the bytes begin with the PNG signature.</summary>
    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static RgbaImage DecodeFile(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>Decodes a PNG to straight 8-bit RGBA. Throws <see cref="PngException"/> if it cannot.</summary>
    public static RgbaImage Decode(ReadOnlySpan<byte> png)
    {
        if (!IsPng(png)) throw new PngException("Not a PNG: the signature is missing.");

        Header? header = null;
        ReadOnlySpan<byte> palette = default;
        ReadOnlySpan<byte> transparency = default;
        bool sawIdat = false, idatEnded = false, sawEnd = false;

        // Compressed image data, gathered from every IDAT (an encoder may split it into many).
        byte[] compressed = ArrayPool<byte>.Shared.Rent(Math.Max(256, png.Length));
        int compressedLength = 0;
        try
        {
            int pos = Signature.Length;
            while (!sawEnd)
            {
                if (png.Length - pos < 12) throw new PngException("Truncated: the file ends inside a chunk header, before IEND.");
                uint length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(pos));
                if (length > int.MaxValue || length > png.Length - pos - 12)
                    throw new PngException($"Truncated: a chunk claims {length} bytes past the end of the file.");
                uint type = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(pos + 4));
                var data = png.Slice(pos + 8, (int)length);
                uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(pos + 8 + (int)length));
                bool critical = (type & 0x20000000) == 0;
                bool crcOk = Crc32.HashToUInt32(png.Slice(pos + 4, (int)length + 4)) == crc;
                pos += 12 + (int)length;

                if (header is null && type != ChunkIhdr) throw new PngException("IHDR is not the first chunk.");
                if (sawIdat && type != ChunkIdat) idatEnded = true;

                switch (type)
                {
                    case ChunkIhdr:
                        if (header is not null) throw new PngException("More than one IHDR.");
                        if (!crcOk) throw new PngException("IHDR fails its CRC.");
                        header = Header.Parse(data);
                        break;
                    case ChunkPlte:
                        if (!crcOk) throw new PngException("PLTE fails its CRC.");
                        if (length % 3 != 0 || length == 0 || length > 256 * 3) throw new PngException($"PLTE of {length} bytes is not 1-256 entries.");
                        palette = data;
                        break;
                    case ChunkTrns:
                        // An ancillary chunk with a bad CRC is dropped, as libpng does.
                        if (crcOk) transparency = data;
                        break;
                    case ChunkIdat:
                        if (!crcOk) throw new PngException("IDAT fails its CRC.");
                        if (idatEnded) throw new PngException("IDAT chunks are not consecutive.");
                        sawIdat = true;
                        if (compressedLength + data.Length > compressed.Length)
                        {
                            var bigger = ArrayPool<byte>.Shared.Rent(compressedLength + data.Length);
                            compressed.AsSpan(0, compressedLength).CopyTo(bigger);
                            ArrayPool<byte>.Shared.Return(compressed);
                            compressed = bigger;
                        }
                        data.CopyTo(compressed.AsSpan(compressedLength));
                        compressedLength += data.Length;
                        break;
                    case ChunkIend:
                        sawEnd = true;
                        break;
                    default:
                        if (critical) throw new PngException($"Unknown critical chunk '{TypeName(type)}'.");
                        break; // gAMA, iCCP, sRGB, sBIT, pHYs, text…: not needed for the pixels
                }
            }

            var h = header!.Value;
            if (!sawIdat) throw new PngException("No IDAT: the file has no image data.");
            if (h.ColorType == 3 && palette.IsEmpty) throw new PngException("A palette image without PLTE.");

            var raw = Inflate(compressed, compressedLength, h.RawLength());
            try
            {
                return Reconstruct(h, raw, palette, transparency);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    // ── Decode internals ─────────────────────────────────────────────────────

    private readonly record struct Header(int Width, int Height, int BitDepth, int ColorType, bool Interlaced)
    {
        public int Channels => ColorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        public int BitsPerPixel => Channels * BitDepth;
        /// <summary>Filter stride: bytes per complete pixel, at least one.</summary>
        public int FilterBpp => Math.Max(1, BitsPerPixel / 8);
        public long RowBytes(int width) => ((long)width * BitsPerPixel + 7) / 8;

        public long RawLength()
        {
            if (!Interlaced) return Height * (1 + RowBytes(Width));
            long total = 0;
            for (int p = 0; p < 7; p++)
            {
                var (pw, ph) = PassSize(p);
                if (pw > 0 && ph > 0) total += ph * (1 + RowBytes(pw));
            }
            return total;
        }

        public (int W, int H) PassSize(int pass)
        {
            int x0 = Adam7[pass * 4], y0 = Adam7[pass * 4 + 1], dx = Adam7[pass * 4 + 2], dy = Adam7[pass * 4 + 3];
            return ((Width - x0 + dx - 1) / dx, (Height - y0 + dy - 1) / dy);
        }

        public static Header Parse(ReadOnlySpan<byte> d)
        {
            if (d.Length != 13) throw new PngException($"IHDR is {d.Length} bytes, not 13.");
            uint w = BinaryPrimitives.ReadUInt32BigEndian(d);
            uint h = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(4));
            int depth = d[8], type = d[9];
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) throw new PngException($"Image size {w}x{h} is invalid.");
            if (d[10] != 0) throw new PngException($"Compression method {d[10]} is not defined.");
            if (d[11] != 0) throw new PngException($"Filter method {d[11]} is not defined.");
            if (d[12] > 1) throw new PngException($"Interlace method {d[12]} is not defined.");
            bool valid = type switch
            {
                0 => depth is 1 or 2 or 4 or 8 or 16,
                3 => depth is 1 or 2 or 4 or 8,
                2 or 4 or 6 => depth is 8 or 16,
                _ => false,
            };
            if (!valid) throw new PngException($"Colour type {type} at bit depth {depth} is not defined.");
            if ((long)w * h * RgbaImage.BytesPerPixel > Array.MaxLength) throw new PngException($"{w}x{h} is too large to decode.");
            var header = new Header((int)w, (int)h, depth, type, d[12] == 1);
            if (header.RawLength() > Array.MaxLength) throw new PngException($"{w}x{h} is too large to decode.");
            return header;
        }
    }

    private static byte[] Inflate(byte[] compressed, int compressedLength, long rawLength)
    {
        var raw = ArrayPool<byte>.Shared.Rent((int)rawLength);
        try
        {
            using var input = new MemoryStream(compressed, 0, compressedLength, writable: false);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            int read = 0;
            while (read < rawLength)
            {
                int n = z.Read(raw, read, (int)rawLength - read);
                if (n == 0) throw new PngException($"Truncated: image data inflates to {read} bytes, {rawLength} expected.");
                read += n;
            }
            return raw;
        }
        catch (InvalidDataException e)
        {
            ArrayPool<byte>.Shared.Return(raw);
            throw new PngException($"Image data is not a valid zlib stream: {e.Message}");
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(raw);
            throw;
        }
    }

    private static RgbaImage Reconstruct(Header h, byte[] raw, ReadOnlySpan<byte> palette, ReadOnlySpan<byte> trns)
    {
        var image = new RgbaImage(h.Width, h.Height);
        var pixels = image.Pixels;
        Span<byte> lut = stackalloc byte[256 * 4];
        if (h.ColorType == 3) BuildPaletteLut(palette, trns, lut);
        int trnsKey = TransparentKey(h, trns, out var trnsRgb);

        int bpp = h.FilterBpp;
        int offset = 0;

        if (!h.Interlaced)
        {
            int rowBytes = (int)h.RowBytes(h.Width);
            for (int y = 0; y < h.Height; y++)
            {
                var row = raw.AsSpan(offset + 1, rowBytes);
                var prev = y == 0 ? default : raw.AsSpan(offset + 1 - (rowBytes + 1), rowBytes);
                Unfilter(raw[offset], row, prev, bpp);
                ToRgba(h, row, pixels.Slice(y * image.Stride, h.Width * 4), h.Width, lut, trnsKey, trnsRgb);
                offset += rowBytes + 1;
            }
            return image;
        }

        var passRow = new byte[h.Width * 4];
        for (int p = 0; p < 7; p++)
        {
            var (pw, ph) = h.PassSize(p);
            if (pw == 0 || ph == 0) continue;
            int x0 = Adam7[p * 4], y0 = Adam7[p * 4 + 1], dx = Adam7[p * 4 + 2], dy = Adam7[p * 4 + 3];
            int rowBytes = (int)h.RowBytes(pw);
            for (int r = 0; r < ph; r++)
            {
                var row = raw.AsSpan(offset + 1, rowBytes);
                var prev = r == 0 ? default : raw.AsSpan(offset + 1 - (rowBytes + 1), rowBytes);
                Unfilter(raw[offset], row, prev, bpp);
                ToRgba(h, row, passRow, pw, lut, trnsKey, trnsRgb);
                var dest = pixels.Slice((y0 + r * dy) * image.Stride);
                for (int i = 0; i < pw; i++)
                    passRow.AsSpan(i * 4, 4).CopyTo(dest.Slice((x0 + i * dx) * 4));
                offset += rowBytes + 1;
            }
        }
        return image;
    }

    /// <summary>Undoes one row's filter in place. <paramref name="prev"/> is empty for the first row of a pass.</summary>
    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> prev, int bpp)
    {
        int n = row.Length;
        switch (filter)
        {
            case 0:
                return;
            case 1: // Sub
                for (int i = bpp; i < n; i++) row[i] += row[i - bpp];
                return;
            case 2: // Up
                if (prev.IsEmpty) return;
                AddUp(row, prev);
                return;
            case 3: // Average
                if (prev.IsEmpty)
                {
                    for (int i = bpp; i < n; i++) row[i] += (byte)(row[i - bpp] >> 1);
                    return;
                }
                for (int i = 0; i < bpp && i < n; i++) row[i] += (byte)(prev[i] >> 1);
                for (int i = bpp; i < n; i++) row[i] += (byte)((row[i - bpp] + prev[i]) >> 1);
                return;
            case 4: // Paeth
                if (prev.IsEmpty)
                {
                    // With no row above, Paeth picks the left neighbour: Sub.
                    for (int i = bpp; i < n; i++) row[i] += row[i - bpp];
                    return;
                }
                for (int i = 0; i < bpp && i < n; i++) row[i] += prev[i];
                for (int i = bpp; i < n; i++) row[i] += Paeth(row[i - bpp], prev[i], prev[i - bpp]);
                return;
            default:
                throw new PngException($"Row filter {filter} is not defined.");
        }
    }

    private static void AddUp(Span<byte> row, ReadOnlySpan<byte> prev)
    {
        int i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            int width = System.Numerics.Vector<byte>.Count;
            for (; i + width <= row.Length; i += width)
            {
                var sum = new System.Numerics.Vector<byte>(row.Slice(i)) + new System.Numerics.Vector<byte>(prev.Slice(i));
                sum.CopyTo(row.Slice(i));
            }
        }
        for (; i < row.Length; i++) row[i] += prev[i];
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static void BuildPaletteLut(ReadOnlySpan<byte> palette, ReadOnlySpan<byte> trns, Span<byte> lut)
    {
        int count = palette.Length / 3;
        for (int i = 0; i < 256; i++)
        {
            // An index past the palette takes the last entry.
            int e = Math.Min(i, count - 1);
            lut[i * 4] = palette[e * 3];
            lut[i * 4 + 1] = palette[e * 3 + 1];
            lut[i * 4 + 2] = palette[e * 3 + 2];
            lut[i * 4 + 3] = e < trns.Length ? trns[e] : (byte)255;
        }
    }

    /// <summary>
    /// The single transparent sample for grey (returned) or RGB (in <paramref name="rgb"/>)
    /// images, compared at the stored bit depth; -1 when there is none.
    /// </summary>
    private static int TransparentKey(Header h, ReadOnlySpan<byte> trns, out (int R, int G, int B) rgb)
    {
        rgb = (-1, -1, -1);
        int mask = (1 << h.BitDepth) - 1;
        if (h.ColorType == 0 && trns.Length >= 2)
            return BinaryPrimitives.ReadUInt16BigEndian(trns) & mask;
        if (h.ColorType == 2 && trns.Length >= 6)
        {
            rgb = (BinaryPrimitives.ReadUInt16BigEndian(trns) & mask,
                   BinaryPrimitives.ReadUInt16BigEndian(trns.Slice(2)) & mask,
                   BinaryPrimitives.ReadUInt16BigEndian(trns.Slice(4)) & mask);
            return 0;
        }
        return -1;
    }

    /// <summary>Converts one unfiltered row of <paramref name="width"/> pixels to straight RGBA8.</summary>
    private static void ToRgba(Header h, ReadOnlySpan<byte> src, Span<byte> dst, int width, ReadOnlySpan<byte> lut, int trnsKey, (int R, int G, int B) trnsRgb)
    {
        switch (h.ColorType, h.BitDepth)
        {
            case (6, 8):
                src.Slice(0, width * 4).CopyTo(dst);
                return;
            case (6, 16):
                for (int x = 0, s = 0; x < width; x++, s += 8)
                {
                    dst[x * 4] = src[s];
                    dst[x * 4 + 1] = src[s + 2];
                    dst[x * 4 + 2] = src[s + 4];
                    dst[x * 4 + 3] = src[s + 6];
                }
                return;
            case (2, 8):
                for (int x = 0, s = 0; x < width; x++, s += 3)
                {
                    byte r = src[s], g = src[s + 1], b = src[s + 2];
                    dst[x * 4] = r; dst[x * 4 + 1] = g; dst[x * 4 + 2] = b;
                    dst[x * 4 + 3] = trnsKey == 0 && r == trnsRgb.R && g == trnsRgb.G && b == trnsRgb.B ? (byte)0 : (byte)255;
                }
                return;
            case (2, 16):
                for (int x = 0, s = 0; x < width; x++, s += 6)
                {
                    int r = (src[s] << 8) | src[s + 1], g = (src[s + 2] << 8) | src[s + 3], b = (src[s + 4] << 8) | src[s + 5];
                    dst[x * 4] = src[s];
                    dst[x * 4 + 1] = src[s + 2];
                    dst[x * 4 + 2] = src[s + 4];
                    dst[x * 4 + 3] = trnsKey == 0 && r == trnsRgb.R && g == trnsRgb.G && b == trnsRgb.B ? (byte)0 : (byte)255;
                }
                return;
            case (4, 8):
                for (int x = 0, s = 0; x < width; x++, s += 2)
                {
                    byte v = src[s];
                    dst[x * 4] = v; dst[x * 4 + 1] = v; dst[x * 4 + 2] = v; dst[x * 4 + 3] = src[s + 1];
                }
                return;
            case (4, 16):
                for (int x = 0, s = 0; x < width; x++, s += 4)
                {
                    byte v = src[s];
                    dst[x * 4] = v; dst[x * 4 + 1] = v; dst[x * 4 + 2] = v; dst[x * 4 + 3] = src[s + 2];
                }
                return;
            case (0, 16):
                for (int x = 0, s = 0; x < width; x++, s += 2)
                {
                    byte v = src[s];
                    dst[x * 4] = v; dst[x * 4 + 1] = v; dst[x * 4 + 2] = v;
                    dst[x * 4 + 3] = ((src[s] << 8) | src[s + 1]) == trnsKey ? (byte)0 : (byte)255;
                }
                return;
            case (0, _):
            {
                // 1, 2, 4 or 8 bits: scale to 0..255 exactly (255 / max is a whole number).
                int depth = h.BitDepth, max = (1 << depth) - 1, scale = 255 / max;
                for (int x = 0; x < width; x++)
                {
                    int v = Sample(src, x, depth);
                    byte g = (byte)(v * scale);
                    dst[x * 4] = g; dst[x * 4 + 1] = g; dst[x * 4 + 2] = g;
                    dst[x * 4 + 3] = v == trnsKey ? (byte)0 : (byte)255;
                }
                return;
            }
            case (3, _):
                for (int x = 0; x < width; x++)
                    lut.Slice(Sample(src, x, h.BitDepth) * 4, 4).CopyTo(dst.Slice(x * 4));
                return;
            default:
                throw new PngException($"Colour type {h.ColorType} at bit depth {h.BitDepth} is not defined.");
        }
    }

    /// <summary>The <paramref name="x"/>th sample of a packed row, most significant bits first.</summary>
    private static int Sample(ReadOnlySpan<byte> row, int x, int depth)
    {
        if (depth == 8) return row[x];
        int perByte = 8 / depth;
        int shift = 8 - depth * (x % perByte + 1);
        return (row[x / perByte] >> shift) & ((1 << depth) - 1);
    }

    private static string TypeName(uint type) => new([(char)(type >> 24), (char)((type >> 16) & 0xFF), (char)((type >> 8) & 0xFF), (char)(type & 0xFF)]);

    // ── Encode ───────────────────────────────────────────────────────────────

    public static void EncodeFile(RgbaImage image, string path)
    {
        using var file = File.Create(path);
        Encode(image, file);
    }

    public static byte[] Encode(RgbaImage image)
    {
        using var ms = new MemoryStream();
        Encode(image, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes <paramref name="image"/> as an 8-bit RGBA PNG. A premultiplied image is
    /// divided by its alpha first (<c>c * 255 / a</c>, rounded), since PNG stores straight colour.
    /// </summary>
    public static void Encode(RgbaImage image, Stream output)
    {
        ArgumentNullException.ThrowIfNull(image);
        Encode(image.Pixels, image.Width, image.Height, image.Stride, ChannelOrder.Rgba, image.Alpha, output);
    }

    /// <summary>
    /// Encodes pixels held anywhere — a rasterizer's frame, a native buffer — without
    /// first making an image of them. <paramref name="order"/> says whether each pixel
    /// is R,G,B,A or B,G,R,A (what a CPU renderer's frames, <c>RasterPixels</c>, hold), and
    /// <paramref name="alpha"/> whether colour is premultiplied.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int stride, ChannelOrder order, AlphaMode alpha)
    {
        using var ms = new MemoryStream();
        Encode(pixels, width, height, stride, order, alpha, ms);
        return ms.ToArray();
    }

    /// <inheritdoc cref="Encode(ReadOnlySpan{byte}, int, int, int, ChannelOrder, AlphaMode)"/>
    public static void Encode(ReadOnlySpan<byte> pixels, int width, int height, int stride, ChannelOrder order, AlphaMode alpha, Stream output)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"PNG cannot hold a {width}x{height} image.", nameof(width));
        if (stride < (long)width * 4) throw new ArgumentOutOfRangeException(nameof(stride), $"Stride {stride} is shorter than a row of {width} pixels.");
        if (pixels.Length < (long)(height - 1) * stride + (long)width * 4)
            throw new ArgumentException($"{pixels.Length} bytes cannot hold {width}x{height} at stride {stride}.", nameof(pixels));

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4), (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // not interlaced

        output.Write(Signature);
        WriteChunk(output, ChunkIhdr, header);

        int rowBytes = width * 4;
        using (var idat = new IdatStream(output))
        {
            using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                var prev = new byte[rowBytes];
                var cur = new byte[rowBytes];
                // One candidate row per filter, each led by its filter byte.
                var candidates = new byte[5 * (rowBytes + 1)];
                for (int y = 0; y < height; y++)
                {
                    var source = pixels.Slice(y * stride, rowBytes);
                    if (order == ChannelOrder.Bgra) SwapRedBlue(source, cur);
                    else source.CopyTo(cur);
                    if (alpha == AlphaMode.Premultiplied) Unpremultiply(cur);

                    var best = ChooseFilter(cur, y == 0 ? default : prev, candidates, rowBytes);
                    z.Write(best);
                    (prev, cur) = (cur, prev);
                }
            }
        }

        WriteChunk(output, ChunkIend, []);
    }

    private static void SwapRedBlue(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i += 4)
        {
            dst[i] = src[i + 2];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i];
            dst[i + 3] = src[i + 3];
        }
    }

    private static void Unpremultiply(Span<byte> px)
    {
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 255) continue;
            if (a == 0) { px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; continue; }
            px[i] = (byte)Math.Min(255, (px[i] * 255 + a / 2) / a);
            px[i + 1] = (byte)Math.Min(255, (px[i + 1] * 255 + a / 2) / a);
            px[i + 2] = (byte)Math.Min(255, (px[i + 2] * 255 + a / 2) / a);
        }
    }

    /// <summary>
    /// Filters the row all five ways and returns the one whose bytes, read as signed,
    /// have the smallest sum of magnitudes. Ties go to the lower filter number.
    /// </summary>
    private static ReadOnlySpan<byte> ChooseFilter(ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, byte[] candidates, int n)
    {
        const int bpp = 4;
        int stride = n + 1;
        var none = candidates.AsSpan(0, stride);
        var sub = candidates.AsSpan(stride, stride);
        var up = candidates.AsSpan(2 * stride, stride);
        var avg = candidates.AsSpan(3 * stride, stride);
        var paeth = candidates.AsSpan(4 * stride, stride);
        none[0] = 0; sub[0] = 1; up[0] = 2; avg[0] = 3; paeth[0] = 4;

        long sNone = 0, sSub = 0, sUp = 0, sAvg = 0, sPaeth = 0;
        bool top = prev.IsEmpty;
        for (int i = 0; i < n; i++)
        {
            byte x = cur[i];
            byte a = i >= bpp ? cur[i - bpp] : (byte)0;
            byte b = top ? (byte)0 : prev[i];
            byte c = i >= bpp && !top ? prev[i - bpp] : (byte)0;

            byte f0 = x;
            byte f1 = (byte)(x - a);
            byte f2 = (byte)(x - b);
            byte f3 = (byte)(x - ((a + b) >> 1));
            byte f4 = (byte)(x - Paeth(a, b, c));
            none[i + 1] = f0; sub[i + 1] = f1; up[i + 1] = f2; avg[i + 1] = f3; paeth[i + 1] = f4;
            sNone += Magnitude(f0);
            sSub += Magnitude(f1);
            sUp += Magnitude(f2);
            sAvg += Magnitude(f3);
            sPaeth += Magnitude(f4);
        }

        int best = 0;
        long bestSum = sNone;
        if (sSub < bestSum) { best = 1; bestSum = sSub; }
        if (sUp < bestSum) { best = 2; bestSum = sUp; }
        if (sAvg < bestSum) { best = 3; bestSum = sAvg; }
        if (sPaeth < bestSum) { best = 4; }
        return candidates.AsSpan(best * stride, stride);
    }

    /// <summary>|b| with b read as a signed byte: 0..128.</summary>
    private static int Magnitude(byte b) => b < 128 ? b : 256 - b;

    private static void WriteChunk(Stream output, uint type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(head.Slice(4), type);
        var crc = new Crc32();
        crc.Append(head.Slice(4));
        crc.Append(data);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc.GetCurrentHashAsUInt32());
        output.Write(head);
        output.Write(data);
        output.Write(tail);
    }

    /// <summary>Buffers what deflate writes and emits it as IDAT chunks of at most <see cref="IdatChunkSize"/> bytes.</summary>
    private sealed class IdatStream(Stream output) : Stream
    {
        private readonly byte[] _buffer = new byte[IdatChunkSize];
        private int _length;

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                int n = Math.Min(data.Length, _buffer.Length - _length);
                data.Slice(0, n).CopyTo(_buffer.AsSpan(_length));
                _length += n;
                data = data.Slice(n);
                if (_length == _buffer.Length) Emit();
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        private void Emit()
        {
            if (_length == 0) return;
            WriteChunk(output, ChunkIdat, _buffer.AsSpan(0, _length));
            _length = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Emit();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
