namespace Bibui.Text.FreeType;

/// <summary>
/// The text "preblend": before a coverage mask is blended, each coverage value is
/// remapped through a table chosen by the text colour's luminance, so an 8-bit
/// sRGB src-over approximates a blend in linear light. Without it dark text on a
/// light ground comes out visibly heavier (up to 92/255 off the baselines).
///
/// <para>The fixture baselines are drawn by Skia, so this reproduces its mask
/// gamma exactly: SkMaskGamma as SkTMaskGamma_build_correcting_lut builds it, with
/// the parameters SkiaSharp 2.88.8 was fitted to: the sRGB transfer curve
/// (gamma 0) and contrast 0.5. The colour's luminance (SkComputeLuminance) is
/// quantised to 3 bits, which picks one of eight tables.</para>
///
/// <para>Static, immutable and built once: safe from any thread.</para>
/// </summary>
public static class MaskGamma
{
    /// <summary>
    /// The contrast applied: 0.5 as stored in the scaler context, rounded to eighths of 255 (<c>round(0.5 * 255) / 255 = 128/255</c>).
    /// Plain 0.5 leaves 19 of the 256 entries of the dark-text table one level light
    /// (found by inverting the baseline pixels; 128/255 matches all 256).
    /// </summary>
    public const float Contrast = 128f / 255f;

    // Eight 256-entry tables back to back, one per luminance bucket.
    private static readonly byte[] s_tables = Build();

    /// <summary>
    /// The 256-entry coverage table for text drawn in <paramref name="color"/>:
    /// composite with <c>table[coverage]</c> in place of <c>coverage</c>. Alpha is
    /// not part of the choice; only the RGB is.
    /// </summary>
    public static ReadOnlySpan<byte> Table(TextColor color) => Table(color.R, color.G, color.B);

    /// <inheritdoc cref="Table(TextColor)"/>
    public static ReadOnlySpan<byte> Table(byte r, byte g, byte b)
    {
        int lum = (r * 54 + g * 183 + b * 19) >> 8;   // SkComputeLuminance
        return new ReadOnlySpan<byte>(s_tables, (lum >> 5) * 256, 256);   // CanonicalColor: 3 bits
    }

    private static byte[] Build()
    {
        var all = new byte[8 * 256];
        for (int i = 0; i < 8; i++)
        {
            int srcI = i << 5 | i << 2 | i >> 1;   // sk_t_scale255<3>
            BuildLut(srcI, all.AsSpan(i * 256, 256));
        }
        return all;
    }

    // sRGB transfer, both ways (SkSRGBColorSpaceLuminance).
    private static float ToLuma(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    private static float FromLuma(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

    private static float ApplyContrast(float a, float contrast) => a + (1f - a) * contrast * a;

    private static void BuildLut(int srcI, Span<byte> table)
    {
        float src = srcI / 255f, dst = 1f - src;
        float linSrc = ToLuma(src), linDst = ToLuma(dst);
        float adjContrast = Contrast * linDst;
        for (int i = 0; i < 256; i++)
        {
            float a = ApplyContrast(i / 255f, adjContrast);
            if (MathF.Abs(src - dst) < 1f / 256f)
            {
                table[i] = (byte)MathF.Round(255f * a);
                continue;
            }
            float linOut = linSrc * a + (1f - a) * linDst;
            float result = (FromLuma(linOut) - dst) / (src - dst);
            table[i] = (byte)Math.Clamp((int)MathF.Round(255f * result), 0, 255);
        }
    }
}
