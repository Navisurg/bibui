using Bibui.Theming;

namespace Bibui.Renderer;

public sealed class RenderOutput
{
    /// <summary>
    /// Raw BGRA8888 pixel data read directly from the CPU raster surface.
    /// Populated on the readback path (Render) and on the immediate-mode path
    /// when pixel capture is enabled; empty otherwise. Pass to OpenGL with
    /// GL_BGRA format for zero-copy upload.
    /// </summary>
    public required byte[] RawBgraBytes { get; init; }

    /// <summary>
    /// True when this output carries real pixel data — i.e. <see cref="PngBytes"/>
    /// is safe to access. False for immediate-mode frames rendered without pixel
    /// capture, whose PNG factory throws.
    /// </summary>
    public bool HasPixels => RawBgraBytes.Length > 0;

    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>
    /// Factory that produces PNG bytes on first access: encoding waits until a
    /// caller actually needs PNG (verification, screenshots). The display path
    /// never triggers this.
    /// </summary>
    public required Func<byte[]> PngFactory { init => _png = new Lazy<byte[]>(value); }

    private Lazy<byte[]> _png = null!;

    /// <summary>PNG-encoded frame. Lazily computed on first access.</summary>
    public byte[] PngBytes => _png.Value;
}
