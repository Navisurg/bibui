using System.Buffers.Binary;
using System.IO.Compression;

namespace Bibui.Raster.Managed.Backend;

/// <summary>
/// A small PNG codec on System.IO.Compression's zlib, so the managed backend
/// needs no native image library. Encodes 8-bit RGBA; decodes non-interlaced
/// greyscale, RGB, palette, grey+alpha and RGBA at 1–16 bits (16 is reduced to
/// 8). Stateless and thread-safe.
/// </summary>
internal static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] s_crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte x in a) c = s_crcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (byte x in b) c = s_crcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // The unpremultiply scale per alpha: (255 << 24) / a, rounded.
    private static readonly uint[] s_unpremul = BuildUnpremul();

    private static uint[] BuildUnpremul()
    {
        var t = new uint[256];
        for (uint a = 1; a < 256; a++) t[a] = (uint)(((255UL << 24) + a / 2) / a);
        return t;
    }

    /// <summary>
    /// PNG bytes for tightly packed BGRA8888 premultiplied pixels, unpremultiplied
    /// by a per-alpha scale table. The table is Skia 2.88's <c>SkUnPreMultiply</c>,
    /// reproduced exactly so a managed candidate's PNG holds the same samples as
    /// the Skia backend would write for the same pixels.
    /// </summary>
    public static byte[] EncodeBgraPremul(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("Too few pixel bytes.", nameof(bgra));
        using var ms = new MemoryStream();
        ms.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // RGBA
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR"u8, ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 4];
            for (int y = 0; y < height; y++)
            {
                row[0] = 0; // filter: none
                var src = bgra.Slice(y * width * 4, width * 4);
                for (int x = 0; x < width; x++)
                {
                    byte b = src[4 * x], g = src[4 * x + 1], r = src[4 * x + 2], a = src[4 * x + 3];
                    if (a != 255 && a != 0)
                    {
                        uint s = s_unpremul[a];
                        r = (byte)((s * r + (1u << 23)) >> 24);
                        g = (byte)((s * g + (1u << 23)) >> 24);
                        b = (byte)((s * b + (1u << 23)) >> 24);
                    }
                    else if (a == 0) { r = g = b = 0; }
                    row[1 + 4 * x] = r; row[2 + 4 * x] = g; row[3 + 4 * x] = b; row[4 + 4 * x] = a;
                }
                z.Write(row);
            }
        }
        WriteChunk(ms, "IDAT"u8, raw.GetBuffer().AsSpan(0, (int)raw.Length));
        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        type.CopyTo(head[4..]);
        s.Write(head);
        s.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(type, data));
        s.Write(crc);
    }

    /// <summary>Straight-alpha RGBA8888 pixels of a PNG, or null when it is not one this decoder reads.</summary>
    public static byte[]? DecodeRgba(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature)) return null;
        int pos = 8;
        int bitDepth = 0, colorType = -1, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png[pos..]);
            if (len < 0 || pos + 12 + len > png.Length) return null;
            var type = png.Slice(pos + 4, 4);
            var data = png.Slice(pos + 8, len);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colorType = data[9];
                interlace = data[12];
            }
            else if (type.SequenceEqual("PLTE"u8)) palette = data.ToArray();
            else if (type.SequenceEqual("tRNS"u8)) trns = data.ToArray();
            else if (type.SequenceEqual("IDAT"u8)) idat.Write(data);
            else if (type.SequenceEqual("IEND"u8)) break;
            pos += 12 + len;
        }
        if (width <= 0 || height <= 0 || interlace != 0 || (long)width * height > 1 << 28) return null;
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || bitDepth is not (1 or 2 or 4 or 8 or 16)) return null;
        if (colorType == 3 && palette is null) return null;
        if ((colorType is 2 or 4 or 6) && bitDepth < 8) return null;

        int bitsPerPixel = channels * bitDepth;
        int stride = (width * bitsPerPixel + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        try
        {
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n == 0) return null;
                read += n;
            }
        }
        catch (InvalidDataException) { return null; }

        // Unfilter in place.
        var prev = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            var row = raw.AsSpan(y * (stride + 1) + 1, stride);
            byte filter = raw[y * (stride + 1)];
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                row[i] = filter switch
                {
                    0 => row[i],
                    1 => (byte)(row[i] + a),
                    2 => (byte)(row[i] + b),
                    3 => (byte)(row[i] + ((a + b) >> 1)),
                    4 => (byte)(row[i] + Paeth(a, b, c)),
                    _ => row[i],
                };
            }
            row.CopyTo(prev);
        }

        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            var row = raw.AsSpan(y * (stride + 1) + 1, stride);
            for (int x = 0; x < width; x++)
            {
                byte r, g, b, al = 255;
                switch (colorType)
                {
                    case 0:
                    {
                        int v = Sample(row, x, bitDepth);
                        if (trns is { Length: >= 2 })
                        {
                            int full = bitDepth == 16 ? (row[2 * x] << 8) | row[2 * x + 1] : v;
                            if (full == BinaryPrimitives.ReadUInt16BigEndian(trns)) al = 0;
                        }
                        r = g = b = (byte)(bitDepth == 16 ? v : ScaleTo8(v, bitDepth));
                        break;
                    }
                    case 3:
                    {
                        int i = Sample(row, x, bitDepth);
                        if (3 * i + 2 >= palette!.Length) return null;
                        r = palette[3 * i]; g = palette[3 * i + 1]; b = palette[3 * i + 2];
                        if (trns is not null && i < trns.Length) al = trns[i];
                        break;
                    }
                    case 2:
                        r = Byte8(row, 3 * x, bitDepth); g = Byte8(row, 3 * x + 1, bitDepth); b = Byte8(row, 3 * x + 2, bitDepth);
                        break;
                    case 4:
                        r = g = b = Byte8(row, 2 * x, bitDepth); al = Byte8(row, 2 * x + 1, bitDepth);
                        break;
                    default:
                        r = Byte8(row, 4 * x, bitDepth); g = Byte8(row, 4 * x + 1, bitDepth);
                        b = Byte8(row, 4 * x + 2, bitDepth); al = Byte8(row, 4 * x + 3, bitDepth);
                        break;
                }
                int o = (y * width + x) * 4;
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = al;
            }
        }
        return rgba;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>The <paramref name="i"/>th sample of a row of 1/2/4/8-bit samples (16-bit: the high byte).</summary>
    private static int Sample(ReadOnlySpan<byte> row, int i, int bitDepth) => bitDepth switch
    {
        8 => row[i],
        16 => row[2 * i],
        _ => (row[i * bitDepth / 8] >> (8 - bitDepth - (i * bitDepth % 8))) & ((1 << bitDepth) - 1),
    };

    private static int ScaleTo8(int v, int bitDepth) => bitDepth switch
    {
        1 => v * 255,
        2 => v * 85,
        4 => v * 17,
        16 => v >> 8,
        _ => v,
    };

    private static byte Byte8(ReadOnlySpan<byte> row, int sampleIndex, int bitDepth)
        => bitDepth == 16 ? row[2 * sampleIndex] : row[sampleIndex];
}
