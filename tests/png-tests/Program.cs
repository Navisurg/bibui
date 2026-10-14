// The managed PNG codec against SkiaSharp, on every PNG the repo holds.
//
//   dotnet run --project tests/png-tests -c Release

using System.IO.Compression;
using Bibui.Imaging;
using SkiaSharp;

var root = FindRepoRoot();

int passed = 0, failed = 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] {name}");
        Console.WriteLine($"         {ex.Message}");
        failed++;
    }
}

var fixtures = Directory.GetFiles(Path.Combine(root, "tests", "png-tests", "fixtures"), "*.png").Order(StringComparer.Ordinal).ToArray();
// Image assets and committed screenshots elsewhere in the tree, plus whatever a
// local fixture run has left in artifacts/ (absent on a clean checkout).
var others = new[] { "samples", "artifacts" }
    .Select(d => Path.Combine(root, d))
    .Where(Directory.Exists)
    .SelectMany(d => Directory.EnumerateFiles(d, "*.png", SearchOption.AllDirectories))
    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
    .Order(StringComparer.Ordinal)
    .ToArray();
var corpus = fixtures.Concat(others).ToArray();

Console.WriteLine($"PNG corpus: {fixtures.Length} fixtures, {others.Length} others");

Run($"Decode: every PNG in the repo ({corpus.Length}) is pixel-identical to SkiaSharp's straight-RGBA decode", () =>
{
    var mismatches = new List<string>();
    foreach (var path in corpus)
    {
        var bytes = File.ReadAllBytes(path);
        var ours = Png.Decode(bytes);
        var skia = SkiaDecodeStraight(bytes);
        var diff = FirstDifference(ours, skia);
        if (diff is not null) mismatches.Add($"{Rel(path)}: {diff}");
    }
    if (mismatches.Count > 0)
        throw new Exception($"{mismatches.Count} differ: " + string.Join("; ", mismatches.Take(8)));
});

Run("Decode: the fixtures cover colour types 0/2/3/4/6, depths 1-16, Adam7, tRNS, gAMA, iCCP and all five filters", () =>
{
    var seen = new HashSet<string>();
    var filters = new HashSet<int>();
    foreach (var path in fixtures)
    {
        var info = Inspect(File.ReadAllBytes(path));
        seen.Add($"ct{info.ColorType}/{info.BitDepth}");
        if (info.Interlaced) seen.Add("adam7");
        foreach (var c in info.Chunks) seen.Add(c);
        filters.UnionWith(info.Filters);
    }
    var want = new[]
    {
        "ct0/1", "ct0/2", "ct0/4", "ct0/8", "ct0/16", "ct2/8", "ct2/16", "ct3/1", "ct3/2", "ct3/4", "ct3/8",
        "ct4/8", "ct4/16", "ct6/8", "ct6/16", "adam7", "tRNS", "gAMA", "iCCP", "PLTE",
    };
    var missing = want.Where(w => !seen.Contains(w)).ToList();
    if (missing.Count > 0) throw new Exception("not covered: " + string.Join(", ", missing));
    for (int f = 0; f <= 4; f++)
        if (!filters.Contains(f)) throw new Exception($"no fixture uses filter {f}");
});

Run("Round trip: encode then decode gives back every corpus image exactly", () =>
{
    foreach (var path in corpus)
    {
        var image = Png.Decode(File.ReadAllBytes(path));
        var again = Png.Decode(Png.Encode(image));
        var diff = FirstDifference(again, image);
        if (diff is not null) throw new Exception($"{Rel(path)}: {diff}");
    }
});

Run("Round trip: Skia reads what we encode, pixel for pixel", () =>
{
    foreach (var path in fixtures)
    {
        var image = Png.Decode(File.ReadAllBytes(path));
        var diff = FirstDifference(image, SkiaDecodeStraight(Png.Encode(image)));
        if (diff is not null) throw new Exception($"{Rel(path)}: {diff}");
    }
});

Run("Round trip: random translucent images of awkward sizes", () =>
{
    var rng = new Random(20);
    foreach (var (w, h) in new[] { (1, 1), (1, 9), (9, 1), (3, 5), (17, 13), (64, 64), (257, 3) })
    {
        var image = new RgbaImage(w, h);
        rng.NextBytes(image.Pixels);
        var diff = FirstDifference(Png.Decode(Png.Encode(image)), image);
        if (diff is not null) throw new Exception($"{w}x{h}: {diff}");
    }
});

Run("Encode: deterministic — the same pixels give the same bytes, whatever memory or stride holds them", () =>
{
    var image = Png.Decode(File.ReadAllBytes(corpus[0]));
    var first = Png.Encode(image);
    if (!first.AsSpan().SequenceEqual(Png.Encode(image))) throw new Exception("two encodes of one image differ");

    // The same pixels as a view into a wider, padded canvas.
    var canvas = new RgbaImage(image.Width + 7, image.Height + 3);
    var view = canvas.View(5, 2, image.Width, image.Height);
    for (int y = 0; y < image.Height; y++) image.Row(y).CopyTo(view.Row(y));
    if (!first.AsSpan().SequenceEqual(Png.Encode(view))) throw new Exception("a strided view encodes differently");

    // The same pixels as BGRA, through the span overload a raster backend uses.
    var bgra = image.Pixels.ToArray();
    for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
    if (!first.AsSpan().SequenceEqual(Png.Encode(bgra, image.Width, image.Height, image.Width * 4, ChannelOrder.Bgra, AlphaMode.Straight)))
        throw new Exception("BGRA input encodes differently");
});

Run("Encode: a premultiplied image is written straight (c * 255 / a, rounded)", () =>
{
    var pm = new RgbaImage(4, 1, AlphaMode.Premultiplied);
    byte[] px = [100, 50, 0, 255, /**/ 64, 32, 1, 128, /**/ 0, 0, 0, 0, /**/ 3, 2, 1, 4];
    px.CopyTo(pm.Pixels);
    var back = Png.Decode(Png.Encode(pm));
    byte[] want = [100, 50, 0, 255, /**/ 128, 64, 2, 128, /**/ 0, 0, 0, 0, /**/ 191, 128, 64, 4];
    if (!back.Pixels.SequenceEqual(want))
        throw new Exception($"got {string.Join(",", back.Pixels.ToArray())}");
});

Run("Encode: a Skia surface's pixels wrap without copying and encode as Skia's own PNG decodes", () =>
{
    var info = new SKImageInfo(40, 30, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    surface.Canvas.Clear(new SKColor(250, 250, 250));
    using (var paint = new SKPaint { Color = new SKColor(30, 120, 220, 255), IsAntialias = true })
        surface.Canvas.DrawCircle(20, 15, 11, paint);
    using var pixmap = surface.PeekPixels();
    var wrapped = RgbaImage.Wrap(pixmap.GetPixels(), pixmap.Width, pixmap.Height, pixmap.RowBytes, AlphaMode.Premultiplied);

    using var snapshot = surface.Snapshot();
    using var skiaPng = snapshot.Encode(SKEncodedImageFormat.Png, 100);
    var diff = FirstDifference(Png.Decode(Png.Encode(wrapped)), Png.Decode(skiaPng.ToArray()));
    if (diff is not null) throw new Exception(diff);
});

Run("Decode: broken files fail with PngException, not garbage", () =>
{
    var good = File.ReadAllBytes(fixtures.First(f => f.EndsWith("rgba8.png")));
    var cases = new Dictionary<string, byte[]>
    {
        ["no signature"] = good[1..],
        ["truncated mid-IDAT"] = good[..(good.Length / 2)],
        ["no IEND"] = good[..^12],
        ["bad IDAT CRC"] = Corrupt(good, "IDAT", crc: true),
        ["bad filter byte"] = WithFilterByte(good, 9),
        ["empty"] = [],
    };
    foreach (var (name, bytes) in cases)
    {
        try
        {
            Png.Decode(bytes);
            throw new Exception($"{name}: decoded without complaint");
        }
        catch (PngException) { }
    }
});

Run("RgbaImage: View shares memory, Clone does not, and stride is honoured", () =>
{
    var image = new RgbaImage(10, 6);
    var view = image.View(2, 1, 3, 4);
    view.Row(0)[0] = 77;
    if (image.Pixels[1 * image.Stride + 2 * 4] != 77) throw new Exception("a write through a view did not reach the image");
    var clone = view.Clone();
    if (clone.Stride != 12 || clone.Row(0)[0] != 77) throw new Exception("clone is not a packed copy");
    clone.Row(0)[0] = 1;
    if (image.Pixels[1 * image.Stride + 2 * 4] != 77) throw new Exception("a clone shares memory");
});

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

// ── Helpers ──────────────────────────────────────────────────────────────────

static RgbaImage SkiaDecodeStraight(byte[] bytes)
{
    using var codec = SKCodec.Create(new SKMemoryStream(bytes)) ?? throw new Exception("Skia cannot decode it");
    var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    var image = new RgbaImage(info.Width, info.Height);
    using var bitmap = new SKBitmap();
    var pixels = new byte[info.BytesSize];
    var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
    try
    {
        var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
        if (result != SKCodecResult.Success) throw new Exception($"Skia: {result}");
    }
    finally
    {
        handle.Free();
    }
    pixels.CopyTo(image.Pixels);
    return image;
}

static string? FirstDifference(RgbaImage actual, RgbaImage expected)
{
    if (actual.Width != expected.Width || actual.Height != expected.Height)
        return $"size {actual.Width}x{actual.Height}, expected {expected.Width}x{expected.Height}";
    for (int y = 0; y < actual.Height; y++)
    {
        var a = actual.Row(y);
        var e = expected.Row(y);
        int i = a.CommonPrefixLength(e);
        if (i < a.Length)
        {
            int x = i / 4, p = x * 4;
            return $"pixel ({x},{y}) is {a[p]},{a[p + 1]},{a[p + 2]},{a[p + 3]}, expected {e[p]},{e[p + 1]},{e[p + 2]},{e[p + 3]}";
        }
    }
    return null;
}

static (int ColorType, int BitDepth, bool Interlaced, HashSet<string> Chunks, HashSet<int> Filters) Inspect(byte[] png)
{
    int pos = 8, ct = 0, depth = 0, w = 0, h = 0;
    bool interlaced = false;
    var chunks = new HashSet<string>();
    using var idat = new MemoryStream();
    while (pos < png.Length)
    {
        int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
        var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
        chunks.Add(type);
        if (type == "IHDR")
        {
            w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos + 8));
            h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos + 12));
            depth = png[pos + 16]; ct = png[pos + 17]; interlaced = png[pos + 20] == 1;
        }
        if (type == "IDAT") idat.Write(png, pos + 8, len);
        pos += 12 + len;
    }
    var filters = new HashSet<int>();
    if (!interlaced)
    {
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        int channels = ct switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int rowBytes = (w * channels * depth + 7) / 8;
        var bytes = raw.GetBuffer();
        for (int y = 0; y < h; y++) filters.Add(bytes[y * (rowBytes + 1)]);
    }
    return (ct, depth, interlaced, chunks, filters);
}

static byte[] Corrupt(byte[] png, string chunkType, bool crc)
{
    var copy = (byte[])png.Clone();
    int pos = 8;
    while (pos < copy.Length)
    {
        int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(copy.AsSpan(pos));
        if (System.Text.Encoding.ASCII.GetString(copy, pos + 4, 4) == chunkType)
        {
            copy[crc ? pos + 8 + len : pos + 8] ^= 0xFF;
            return copy;
        }
        pos += 12 + len;
    }
    throw new Exception($"no {chunkType}");
}

static byte[] WithFilterByte(byte[] png, byte filter)
{
    // Re-encode a tiny image by hand with an undefined filter byte on row 0.
    var raw = new byte[] { filter, 1, 2, 3, 4 };
    using var z = new MemoryStream();
    using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw);
    var idat = z.ToArray();
    using var ms = new MemoryStream();
    ms.Write(png, 0, 8 + 25); // signature + IHDR, then patch the size to 1x1
    var bytes = ms.ToArray();
    bytes[16] = 0; bytes[17] = 0; bytes[18] = 0; bytes[19] = 1;
    bytes[20] = 0; bytes[21] = 0; bytes[22] = 0; bytes[23] = 1;
    var ihdrCrc = System.IO.Hashing.Crc32.HashToUInt32(bytes.AsSpan(12, 17));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29), ihdrCrc);
    var output = new MemoryStream();
    output.Write(bytes);
    WriteChunk(output, "IDAT", idat);
    WriteChunk(output, "IEND", []);
    return output.ToArray();
}

static void WriteChunk(Stream s, string type, byte[] data)
{
    var head = new byte[8];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
    System.Text.Encoding.ASCII.GetBytes(type, head.AsSpan(4));
    s.Write(head);
    s.Write(data);
    var crc = new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32([.. head.AsSpan(4).ToArray(), .. data]));
    s.Write(crc);
}

string Rel(string path) => Path.GetRelativePath(root, path);

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
    return dir?.FullName ?? throw new Exception("cannot find the repo root (Directory.Build.props) above " + AppContext.BaseDirectory);
}
