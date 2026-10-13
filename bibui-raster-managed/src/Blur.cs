using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed;

/// <summary>
/// The Gaussian blur of a coverage mask, for <see cref="Paint.MaskBlurSigma"/>
/// (soft shadows). Two kernels, chosen by sigma, in integer arithmetic:
///
/// <list type="bullet">
/// <item><b>σ &lt; 2</b>: the discrete Gaussian of Lindeberg's "Scale-Space for
/// Discrete Signals" (modified Bessel functions), cut where a weight falls to
/// 0.01 (radius ≤ 4), as 0.16 fixed-point weights. Vertical pass, then
/// horizontal, each rounded to 8 bits.</item>
/// <item><b>σ ≥ 2</b>: three box filters, two of width w and one of w or w + 1
/// (w = ⌊σ·3·√(2π)/4 + ½⌋, the SVG spec's window), summed exactly in 32 bits and
/// scaled by round(2³²/divisor). Horizontal pass, then vertical.</item>
/// <item><b>3σ ≤ 1</b>: no blur at all (<see cref="HasNoBlur"/>): the shape is
/// drawn as if it had no blur.</item>
/// </list>
///
/// <para>This reproduces Skia 2.88's mask blur exactly (<c>SkMaskBlurFilter</c>:
/// <c>SkGaussFilter</c>'s radius and 0.16 weights, the box plan, the 8-bit
/// rounding of each pass, and <c>SkBlurMaskFilterImpl</c>'s sigma cap): the
/// fixture baselines are drawn by the Skia backend, so the managed backend's
/// shadows must match its masks byte for byte to be verified against them.</para>
///
/// <para>Also holds the shadow cache: the blurred nine-patches the canvas
/// stretches for rect and rrect shadows (<see cref="Canvas"/>), so a card's
/// shadow is blurred once, not every frame.</para>
/// </summary>
public sealed class GaussianMaskBlur : IMaskBlur
{
    private const double SmallSigma = 2.0;
    private const float MaxSigma = 532f;   // the largest sigma blurred; larger ones blur as this

    private uint[] _sums = [];

    /// <summary>Blurred nine-patches by shape and sigma.</summary>
    internal ShadowCache Cache { get; } = new();

    /// <summary>True when the sigma is too small to blur at all: 3σ ≤ 1.</summary>
    internal static bool HasNoBlur(float sigma) => !(3.0 * Clamp(sigma) > 1.0);

    private static double Clamp(float sigma) => Math.Min(sigma, MaxSigma);

    public int Margin(float sigma)
    {
        if (HasNoBlur(sigma)) return 0;
        double s = Clamp(sigma);
        return s < SmallSigma ? GaussRadius(s) : BoxBorder(BoxWindow(s));
    }

    public void Blur(CoverageMask mask, float sigma) => BlurA8(mask.Data, mask.Bounds.Width, mask.Bounds.Height, sigma);

    /// <summary>
    /// Blurs a tightly packed A8 mask in place. The shape must be at least
    /// <see cref="Margin"/> px clear of every edge, which is where the blur spreads.
    /// </summary>
    public void BlurA8(Span<byte> data, int width, int height, float sigma)
    {
        if (HasNoBlur(sigma) || width <= 0 || height <= 0) return;
        double s = Clamp(sigma);
        if (s < SmallSigma)
        {
            SmallBlur(data, width, height, GaussFactors(s));
            return;
        }
        int w = BoxWindow(s);
        if (w > 1) BoxBlurXY(data, width, height, w);
    }

    // ── σ < 2: the discrete Gaussian ──────────────────────────────────────

    /// <summary>The small-sigma kernel's radius (1–4): taps out to where a weight falls below 0.01.</summary>
    internal static int GaussRadius(double sigma)
    {
        Span<double> g = stackalloc double[8];
        return Gauss(sigma, g) - 1;
    }

    /// <summary>
    /// <c>SkGaussFilter</c>: weights for distances 0..radius, normalised so the
    /// whole (two-sided) kernel sums to 1.
    /// </summary>
    internal static double[] GaussWeights(double sigma)
    {
        Span<double> g = stackalloc double[8];
        return g[..Gauss(sigma, g)].ToArray();
    }

    /// <summary>The weights into <paramref name="gauss"/> (8 long); returns how many.</summary>
    private static int Gauss(double sigma, Span<double> gauss)
    {
        double var = sigma * sigma;
        static double BesselI0(double t)
        {
            double q = t * t / 4.0, sum = 1.0, factor = 1.0;
            for (int k = 1; factor > 1.0 / 1000000.0; k++) { factor *= q / (k * k); sum += factor; }
            return sum;
        }
        static double BesselI1(double t)
        {
            double q = t * t / 4.0, sum = t / 2.0, factor = sum;
            for (int k = 1; factor > 1.0 / 1000000.0; k++) { factor *= q / (k * (k + 1)); sum += factor; }
            return sum;
        }
        // gauss(n; var) = I_n(var) / e^var, by the recurrence for I_n.
        Span<double> b = stackalloc double[8];
        double d = Math.Exp(var);
        b[0] = BesselI0(var);
        b[1] = BesselI1(var);
        gauss[0] = b[0] / d;
        gauss[1] = b[1] / d;
        int n = 1;
        while (gauss[n] > 0.01 && n < 5)
        {
            b[n + 1] = -(2 * n / var) * b[n] + b[n - 1];
            gauss[n + 1] = b[n + 1] / d;
            n++;
        }
        // Normalise, adding small to large, and give the slop to the centre.
        double sum = 0;
        for (int i = n - 1; i >= 1; i--) sum += 2 * gauss[i];
        sum += gauss[0];
        for (int i = 0; i < n; i++) gauss[i] /= sum;
        sum = 0;
        for (int i = n - 1; i >= 1; i--) sum += 2 * gauss[i];
        gauss[0] = 1 - sum;
        return n;
    }

    /// <summary>The weights in 0.16 fixed point.</summary>
    internal static ushort[] GaussFactors(double sigma)
    {
        var g = GaussWeights(sigma);
        var f = new ushort[g.Length];
        for (int i = 0; i < g.Length; i++) f[i] = (ushort)Math.Round(g[i] * (1 << 16), MidpointRounding.AwayFromZero);
        return f;
    }

    // ── σ ≥ 2: three box filters ──────────────────────────────────────────

    /// <summary>The box width w for a sigma: ⌊σ·3·√(2π)/4 + ½⌋, at least 1.</summary>
    internal static int BoxWindow(double sigma)
        => Math.Max(1, (int)Math.Floor(sigma * 3 * Math.Sqrt(2 * Math.PI) / 4 + 0.5));

    /// <summary>How far three boxes of <paramref name="window"/> spread: 3(w−1)/2 for odd w, 3w/2 − 1 for even.</summary>
    internal static int BoxBorder(int window) => (window & 1) == 1 ? 3 * ((window - 1) / 2) : 3 * (window / 2) - 1;

    /// <summary>The three box widths: w, w, and w (odd w) or w + 1 (even w).</summary>
    internal static (int A, int B, int C) BoxWidths(int window) => (window, window, (window & 1) == 1 ? window : window + 1);

    /// <summary>The sum of the three boxes' weights: w³, or w²(w + 1) for even w.</summary>
    internal static long BoxDivisor(int window)
    {
        long w2 = (long)window * window;
        return (window & 1) == 1 ? w2 * window : w2 * window + w2;
    }

    /// <summary>round(2³²/divisor): what a box sum is multiplied by before the final >> 32.</summary>
    internal static ulong BoxWeight(int window) => (ulong)Math.Round(1.0 / BoxDivisor(window) * (1UL << 32), MidpointRounding.AwayFromZero);

    private uint[] _lines = [];

    /// <summary>
    /// Both passes as vertical passes over 32-bit lanes, vectorised across the
    /// row, the horizontal one on the transposed mask; each pass rounds to 8
    /// bits.
    /// </summary>
    private void SmallBlur(Span<byte> data, int w, int h, ushort[] g)
    {
        var lines = Rent(ref _lines, 2 * w * h);
        var a = lines[..(w * h)];
        var b = lines.Slice(w * h, w * h);
        BoxBlur.Widen(data, a);
        BoxBlur.SmallColumns(a, w, h, b, g);   // vertical
        BoxBlur.Transpose(b, h, w, a);
        BoxBlur.SmallColumns(a, h, w, b, g);   // horizontal, transposed
        BoxBlur.Transpose(b, w, h, a);
        BoxBlur.Narrow(a, data);
    }

    private void BoxBlurXY(Span<byte> data, int w, int h, int window)
    {
        ulong weight = BoxWeight(window);
        const ulong half = 1UL << 31;
        var lines = Rent(ref _lines, 2 * w * h);
        var a = lines[..(w * h)];
        var b = lines.Slice(w * h, w * h);
        BoxBlur.Widen(data, b);
        BoxBlur.Transpose(b, h, w, a);
        BoxBlur.Columns(a, h, 0, w, b, w, window, weight, half, ref _sums);   // horizontal, transposed
        BoxBlur.Transpose(b, w, h, a);
        BoxBlur.Columns(a, w, 0, h, b, h, window, weight, half, ref _sums);   // vertical
        BoxBlur.Narrow(b, data);
    }

    private static Span<T> Rent<T>(ref T[] buffer, int n)
    {
        if (buffer.Length < n) buffer = new T[Math.Max(n, buffer.Length * 2)];
        return buffer.AsSpan(0, n);
    }
}
