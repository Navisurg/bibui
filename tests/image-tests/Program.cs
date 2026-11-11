// JPEG and WebP decode through the system's libraries, and Images.Decode in
// front of every format, against known pixels and against SkiaSharp.
//
//   dotnet run --project tests/image-tests -c Release
//
// The fixtures are flat blocks of colour (tests/image-tests/fixtures/generate.py):
// a lossy decode is checked at block centres, within a tolerance.

using System.Buffers.Binary;
using Bibui.Core.Imaging;
using Bibui.Imaging;
using SkiaSharp;

var root = FindRepoRoot();
var fixtures = Path.Combine(root, "tests", "image-tests", "fixtures");
byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(fixtures, name));

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

// What generate.py paints: red top left, green top right, blue bottom left,
// yellow bottom right, on 24x16.
(byte R, byte G, byte B) red = (230, 30, 30), green = (30, 200, 60), blue = (40, 60, 220), yellow = (240, 220, 40);

// Each quadrant's centre is within `tolerance` of its colour, and the image is opaque.
void Quadrants(RgbaImage image, int tolerance, bool grey = false)
{
    if (image.Width != 24 || image.Height != 16) throw new Exception($"size {image.Width}x{image.Height}, expected 24x16");
    foreach (var (x, y, c, where) in new[] { (6, 4, red, "top left"), (18, 4, green, "top right"), (6, 12, blue, "bottom left"), (18, 12, yellow, "bottom right") })
    {
        var want = grey ? Grey(c) : c;
        var p = image.Row(y).Slice(x * 4, 4);
        int off = Math.Max(Math.Abs(p[0] - want.R), Math.Max(Math.Abs(p[1] - want.G), Math.Abs(p[2] - want.B)));
        if (off > tolerance || p[3] != 255)
            throw new Exception($"{where} ({x},{y}) is {p[0]},{p[1]},{p[2]},{p[3]}, expected about {want.R},{want.G},{want.B},255 (±{tolerance})");
    }
}

static (byte R, byte G, byte B) Grey((byte R, byte G, byte B) c)
{
    var l = (byte)Math.Round(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
    return (l, l, l);
}

Console.WriteLine($"libjpeg-turbo: {(Jpeg.IsAvailable ? "yes" : "NO")}, libwebp: {(WebP.IsAvailable ? "yes" : "NO")}");

Run("The system has libjpeg-turbo and libwebp (every desktop Linux does; CI installs them)", () =>
{
    if (!Jpeg.IsAvailable) throw new Exception("libturbojpeg.so.0 did not load");
    if (!WebP.IsAvailable) throw new Exception("libwebp.so.7 did not load");
});

// ── JPEG ─────────────────────────────────────────────────────────────────────

Run("JPEG: baseline 4:4:4 decodes to its size and colours, opaque", () => Quadrants(Jpeg.Decode(Fixture("quadrants-444.jpg")), 6));

Run("JPEG: progressive 4:2:0 decodes", () => Quadrants(Jpeg.Decode(Fixture("quadrants-420-progressive.jpg")), 20));

Run("JPEG: grey comes back as grey RGB", () => Quadrants(Jpeg.Decode(Fixture("quadrants-grey.jpg")), 4, grey: true));

Run("JPEG: Adobe CMYK (inverted, as Photoshop writes it) comes back as its RGB", () => Quadrants(Jpeg.Decode(Fixture("quadrants-cmyk.jpg")), 6));

Run("JPEG: every EXIF orientation (1-8) comes back upright, at the upright size", () =>
{
    for (int o = 1; o <= 8; o++)
    {
        try { Quadrants(Jpeg.Decode(Fixture($"exif-orientation-{o}.jpg")), 6); }
        catch (Exception e) { throw new Exception($"orientation {o}: {e.Message}"); }
    }
});

Run("JPEG: a little-endian (II) EXIF orientation is read too", () =>
{
    // exif-orientation-3.jpg's pixels are stored upside down; its own APP1 is
    // big-endian (MM), as Pillow writes it. Swap that segment for a
    // little-endian one, as many Android phones write it, saying the same.
    var file = Fixture("exif-orientation-3.jpg");
    var (at, length) = Segment(file, 0xE1);
    byte[] tiff =
    [
        (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, // header, IFD0 at 8
        1, 0,                                    // one entry
        0x12, 0x01, 3, 0, 1, 0, 0, 0, 3, 0, 0, 0, // Orientation, SHORT, 1, value 3
        0, 0, 0, 0,                              // no next IFD
    ];
    byte[] app1 = [0xFF, 0xE1, 0, (byte)(2 + 6 + tiff.Length), .. "Exif\0\0"u8, .. tiff];
    if (file[at + 10] != 'M') throw new Exception("the fixture's own EXIF is not big-endian any more");
    byte[] bigEndian = [.. file.AsSpan(0, at), .. app1, .. file.AsSpan(at + 2 + length)];
    Quadrants(Jpeg.Decode(bigEndian), 6);

    // And without the segment at all, the same pixels are shown as stored.
    byte[] none = [.. file.AsSpan(0, at), .. file.AsSpan(at + 2 + length)];
    var stored = Jpeg.Decode(none);
    var p = stored.Row(4).Slice(6 * 4, 4);
    if (Math.Abs(p[0] - yellow.R) > 6 || Math.Abs(p[2] - yellow.B) > 6) throw new Exception($"stored top left is {p[0]},{p[1]},{p[2]}, expected yellow");
});

Run("JPEG: every fixture matches SkiaSharp's decode within 2 levels", () =>
{
    // Skia does not apply the EXIF orientation, so orientation 1 stands for those.
    foreach (var name in new[] { "quadrants-444.jpg", "quadrants-420-progressive.jpg", "quadrants-grey.jpg", "quadrants-cmyk.jpg", "exif-orientation-1.jpg" })
    {
        var bytes = Fixture(name);
        int diff = MaxDifference(Jpeg.Decode(bytes), SkiaDecodeStraight(bytes));
        if (diff > 2) throw new Exception($"{name}: differs from Skia by {diff}");
    }
});

Run("JPEG: corrupt input throws JpegException, never crashes", () =>
{
    var good = Fixture("quadrants-444.jpg");
    var cases = new (string, byte[])[]
    {
        ("empty", []),
        ("just the start-of-image marker", [0xFF, 0xD8, 0xFF]),
        ("cut inside the header", good[..120]),
        ("garbage after the start-of-image marker", [0xFF, 0xD8, .. Enumerable.Range(0, 400).Select(i => (byte)(i * 37 + 11))]),
        ("a PNG", Png.Encode(new RgbaImage(2, 2))),
        // A header libjpeg reads, then more scans than it will decode (TJFLAG_LIMITSCANS):
        // the progressive fixture's last table and scan, over and over.
        ("a progressive JPEG of more than 500 scans", ManyScans(Fixture("quadrants-420-progressive.jpg"), 600)),
    };
    foreach (var (what, bytes) in cases)
    {
        try { Jpeg.Decode(bytes); throw new Exception($"{what}: decoded"); }
        catch (JpegException) { }
    }
});

Run("JPEG: a file cut short in its scan decodes as far as it goes, at full size", () =>
{
    // Cut a third of the way into the entropy-coded data, after the scan header.
    var good = Fixture("quadrants-444.jpg");
    var (sos, sosLength) = Segment(good, 0xDA);
    int scan = sos + 2 + sosLength;
    var image = Jpeg.Decode(good[..(scan + (good.Length - scan) / 3)]);
    if (image.Width != 24 || image.Height != 16) throw new Exception($"size {image.Width}x{image.Height}");
    var p = image.Row(4).Slice(6 * 4, 4);
    if (Math.Abs(p[0] - red.R) > 6) throw new Exception($"the top left, before the cut, is {p[0]},{p[1]},{p[2]}");
});

// ── WebP ─────────────────────────────────────────────────────────────────────

Run("WebP: lossy decodes to its size and colours, opaque", () => Quadrants(WebP.Decode(Fixture("quadrants-lossy.webp")), 20));

Run("WebP: lossy with alpha keeps its alpha, straight", () =>
{
    var image = WebP.Decode(Fixture("quadrants-lossy-alpha.webp"));
    if (image.Alpha != AlphaMode.Straight) throw new Exception("not straight alpha");
    var left = image.Row(4).Slice(6 * 4, 4);
    var right = image.Row(4).Slice(18 * 4, 4);
    if (left[3] != 255 || right[3] != 96) throw new Exception($"alpha {left[3]} and {right[3]}, expected 255 and 96");
    // Straight: the translucent half's colour is still green, not darkened by its alpha.
    if (Math.Abs(right[1] - green.G) > 20) throw new Exception($"translucent green is {right[0]},{right[1]},{right[2]}");
});

Run("WebP: lossless with alpha is exact, every pixel", () =>
{
    var image = WebP.Decode(Fixture("exact-lossless.webp"));
    if (image.Width != 7 || image.Height != 5) throw new Exception($"size {image.Width}x{image.Height}");
    for (int y = 0; y < 5; y++)
        for (int x = 0; x < 7; x++)
        {
            byte[] want = [(byte)(x * 40), (byte)(y * 60), (byte)((x * 7 + y * 13) % 256), (byte)((x + y * 7) * 255 / 34)];
            var p = image.Row(y).Slice(x * 4, 4);
            if (!p.SequenceEqual(want)) throw new Exception($"({x},{y}) is {p[0]},{p[1]},{p[2]},{p[3]}, expected {string.Join(",", want)}");
        }
});

Run("WebP: every still fixture matches SkiaSharp's decode within 2 levels", () =>
{
    foreach (var name in new[] { "quadrants-lossy.webp", "quadrants-lossy-alpha.webp", "exact-lossless.webp" })
    {
        var bytes = Fixture(name);
        int diff = MaxDifference(WebP.Decode(bytes), SkiaDecodeStraight(bytes));
        if (diff > 2) throw new Exception($"{name}: differs from Skia by {diff}");
    }
});

Run("WebP: corrupt, truncated and animated input throws WebPException, never crashes", () =>
{
    var good = Fixture("quadrants-lossy.webp");
    var lossless = Fixture("exact-lossless.webp");
    var scrambled = (byte[])good.Clone();
    for (int i = 30; i < scrambled.Length; i++) scrambled[i] ^= 0x5A;
    var cases = new (string, byte[])[]
    {
        ("empty", []),
        ("just the RIFF header", good[..12]),
        ("cut in half", good[..(good.Length / 2)]),
        ("lossless cut short", lossless[..(lossless.Length - 10)]),
        ("scrambled after the header", scrambled),
        ("animated", Fixture("animated.webp")),
    };
    foreach (var (what, bytes) in cases)
    {
        try { WebP.Decode(bytes); throw new Exception($"{what}: decoded"); }
        catch (WebPException e) when (what != "animated" || e.Message.Contains("animated")) { }
    }
});

// ── Images: the format from the first bytes ──────────────────────────────────

Run("Images.FormatOf tells PNG, JPEG and WebP apart by their first bytes", () =>
{
    var cases = new (byte[], ImageFormat)[]
    {
        (Png.Encode(new RgbaImage(1, 1)), ImageFormat.Png),
        (Fixture("quadrants-444.jpg"), ImageFormat.Jpeg),
        (Fixture("quadrants-lossy.webp"), ImageFormat.WebP),
        ("GIF89a"u8.ToArray(), ImageFormat.Unknown),
        ("RIFF\0\0\0\0WAVE"u8.ToArray(), ImageFormat.Unknown),
        ([], ImageFormat.Unknown),
    };
    foreach (var (bytes, want) in cases)
        if (Images.FormatOf(bytes) != want) throw new Exception($"{want}: read as {Images.FormatOf(bytes)}");
    if (!Images.CanDecode(ImageFormat.Png) || Images.CanDecode(ImageFormat.Unknown)) throw new Exception("CanDecode is wrong");
});

Run("Images.Decode decodes each format as that format's own Decode does", () =>
{
    var png = new RgbaImage(3, 2);
    png.Pixels.Fill(200);
    if (Images.Decode(Png.Encode(png)) is not { Width: 3, Height: 2 } fromPng || fromPng.Pixels[0] != 200) throw new Exception("PNG");
    Quadrants(Images.Decode(Fixture("exif-orientation-6.jpg")) ?? throw new Exception("JPEG: null"), 6);
    if (MaxDifference(Images.Decode(Fixture("exact-lossless.webp")) ?? throw new Exception("WebP: null"), WebP.Decode(Fixture("exact-lossless.webp"))) != 0)
        throw new Exception("WebP differs");
    if (Images.DecodeFile(Path.Combine(fixtures, "quadrants-444.jpg")) is not { Width: 24 }) throw new Exception("DecodeFile");
});

Run("Images.Decode answers null for what it cannot decode, and never throws", () =>
{
    var jpeg = Fixture("quadrants-444.jpg");
    var cases = new (string, byte[])[]
    {
        ("empty", []),
        ("unknown", "GIF89a\x01\0\x01\0"u8.ToArray()),
        ("a corrupt PNG", Png.Encode(new RgbaImage(4, 4))[..40]),
        ("a corrupt JPEG", [0xFF, 0xD8, 0xFF, .. Enumerable.Repeat((byte)0x42, 300)]),
        ("a JPEG cut inside its header", jpeg[..100]),
        ("a corrupt WebP", [.. "RIFF"u8, 100, 0, 0, 0, .. "WEBPVP8 "u8, .. Enumerable.Repeat((byte)7, 92)]),
        ("an animated WebP", Fixture("animated.webp")),
    };
    foreach (var (what, bytes) in cases)
        if (Images.Decode(bytes) is not null) throw new Exception($"{what}: decoded");
    if (Images.DecodeFile(Path.Combine(fixtures, "no-such-file.jpg")) is not null) throw new Exception("a missing file decoded");
    if (Images.DecodeFile(fixtures) is not null) throw new Exception("a directory decoded");
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

// The offset and length field of a JPEG's first marker segment of a kind, before
// the scan (the length counts itself, not the marker).
static (int At, int Length) Segment(byte[] jpeg, byte marker)
{
    for (int i = 2; i + 4 <= jpeg.Length;)
    {
        int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(i + 2));
        if (jpeg[i + 1] == marker) return (i, length);
        if (jpeg[i + 1] == 0xDA) break;
        i += 2 + length;
    }
    throw new Exception($"no marker 0x{marker:X2}");
}

// The largest difference in any channel of any pixel.
// The JPEG with its last Huffman table and scan (from the last DHT to the end of
// image) repeated until it has `scans` more of them.
static byte[] ManyScans(byte[] jpeg, int scans)
{
    int last = jpeg.AsSpan().LastIndexOf([(byte)0xFF, (byte)0xC4]);
    var tail = jpeg.AsSpan(last, jpeg.Length - 2 - last);
    var many = new List<byte>(jpeg.AsSpan(0, jpeg.Length - 2).ToArray());
    for (int i = 0; i < scans; i++) many.AddRange(tail);
    many.AddRange([0xFF, 0xD9]);
    return [.. many];
}

static int MaxDifference(RgbaImage actual, RgbaImage expected)
{
    if (actual.Width != expected.Width || actual.Height != expected.Height)
        throw new Exception($"size {actual.Width}x{actual.Height}, expected {expected.Width}x{expected.Height}");
    int max = 0;
    for (int y = 0; y < actual.Height; y++)
    {
        var a = actual.Row(y);
        var e = expected.Row(y);
        for (int i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - e[i]));
    }
    return max;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
    return dir?.FullName ?? throw new Exception("cannot find the repo root (Directory.Build.props) above " + AppContext.BaseDirectory);
}
