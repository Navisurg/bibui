using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Bibui.Raster.Skia;

/// <summary>An SKPath as a bibui path.</summary>
public sealed class SkiaPath : RasterPath
{
    internal SkiaPath(SKPath native) => Native = native;

    /// <summary>The Skia path. The Skia backend's own escape hatch; bibui core never reaches for it.</summary>
    public SKPath Native { get; }

    public override void MoveTo(float x, float y) => Native.MoveTo(x, y);
    public override void LineTo(float x, float y) => Native.LineTo(x, y);
    public override void QuadTo(float x1, float y1, float x2, float y2) => Native.QuadTo(x1, y1, x2, y2);
    public override void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3)
        => Native.CubicTo(x1, y1, x2, y2, x3, y3);

    public override void ArcTo(float rx, float ry, float xAxisRotate, bool largeArc, bool sweep, float x, float y)
        => Native.ArcTo(rx, ry, xAxisRotate,
            largeArc ? SKPathArcSize.Large : SKPathArcSize.Small,
            sweep ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise,
            x, y);

    public override void AddArc(RectF oval, float startAngle, float sweepAngle)
        => Native.AddArc(oval.ToSk(), startAngle, sweepAngle);

    public override void Close() => Native.Close();

    public override void AddPath(RasterPath source, in Matrix3x2 transform)
    {
        var src = source as SkiaPath ?? throw new ArgumentException(
            $"A {source.GetType().Name} is not a Skia path; it came from another raster backend.", nameof(source));
        var m = transform.ToSk();
        // Into an empty path, Transform writes the mapped copy outright — the
        // exact operation icons were scaled with before the backend seam, so
        // their geometry (and pixels) stay bit-identical.
        if (Native.IsEmpty) src.Native.Transform(m, Native);
        else Native.AddPath(src.Native, ref m);
    }

    public override void Rewind() => Native.Rewind();

    public override bool IsEmpty => Native.IsEmpty;

    protected override void Dispose(bool disposing)
    {
        if (disposing) Native.Dispose();
    }
}

/// <summary>An SKSurface as a bibui render target.</summary>
public sealed class SkiaRasterSurface : IRasterSurface
{
    private readonly bool _owns;
    private readonly SkiaRasterCanvas _canvas;

    internal SkiaRasterSurface(SkiaRasterBackend backend, SKSurface native, bool owns)
    {
        Native = native;
        _owns = owns;
        _canvas = new SkiaRasterCanvas(backend, native.Canvas, this);
        // An SKSurface does not say its size directly; before anything has
        // clipped it, its canvas's device clip is the whole surface.
        var bounds = native.Canvas.DeviceClipBounds;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    /// <summary>The Skia surface. The Skia backend's own escape hatch.</summary>
    public SKSurface Native { get; }

    public IRasterCanvas Canvas => _canvas;

    public int Width { get; }
    public int Height { get; }

    public unsafe bool ReadPixels(Span<byte> bgraPremul, int width, int height)
    {
        if ((long)width * height * 4 != bgraPremul.Length) return false;

        // A CPU surface already in the layout we want is one memcpy away — the
        // path the readback pipeline has always taken. Anything else (a GPU
        // surface, another colour type) asks Skia to read and convert.
        using (var pixmap = Native.PeekPixels())
        {
            if (pixmap is not null
                && pixmap.ColorType == SKColorType.Bgra8888
                && pixmap.AlphaType == SKAlphaType.Premul
                && pixmap.Width == width && pixmap.Height == height
                && pixmap.RowBytes == width * 4)
            {
                new ReadOnlySpan<byte>((void*)pixmap.GetPixels(), bgraPremul.Length).CopyTo(bgraPremul);
                return true;
            }
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        fixed (byte* p = bgraPremul)
            return Native.ReadPixels(info, (nint)p, info.RowBytes, 0, 0);
    }

    public void Dispose()
    {
        if (_owns) Native.Dispose();
    }
}
