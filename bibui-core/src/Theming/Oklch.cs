namespace Bibui.Core.Theming;

/// <summary>
/// A colour as lightness, chroma and hue in the OKLab space: L from 0 (black)
/// to 1 (white), C how far from grey, H the hue angle in degrees. Equal steps
/// of L look like equal steps of lightness whatever the hue, which is what a
/// ramp of eleven steps needs and what sRGB channels do not give.
/// </summary>
/// <remarks>
/// The maths a theme generated from seeds is built with: <see cref="Theme.FromSeed"/>
/// draws its ramps here, and <see cref="Theme.Contrast"/> measures what they give.
/// Doubles throughout, so a ramp is the same on every machine.
/// </remarks>
internal readonly record struct Oklch(double L, double C, double H)
{
    /// <summary>The colour <paramref name="hex"/> names; its alpha, if any, is ignored.</summary>
    public static Oklch FromHex(string hex)
    {
        var (r, g, b) = Linear(hex);

        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);

        double L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        double A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        double B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;

        double c = Math.Sqrt(A * A + B * B);
        double h = Math.Atan2(B, A) * 180.0 / Math.PI;
        return new Oklch(L, c, h < 0 ? h + 360.0 : h);
    }

    /// <summary>
    /// This colour as <c>#RRGGBB</c>. One sRGB cannot show keeps its lightness
    /// and hue and loses chroma until it can, so a ramp step stays at the
    /// lightness it was asked for.
    /// </summary>
    public string ToHex()
    {
        var (r, g, b) = ToLinear(L, C, H);
        if (!InGamut(r, g, b))
        {
            double lo = 0, hi = C;
            for (int i = 0; i < 32; i++)
            {
                double mid = (lo + hi) / 2;
                var (mr, mg, mb) = ToLinear(L, mid, H);
                if (InGamut(mr, mg, mb)) lo = mid; else hi = mid;
            }
            (r, g, b) = ToLinear(L, lo, H);
        }
        return $"#{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}";
    }

    /// <summary>
    /// How bright <paramref name="hex"/> is to the eye, 0 for black to 1 for
    /// white: the relative luminance the contrast ratio is taken between.
    /// </summary>
    public static double Luminance(string hex)
    {
        var (r, g, b) = Linear(hex);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>
    /// The contrast ratio of two colours, from 1 (the same) to 21 (black on
    /// white): the lighter luminance plus 0.05 over the darker plus 0.05.
    /// </summary>
    public static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static (double R, double G, double B) ToLinear(double L, double C, double H)
    {
        double hr = H * Math.PI / 180.0;
        double A = C * Math.Cos(hr), B = C * Math.Sin(hr);

        double l = L + 0.3963377774 * A + 0.2158037573 * B;
        double m = L - 0.1055613458 * A - 0.0638541728 * B;
        double s = L - 0.0894841775 * A - 1.2914855480 * B;
        l *= l * l; m *= m * m; s *= s * s;

        return (
            +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    // A hair of slack, so a colour that rounds into range is not robbed of chroma.
    private static bool InGamut(double r, double g, double b) =>
        r is >= -1e-6 and <= 1 + 1e-6 && g is >= -1e-6 and <= 1 + 1e-6 && b is >= -1e-6 and <= 1 + 1e-6;

    private static (double R, double G, double B) Linear(string hex)
    {
        ColorRamp.CheckHex(hex, nameof(hex));
        var (r, g, b, _) = UiColor.ParseHex(hex);
        return (ToLinear(r), ToLinear(g), ToLinear(b));

        static double ToLinear(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    private static int Channel(double linear)
    {
        double c = Math.Clamp(linear, 0, 1);
        double encoded = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (int)Math.Round(encoded * 255.0, MidpointRounding.AwayFromZero);
    }
}
