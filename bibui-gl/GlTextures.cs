using Bibui.Draw;
using Silk.NET.OpenGL;

namespace Bibui.Gl;

/// <summary>
/// GL textures, made, filled and deleted: what a renderer on a GL context makes
/// its textures with. rad's GL backend calls (<c>r_tex2d_alloc</c>,
/// <c>r_fill_tex2d_region</c>, <c>r_tex2d_release</c>). The renderer keeps the
/// ids in its own table and hands out handles; this knows nothing of handles.
///
/// <para>On the thread where the context is current. Each call moves the
/// texture binding and the pixel store: a renderer that caches GL state (Skia)
/// resets it before it draws again.</para>
/// </summary>
public static class GlTextures
{
    /// <summary>
    /// A new <paramref name="width"/> × <paramref name="height"/> texture of
    /// <paramref name="format"/>, sampled linearly and clamped, its pixels zero,
    /// and its GL name.
    /// </summary>
    public static uint Create(GL gl, TextureFormat format, int width, int height)
    {
        uint id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, id);
        // Without mipmaps a texture is complete only with a non-mipmap filter.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat(format), (uint)width, (uint)height, 0,
                PixelFormat(format), PixelType.UnsignedByte, null);
        }
        return id;
    }

    /// <summary>Overwrites the whole texture with <paramref name="pixels"/>, rows <paramref name="rowBytes"/> apart.</summary>
    public static void Upload(GL gl, uint texture, TextureFormat format, int width, int height, ReadOnlySpan<byte> pixels, int rowBytes)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        // Rows of a 1- or 2-byte format need not start on 4 bytes.
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.PixelStore(PixelStoreParameter.UnpackRowLength, rowBytes / format.BytesPerPixel());
        gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height,
            PixelFormat(format), PixelType.UnsignedByte, pixels);
        gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
    }

    public static void Delete(GL gl, uint texture) => gl.DeleteTexture(texture);

    /// <summary>The sized format a texture of <paramref name="format"/> is stored as. A8 is one red channel, read as alpha by whoever samples it.</summary>
    public static InternalFormat InternalFormat(TextureFormat format) => format switch
    {
        TextureFormat.R8 or TextureFormat.A8 => Silk.NET.OpenGL.InternalFormat.R8,
        TextureFormat.RG8 => Silk.NET.OpenGL.InternalFormat.RG8,
        _ => Silk.NET.OpenGL.InternalFormat.Rgba8,
    };

    // The layout a format's bytes are handed over in.
    private static PixelFormat PixelFormat(TextureFormat format) => format switch
    {
        TextureFormat.R8 or TextureFormat.A8 => Silk.NET.OpenGL.PixelFormat.Red,
        TextureFormat.RG8 => Silk.NET.OpenGL.PixelFormat.RG,
        _ => Silk.NET.OpenGL.PixelFormat.Rgba,
    };
}
