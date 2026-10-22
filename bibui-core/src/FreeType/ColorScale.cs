namespace Bibui.Core.FreeType;

/// <summary>
/// Resizes a colour glyph's premultiplied pixels: area averaging when it shrinks
/// (a 136 px emoji drawn at 20 px keeps every pixel's share, rather than the
/// few a point sample would pick), bilinear when it grows.
/// </summary>
internal static class ColorScale
{
    public static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        float fx = sw / (float)dw, fy = sh / (float)dh;
        if (fx >= 1f && fy >= 1f)
        {
            Span<float> acc = stackalloc float[4];
            for (int y = 0; y < dh; y++)
            {
                float y0 = y * fy, y1 = y0 + fy;
                for (int x = 0; x < dw; x++)
                {
                    float x0 = x * fx, x1 = x0 + fx;
                    acc.Clear();
                    for (int sy = (int)y0; sy < Math.Min(sh, (int)MathF.Ceiling(y1)); sy++)
                    {
                        float wy = MathF.Min(y1, sy + 1) - MathF.Max(y0, sy);
                        if (wy <= 0f) continue;
                        for (int sx = (int)x0; sx < Math.Min(sw, (int)MathF.Ceiling(x1)); sx++)
                        {
                            float wgt = (MathF.Min(x1, sx + 1) - MathF.Max(x0, sx)) * wy;
                            if (wgt <= 0f) continue;
                            int i = (sy * sw + sx) * 4;
                            acc[0] += src[i] * wgt; acc[1] += src[i + 1] * wgt; acc[2] += src[i + 2] * wgt; acc[3] += src[i + 3] * wgt;
                        }
                    }
                    float area = fx * fy;
                    int o = (y * dw + x) * 4;
                    byte a = Clamp(acc[3] / area);
                    dst[o + 3] = a;
                    // Premultiplied: no channel may exceed alpha after rounding.
                    dst[o] = Math.Min(a, Clamp(acc[0] / area));
                    dst[o + 1] = Math.Min(a, Clamp(acc[1] / area));
                    dst[o + 2] = Math.Min(a, Clamp(acc[2] / area));
                }
            }
            return dst;
        }

        for (int y = 0; y < dh; y++)
        {
            float sy = MathF.Max(0f, (y + 0.5f) * fy - 0.5f);
            int y0 = Math.Min(sh - 1, (int)sy), y1 = Math.Min(sh - 1, y0 + 1);
            float ty = sy - y0;
            for (int x = 0; x < dw; x++)
            {
                float sx = MathF.Max(0f, (x + 0.5f) * fx - 0.5f);
                int x0 = Math.Min(sw - 1, (int)sx), x1 = Math.Min(sw - 1, x0 + 1);
                float tx = sx - x0;
                int o = (y * dw + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float top = src[(y0 * sw + x0) * 4 + c] * (1 - tx) + src[(y0 * sw + x1) * 4 + c] * tx;
                    float bottom = src[(y1 * sw + x0) * 4 + c] * (1 - tx) + src[(y1 * sw + x1) * 4 + c] * tx;
                    dst[o + c] = Clamp(top * (1 - ty) + bottom * ty);
                }
            }
        }
        return dst;
    }

    private static byte Clamp(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
