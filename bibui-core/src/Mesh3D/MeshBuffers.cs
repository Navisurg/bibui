using System.Numerics;
using Bibui.Core.Draw;

namespace Bibui.Core;

/// <summary>
/// What lies under a point of a 3D view: the mesh's id in its scene, the face's
/// index in that mesh, the weights of the face's three vertices (in the order
/// the face names them) that give the point, the point in the world, and its
/// depth, z along the camera's axis.
/// </summary>
public readonly record struct MeshHit(int Mesh, int Face, Vector3 Barycentric, Vector3 Point, float Depth);

/// <summary>
/// A <see cref="MeshScene"/> drawn without a window, into buffers the app
/// reads: colour, depth, mesh and face ids and, when asked, normals, one sample
/// a pixel. Pixel (x, y) is sampled at its centre; the camera's axis passes
/// through the image's centre, and its focal length in pixels is
/// <c>Height / 2 / tan(FovY / 2)</c>. Camera axes are OpenCV's: x right, y
/// down, z ahead. Each buffer is converted from the rasteriser's own when it is
/// first read after a <see cref="Render"/>, and is valid until the next one.
/// </summary>
public sealed class MeshBuffers
{
    private static readonly Mesh3DOptions s_defaults = new();

    private readonly MeshRasterizer _raster = new();
    private readonly PixelImage _image = new();
    private readonly byte[] _color;
    private readonly float[] _depth;
    private readonly int[] _meshIds, _faceIds;
    private float[] _normals = [];

    // Which render each buffer was converted for; 0 is none.
    private uint _rendered, _colorOf, _depthOf, _idsOf, _normalsOf;
    private bool _normalsDrawn;

    /// <summary>Buffers of <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    public MeshBuffers(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"Buffers are at least 1×1, not {width}×{height}.");
        (Width, Height) = (width, height);
        _color = new byte[width * height * 4];
        _depth = new float[width * height];
        _meshIds = new int[width * height];
        _faceIds = new int[width * height];
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Also draws the normal at each pixel, for <see cref="NormalXyz"/>. Off by default: it costs a little in the rasteriser.</summary>
    public bool Normals { get; set; }

    /// <summary>
    /// Draws <paramref name="scene"/> through <paramref name="camera"/>, as a view
    /// with <paramref name="options"/> would (null: the defaults), but without the
    /// overlay and with one sample a pixel whatever <see cref="Mesh3DOptions.Supersample"/> says.
    /// </summary>
    public void Render(MeshScene scene, OrbitCamera camera, Mesh3DOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _raster.Render(scene, camera, options ?? s_defaults, _image, Width, Height, supersample: 1, Normals);
        _normalsDrawn = Normals;
        _rendered++;
    }

    /// <summary>RGBA8, straight alpha, rows tight, the top row first: the background where there is no mesh (transparent when null).</summary>
    public ReadOnlySpan<byte> Color
    {
        get
        {
            if (_colorOf == _rendered) return _color;
            _colorOf = _rendered;
            var argb = _image.Pixels;
            for (int i = 0; i < argb.Length; i++)
            {
                uint p = argb[i], a = p >> 24;
                uint r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                if (a is > 0 and < 0xFF) (r, g, b) = ((r * 0xFF + a / 2) / a, (g * 0xFF + a / 2) / a, (b * 0xFF + a / 2) / a);
                (_color[4 * i], _color[4 * i + 1], _color[4 * i + 2], _color[4 * i + 3]) = ((byte)r, (byte)g, (byte)b, (byte)a);
            }
            return _color;
        }
    }

    /// <summary>
    /// The depth at each pixel: the distance along the camera's axis (z in camera
    /// coordinates), not along the ray, in the scene's units; +∞ where there is no mesh.
    /// </summary>
    public ReadOnlySpan<float> Depth
    {
        get
        {
            if (_depthOf == _rendered) return _depth;
            _depthOf = _rendered;
            var inverse = _raster.InverseDepth;
            for (int i = 0; i < inverse.Length; i++) _depth[i] = inverse[i] == 0f ? float.PositiveInfinity : 1f / inverse[i];
            return _depth;
        }
    }

    /// <summary>The id in the scene of the mesh at each pixel; -1 where there is none.</summary>
    public ReadOnlySpan<int> MeshIds
    {
        get
        {
            Ids();
            return _meshIds;
        }
    }

    /// <summary>The index in its mesh of the face at each pixel; -1 where there is none.</summary>
    public ReadOnlySpan<int> FaceIds
    {
        get
        {
            Ids();
            return _faceIds;
        }
    }

    /// <summary>
    /// The surface's normal at each pixel, three floats a pixel, unit length, in
    /// the camera's axes (x right, y down, z ahead) and turned toward the eye; 0
    /// where there is no mesh. Drawn only with <see cref="Normals"/> on.
    /// </summary>
    public ReadOnlySpan<float> NormalXyz
    {
        get
        {
            if (!_normalsDrawn) throw new InvalidOperationException("Normals were not drawn: set MeshBuffers.Normals before Render.");
            if (_normalsOf == _rendered) return _normals;
            _normalsOf = _rendered;
            if (_normals.Length != 3 * Width * Height) _normals = new float[3 * Width * Height];
            var drawn = _raster.CameraNormals;
            float f = _raster.FocalPixels, cx = Width * 0.5f, cy = Height * 0.5f;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int i = 3 * (y * Width + x);
                    // Drawn along right, up and ahead; turned to x right, y down, z ahead.
                    var n = new Vector3(drawn[i], -drawn[i + 1], drawn[i + 2]);
                    var ray = new Vector3((x + 0.5f - cx) / f, (y + 0.5f - cy) / f, 1f);
                    if (Vector3.Dot(n, ray) > 0f) n = -n;
                    (_normals[i], _normals[i + 1], _normals[i + 2]) = (n.X, n.Y, n.Z);
                }
            return _normals;
        }
    }

    /// <summary>What lies under <paramref name="pixel"/> (in this buffer's pixels; a pixel's centre is at +0.5), or null where there is no mesh.</summary>
    public MeshHit? Pick(Vector2 pixel) => _raster.Pick(pixel);

    private void Ids()
    {
        if (_idsOf == _rendered) return;
        _idsOf = _rendered;
        var faces = _raster.FacePlusOne;
        var faceMesh = _raster.FaceMesh;
        var meshStart = _raster.MeshFaceStart;
        for (int i = 0; i < faces.Length; i++)
        {
            int f = faces[i] - 1;
            if (f < 0) { _meshIds[i] = _faceIds[i] = -1; continue; }
            int m = faceMesh[f];
            _meshIds[i] = m;
            _faceIds[i] = f - meshStart[m];
        }
    }
}
