using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed;

/// <summary>Coverage → pixels for a solid colour, through the clip mask if there is one.</summary>
internal unsafe struct SolidSink : ICoverageSink
{
    public uint* Origin;
    public int Stride;
    public uint Pm;
    public byte* Mask;
    public int MaskStride;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Blit(int y, int x, int len, int alpha)
    {
        uint* row = Origin + (long)y * Stride + x;
        if (Mask == null)
        {
            if (len == 1)
            {
                uint s = PixelMath.ScaleByCoverage(Pm, (uint)alpha);
                *row = (s >> 24) == 255 ? s : PixelMath.SrcOverColor(s, *row);
                return;
            }
            PixelMath.BlendSpan(row, len, PixelMath.ScaleByCoverage(Pm, (uint)alpha));
            return;
        }
        byte* m = Mask + (long)y * MaskStride + x;
        for (int i = 0; i < len; i++)
        {
            uint c = m[i];
            if (c == 0) continue;
            if (c != 255) c = PixelMath.MulDiv255Round((uint)alpha, c);
            else c = (uint)alpha;
            if (c == 0) continue;
            uint s = PixelMath.ScaleByCoverage(Pm, c);
            row[i] = (s >> 24) == 255 ? s : PixelMath.SrcOverColor(s, row[i]);
        }
    }
}

/// <summary>Coverage → pixels for a shader: shade the span, then scale each pixel by coverage × paint alpha × mask.</summary>
internal unsafe struct ShaderSink : ICoverageSink
{
    public uint* Origin;
    public int Stride;
    public Shader Shader;
    public uint PaintAlpha;
    public bool SwapRB;
    public uint[] Scratch;
    public byte* Mask;
    public int MaskStride;

    public void Blit(int y, int x, int len, int alpha)
    {
        uint a = PaintAlpha == 255 ? (uint)alpha : PixelMath.MulDiv255Round((uint)alpha, PaintAlpha);
        if (a == 0) return;
        uint* row = Origin + (long)y * Stride;
        byte* m = Mask == null ? null : Mask + (long)y * MaskStride;
        // An opaque shader at full coverage is its own output; anything
        // else is shaded, then blended a vector at a time (ShaderBlend.cs).
        if (a == 255 && m == null && !SwapRB && Shader.IsOpaque)
        {
            Shader.ShadeSpan(x, y, new Span<uint>(row + x, len));
            return;
        }
        while (len > 0)
        {
            int n = Math.Min(len, Scratch.Length);
            var span = Scratch.AsSpan(0, n);
            Shader.ShadeSpan(x, y, span);
            if (SwapRB) for (int i = 0; i < n; i++) span[i] = PixelMath.SwapRB(span[i]);
            ShaderBlend.Blend(row + x, span, a, m == null ? null : m + x);
            x += n;
            len -= n;
        }
    }
}

/// <summary>Coverage → a clip mask: the new clip's coverage, times the enclosing mask's if there is one.</summary>
internal unsafe struct MaskSink : ICoverageSink
{
    public byte* Origin;
    public int Stride;
    public byte* Prev;
    public int PrevStride;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Blit(int y, int x, int len, int alpha)
    {
        byte* d = Origin + (long)y * Stride + x;
        if (Prev == null)
        {
            new Span<byte>(d, len).Fill((byte)alpha);
            return;
        }
        byte* p = Prev + (long)y * PrevStride + x;
        for (int i = 0; i < len; i++)
            d[i] = alpha == 255 ? p[i] : (byte)PixelMath.MulDiv255Round((uint)alpha, p[i]);
    }
}
