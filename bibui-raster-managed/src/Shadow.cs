namespace Bibui.Raster.Managed;

/// <summary>
/// A blurred shape small enough to stretch: a nine-patch. The four
/// corners are copied, the row <see cref="CenterY"/> and the column
/// <see cref="CenterX"/> are repeated along the sides, and the middle is solid.
/// Immutable once built, so the <see cref="ShadowCache"/> can hand one to every
/// draw of the same shadow.
/// </summary>
internal sealed class NinePatch
{
    internal NinePatch(byte[] mask, int width, int height, int centerX, int centerY)
    {
        Mask = mask;
        Width = width;
        Height = height;
        CenterX = centerX;
        CenterY = centerY;
    }

    /// <summary>A8 coverage, <see cref="Width"/> per row.</summary>
    public byte[] Mask { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>The column that is stretched: columns before it are the left side, after it the right.</summary>
    public int CenterX { get; }

    /// <summary>The row that is stretched.</summary>
    public int CenterY { get; }

    public byte this[int x, int y] => Mask[y * Width + x];
}

/// <summary>What a blurred nine-patch was made from. Two equal keys give byte-identical patches.</summary>
internal readonly record struct ShadowKey(byte Kind, float Sigma, float W, float H, float Rx, float Ry, byte Aa);

/// <summary>
/// The shadow cache: blurred nine-patches by the shape and sigma that
/// made them. A shadow is a pure function of its
/// key, so evicting one never changes a pixel — it only costs a re-blur. Full
/// at <see cref="Capacity"/>, it starts over.
/// </summary>
internal sealed class ShadowCache
{
    private readonly Dictionary<ShadowKey, NinePatch> _patches = [];

    public int Capacity { get; init; } = 256;
    public int Count => _patches.Count;

    /// <summary>Draws that found their patch here.</summary>
    public long Hits { get; private set; }

    /// <summary>Draws that had to blur.</summary>
    public long Misses { get; private set; }

    public void Clear() => _patches.Clear();

    internal bool TryGet(in ShadowKey key, out NinePatch patch)
    {
        if (_patches.TryGetValue(key, out patch!)) { Hits++; return true; }
        Misses++;
        return false;
    }

    internal void Add(in ShadowKey key, NinePatch patch)
    {
        if (_patches.Count >= Capacity) _patches.Clear();
        _patches[key] = patch;
    }
}

/// <summary>
/// The analytic blur of an axis-aligned rect: a one-dimensional profile from a
/// cubic approximation of the Gaussian integral, applied across x and down y,
/// the two multiplied. This — not the box blur — is what a blurred rect gets.
///
/// <para>This reproduces Skia 2.88's <c>SkBlurMask::BlurRect</c> exactly, cubic
/// included: the fixture baselines are drawn by the Skia backend, so the managed backend matches its pixels bit for bit to be verified against them.</para>
/// </summary>
internal static class RectBlur
{
    /// <summary>⌈6σ⌉: the profile's length; the mask reaches half of it past each edge.</summary>
    public static int ProfileSize(float sigma) => (int)MathF.Ceiling(6 * sigma);

    /// <summary>How far past the rect the blurred mask reaches: ⌈6σ⌉/2.</summary>
    public static int Pad(float sigma) => ProfileSize(sigma) / 2;

    /// <summary>The blurred mask's pixel bounds: each edge outset by <see cref="Pad"/>, rounded.</summary>
    public static IRect Bounds(Rect r, float sigma)
    {
        int pad = Pad(sigma);
        return new IRect(RoundToInt(r.Left - pad), RoundToInt(r.Top - pad), RoundToInt(r.Right + pad), RoundToInt(r.Bottom + pad));
    }

    internal static int RoundToInt(float x) => (int)MathF.Floor(x + 0.5f);

    /// <summary>The blurred mask of <paramref name="r"/>, over <see cref="Bounds"/>.</summary>
    public static byte[] Mask(Rect r, float sigma, out IRect bounds)
    {
        bounds = Bounds(r, sigma);
        int w = bounds.Width, h = bounds.Height;
        int size = ProfileSize(sigma);
        var profile = Profile(sigma);
        Span<byte> hs = w <= 1024 ? stackalloc byte[w] : new byte[w];
        Span<byte> vs = h <= 1024 ? stackalloc byte[h] : new byte[h];
        Scanline(hs, profile, size, sigma);
        Scanline(vs, profile, size, sigma);
        var mask = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            uint v = vs[y];
            var row = mask.AsSpan(y * w, w);
            for (int x = 0; x < w; x++) row[x] = (byte)PixelMath.MulDiv255Round(hs[x], v);
        }
        return mask;
    }

    /// <summary>Approximates ∫ of the unit Gaussian from −∞ to x with a cubic.</summary>
    internal static float GaussianIntegral(float x)
    {
        if (x > 1.5f) return 0.0f;
        if (x < -1.5f) return 1.0f;
        float x2 = x * x;
        float x3 = x2 * x;
        if (x > 0.5f) return 0.5625f - (x3 / 6.0f - 3.0f * x2 * 0.25f + 1.125f * x);
        if (x > -0.5f) return 0.5f - (0.75f * x - x3 / 3.0f);
        return 0.4375f + (-x3 / 6.0f - 3.0f * x2 * 0.25f - 1.125f * x);
    }

    /// <summary><c>ComputeBlurProfile</c>: the fall-off from inside the edge to outside.</summary>
    internal static byte[] Profile(float sigma)
    {
        int size = ProfileSize(sigma);
        var profile = new byte[Math.Max(1, size)];
        int center = size >> 1;
        float invr = 1f / (2 * sigma);
        profile[0] = 255;
        for (int x = 1; x < size; x++)
        {
            float scaled = (center - x - .5f) * invr;
            float gi = GaussianIntegral(scaled);
            profile[x] = (byte)(255 - (byte)(255f * gi));
        }
        return profile;
    }

    /// <summary><c>ComputeBlurredScanline</c>: one axis of the mask.</summary>
    private static void Scanline(Span<byte> pixels, byte[] profile, int profileSize, float sigma)
    {
        int width = pixels.Length;
        int sw = width - profileSize;
        // The nearest odd number below the profile size is the centre of the (doubled) profile.
        int center = (profileSize & ~1) - 1;
        int w = sw - center;
        for (int x = 0; x < width; x++)
        {
            if (sw >= 0 && profileSize <= sw)
            {
                int dx = Math.Abs(((x << 1) + 1) - width) - w;
                int ox = dx >> 1;
                if (ox < 0) ox = 0;
                pixels[x] = profile[Math.Min(ox, profile.Length - 1)];
            }
            else
            {
                float span = (float)sw / (2 * sigma);
                float giX = 1.5f - (x + .5f) / (2 * sigma);
                pixels[x] = (byte)(int)MathF.Max(0f, 255 * (GaussianIntegral(giX) - GaussianIntegral(giX + span)));
            }
        }
    }
}
