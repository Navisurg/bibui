using System.Numerics;

namespace Bibui.Raster.Managed;

public sealed unsafe partial class Canvas
{
    /// <summary>
    /// How far, in device px, an image's edges may lie off the pixel grid and still be
    /// drawn 1:1 by <see cref="DrawImageAligned"/>. A rect laid out in logical px and
    /// scaled by an output's factor (1.25) lands a few millionths off; anything a
    /// person could see is far more.
    /// </summary>
    public const float GridTolerance = 1f / 1024f;

    /// <summary>
    /// Draws an image <b>1:1</b>: <paramref name="argb"/>, premultiplied <c>0xAARRGGBB</c>
    /// pixels <paramref name="width"/> × <paramref name="height"/> packed tight, into
    /// <paramref name="dest"/> (local coordinates), when the transform is axis-aligned
    /// and unflipped and <paramref name="dest"/> lands on exactly
    /// <paramref name="width"/> × <paramref name="height"/> device pixels on the pixel grid
    /// (within <see cref="GridTolerance"/>). Then no pixel is sampled or filtered: each
    /// row is copied (an <paramref name="opaque"/> image at full
    /// <paramref name="alpha"/> with no clip mask) or blended src-over at
    /// <paramref name="alpha"/>/255 through the clip mask — the same bytes an image
    /// shader drawing the rect at that place gives.
    ///
    /// <para>Returns false, drawing nothing, when the image does not land 1:1 (scaled,
    /// rotated, flipped, at a fractional position, or on an RGBA surface): the caller
    /// draws it through its shader. <paramref name="opaque"/> must be true only when
    /// every pixel's alpha is 255.</para>
    /// </summary>
    public bool DrawImageAligned(ReadOnlySpan<uint> argb, int width, int height, Rect dest, byte alpha = 255, bool opaque = false)
    {
        if (width <= 0 || height <= 0 || argb.Length < width * height) return false;
        if (_surface.Order != PixelOrder.Bgra8888) return false;
        if (!OnGrid(dest, width, height, out int left, out int top)) return false;
        if (_s.Clip.IsEmpty || alpha == 0) return true;
        var b = new IRect(left, top, left + width, top + height).Intersect(_s.Clip);
        if (b.IsEmpty) return true;
        _s.Layer?.Touch(b);
        var t = Target();
        var mask = _s.Mask;
        bool copy = opaque && alpha == 255 && mask is null;
        fixed (uint* src0 = argb)
        {
            for (int y = b.Top; y < b.Bottom; y++)
            {
                uint* src = src0 + (long)(y - top) * width + (b.Left - left);
                uint* dst = t.Origin + (long)y * t.Stride + b.Left;
                if (copy) new ReadOnlySpan<uint>(src, b.Width).CopyTo(new Span<uint>(dst, b.Width));
                else ShaderBlend.Blend(dst, new ReadOnlySpan<uint>(src, b.Width), alpha,
                    mask is null ? null : mask.Origin + (long)y * mask.Stride + b.Left);
            }
        }
        return true;
    }

    /// <summary>
    /// Whether <paramref name="dest"/> maps to exactly <paramref name="width"/> ×
    /// <paramref name="height"/> device pixels on the grid under an axis-aligned,
    /// unflipped transform; and where its top-left pixel is.
    /// </summary>
    internal bool OnGrid(Rect dest, int width, int height, out int left, out int top)
    {
        left = top = 0;
        var m = _s.Matrix;
        if (!(IsAxisAligned(m) && m.M11 > 0f && m.M22 > 0f)) return false;
        if (!(dest.Right > dest.Left && dest.Bottom > dest.Top)) return false;   // an unsorted rect flips the image
        var a = Vector2.Transform(new Vector2(dest.Left, dest.Top), m);
        var c = Vector2.Transform(new Vector2(dest.Right, dest.Bottom), m);
        float l = MathF.Round(a.X), tp = MathF.Round(a.Y);
        if (MathF.Abs(a.X - l) > GridTolerance || MathF.Abs(a.Y - tp) > GridTolerance) return false;
        if (MathF.Abs(c.X - (l + width)) > GridTolerance || MathF.Abs(c.Y - (tp + height)) > GridTolerance) return false;
        if (!(MathF.Abs(l) < 1 << 24 && MathF.Abs(tp) < 1 << 24)) return false;
        left = (int)l;
        top = (int)tp;
        return true;
    }
}
