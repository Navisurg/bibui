using Bibui.Draw;
using Bibui.Gl;
using SkiaSharp;
using Silk.NET.OpenGL;

namespace Bibui.Raster.Skia;

/// <summary>
/// Draws a <see cref="DrawList"/> with Skia on the GPU, into a window's GL
/// framebuffer (<see cref="GlTarget"/>). Its textures are GL textures on that
/// context, made through <see cref="GlTextures"/> and wrapped once for Skia.
/// <see cref="SkiaCpuRenderer"/> is the same drawing into memory.
///
/// <para>On the thread where the GL context is current, one at a time.</para>
/// </summary>
public sealed class SkiaGlRenderer : IDisposable
{
    /// <summary>
    /// A renderer on <paramref name="target"/>: Skia's GPU context is made on that
    /// GL context, which must be current on this thread. Throws when Skia cannot
    /// drive it; <see cref="ForGl"/> answers null instead.
    /// </summary>
    public SkiaGlRenderer(GlTarget target)
        : this(target, GpuContext(target) ?? throw new InvalidOperationException("Skia cannot draw with this GL context."))
    {
    }

    /// <summary>
    /// A renderer on <paramref name="target"/>, or null when Skia cannot drive its
    /// GL context (too old, software GL without what Skia needs). The caller falls
    /// back, e.g. to a <c>SkiaCpuRenderer</c> drawing into a
    /// <c>GlCpuFramebuffer</c>'s memory.
    /// </summary>
    public static SkiaGlRenderer? ForGl(GlTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return GpuContext(target) is { } gpu ? new SkiaGlRenderer(target, gpu) : null;
    }

    private SkiaGlRenderer(GlTarget target, GRContext gpu)
    {
        ArgumentNullException.ThrowIfNull(target);
        (_target, _gpu) = (target, gpu);
        _gl = GL.GetApi(target.GetProcAddress);
    }

    private readonly GlTarget _target;
    private readonly GL _gl;
    private readonly SkiaOps _ops = new();

    // Skia's GPU context on the target's GL context, and the surface over its
    // framebuffer, remade when the size changes.
    private readonly GRContext _gpu;
    private GRBackendRenderTarget? _framebuffer;
    private SKSurface? _surface;
    private int _width, _height;

    /// <summary>Byte budget for Skia's GPU resource cache (glyph atlases, gradient ramps).</summary>
    public long GpuResourceCacheLimit
    {
        get => _gpu.GetResourceCacheLimit();
        set => _gpu.SetResourceCacheLimit(value);
    }

    private static GRContext? GpuContext(GlTarget target)
    {
        var get = target.GetProcAddress;
        using var gl = GRGlInterface.Create(name => get(name));
        if (gl is null) return null;
        var context = GRContext.CreateGl(gl);
        // Bounded: a long-running window would otherwise grow toward Skia's
        // much larger default budget.
        context?.SetResourceCacheLimit(96 * 1024 * 1024);
        return context;
    }

    /// <summary>Draws <paramref name="list"/> into the framebuffer at its current size, then flushes for the swap that follows.</summary>
    public void Draw(DrawList list)
    {
        // Skia caches GL state between draws, and anything else touching the
        // context (the window, our texture calls) invalidates that cache.
        // Cheap when nothing changed.
        _gpu.ResetContext();
        if (_surface is null || _width != _target.Width || _height != _target.Height)
        {
            _surface?.Dispose();
            _framebuffer?.Dispose();
            const uint GlRgba8 = 0x8058;
            _framebuffer = new GRBackendRenderTarget(_target.Width, _target.Height, sampleCount: 0, _target.StencilBits,
                new GRGlFramebufferInfo(_target.Framebuffer, GlRgba8));
            _surface = SKSurface.Create(_gpu, _framebuffer, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
                ?? throw new InvalidOperationException("Skia made no surface over the GL framebuffer.");
            (_width, _height) = (_target.Width, _target.Height);
        }
        _ops.Draw(list, _surface);
        _surface.Canvas.Flush();
        _gpu.Flush();
    }

    // ── Textures ─────────────────────────────────────────────────────────────
    // GL textures on the target's context, made by usage code, which owns them:
    // a draw list only names a handle. Static and Stream are made alike, as rad's
    // GL backend makes them; the kind decides whether an update is taken.

    /// <summary>
    /// A <paramref name="width"/> × <paramref name="height"/> GL texture of
    /// <paramref name="format"/>, from <paramref name="pixels"/> (rows
    /// <paramref name="rowBytes"/> apart, or packed tight when 0; RGBA8 is straight
    /// alpha), wrapped once for Skia. Empty pixels make a Stream texture of zeros.
    /// The caller owns it: <see cref="ReleaseTexture"/> frees it.
    /// </summary>
    public TextureHandle CreateTexture(TextureFormat format, TextureKind kind, int width, int height,
        ReadOnlySpan<byte> pixels, int rowBytes = 0)
    {
        bool blank = pixels.IsEmpty && kind == TextureKind.Stream;
        int stride = blank ? 0 : TexturePixels.RowBytes(format, width, height, pixels, rowBytes);
        var texture = new SkiaTexture { Format = format, Kind = kind, Width = width, Height = height };
        texture.GlTexture = GlTextures.Create(_gl, format, width, height);
        if (!blank) GlTextures.Upload(_gl, texture.GlTexture, format, width, height, pixels, stride);
        // Our GL calls moved the binding and the pixel store under Skia.
        _gpu.ResetContext();
        var info = SkiaOps.Info(format, width, height);
        using var backend = new GRBackendTexture(width, height, false,
            new GRGlTextureInfo((uint)TextureTarget.Texture2D, texture.GlTexture, (uint)GlTextures.InternalFormat(format)));
        texture.Image = SKImage.FromTexture(_gpu, backend, GRSurfaceOrigin.TopLeft, info.ColorType, info.AlphaType)
            ?? throw new InvalidOperationException($"Skia took no {format} image over GL texture {texture.GlTexture}.");
        return _ops.Textures.Add(texture);
    }

    /// <summary>
    /// Overwrites a Stream texture's pixels, the whole texture, at the size and
    /// format it was made with: into the GL texture Skia's image already wraps, no
    /// new image. A Static texture does not change: its update is ignored. So is a
    /// released handle.
    /// </summary>
    public void UpdateTexture(TextureHandle texture, ReadOnlySpan<byte> pixels, int rowBytes = 0)
    {
        if (_ops.Textures.Get(texture) is not { Kind: TextureKind.Stream } t) return;
        int stride = TexturePixels.RowBytes(t.Format, t.Width, t.Height, pixels, rowBytes);
        GlTextures.Upload(_gl, t.GlTexture, t.Format, t.Width, t.Height, pixels, stride);
        _gpu.ResetContext();
        _ops.Textures.Touch(texture);
    }

    /// <summary>Frees <paramref name="texture"/>; drawing it afterwards draws nothing.</summary>
    public void ReleaseTexture(TextureHandle texture)
    {
        if (_ops.Textures.Remove(texture) is { } t) Free(t);
    }

    // The image first: it borrows the GL texture, which goes after it.
    private void Free(SkiaTexture t)
    {
        t.Image?.Dispose();
        GlTextures.Delete(_gl, t.GlTexture);
    }

    public void Dispose()
    {
        // Everything on the GPU context goes before the window's GL context does:
        // dispose the renderer first.
        for (var node = _ops.Textures.FirstLive; node is not null; node = node.Next) Free(node.Item!);
        _ops.Dispose();
        _surface?.Dispose();
        _framebuffer?.Dispose();
        _gpu.Dispose();
    }
}
