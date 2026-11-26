using System.Numerics;
using Bibui.Core.Theming;

namespace Bibui.Core;

/// <summary>
/// A triangle mesh for <c>runtime.Mesh3D</c>: faces fixed for its life, vertex
/// positions the app replaces whenever the mesh moves, and what each vertex
/// looks like (a colour, a scalar through a colormap, a point in a texture).
/// The app owns it and changes it between frames; the view reads it when it
/// draws. Normals and bounds follow the positions, recomputed on the next draw.
/// </summary>
public sealed class TriangleMesh
{
    internal readonly int[] Faces;
    internal readonly float[] Positions;
    internal readonly float[] Normals;
    internal readonly float[] Colors;          // straight rgb, 0..1, V × 3
    internal float[]? ScalarColors;            // the scalars through their colormap, V × 3, while set
    internal float[]? Uv;                      // V × 2
    internal uint[]? Texture;                  // straight 0xAARRGGBB
    internal int TextureWidth, TextureHeight;

    // Bumped by every change; what the view's cache compares.
    internal uint Version;
    private uint _normalsFor = uint.MaxValue;
    private Vector3 _min, _max;

    /// <summary>
    /// A mesh of <paramref name="vertexCount"/> vertices, all at the origin and
    /// light grey, and the triangles <paramref name="faces"/> names: three vertex
    /// indices a face, each below <paramref name="vertexCount"/>.
    /// </summary>
    public TriangleMesh(ReadOnlySpan<uint> faces, int vertexCount)
    {
        if (vertexCount < 0) throw new ArgumentOutOfRangeException(nameof(vertexCount));
        if (faces.Length % 3 != 0) throw new ArgumentException($"{faces.Length} indices are not whole triangles: three a face.", nameof(faces));
        Faces = new int[faces.Length];
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i] >= (uint)vertexCount)
                throw new ArgumentException($"Face {i / 3} names vertex {faces[i]}, and there are {vertexCount}.", nameof(faces));
            Faces[i] = (int)faces[i];
        }
        VertexCount = vertexCount;
        Positions = new float[vertexCount * 3];
        Normals = new float[vertexCount * 3];
        Colors = new float[vertexCount * 3];
        Colors.AsSpan().Fill(0.8f);
    }

    public int VertexCount { get; }
    public int FaceCount => Faces.Length / 3;

    /// <summary>Replaces every vertex position: x, y, z a vertex, <see cref="VertexCount"/> × 3 floats. Copied.</summary>
    public void SetPositions(ReadOnlySpan<float> xyz)
    {
        Check(xyz.Length, 3, "positions");
        xyz.CopyTo(Positions);
        Version++;
    }

    /// <summary>Replaces every vertex position. Copied.</summary>
    public void SetPositions(ReadOnlySpan<Vector3> positions) =>
        SetPositions(System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(positions));

    /// <summary>A colour a vertex, r, g, b bytes, <see cref="VertexCount"/> × 3.</summary>
    public void SetColors(ReadOnlySpan<byte> rgb)
    {
        Check(rgb.Length, 3, "colour bytes");
        for (int i = 0; i < rgb.Length; i++) Colors[i] = rgb[i] / 255f;
        Version++;
    }

    /// <summary>A colour a vertex, r, g, b in 0..1, <see cref="VertexCount"/> × 3.</summary>
    public void SetColors(ReadOnlySpan<float> rgb)
    {
        Check(rgb.Length, 3, "colour floats");
        for (int i = 0; i < rgb.Length; i++) Colors[i] = Math.Clamp(rgb[i], 0f, 1f);
        Version++;
    }

    /// <summary>One colour for every vertex.</summary>
    public void SetColor(Rgba color)
    {
        float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
        for (int i = 0; i < Colors.Length; i += 3) (Colors[i], Colors[i + 1], Colors[i + 2]) = (r, g, b);
        Version++;
    }

    /// <summary>
    /// A value a vertex, coloured through <paramref name="colormap"/> (Viridis
    /// when null; see <see cref="Colormaps"/>) from <paramref name="min"/> to
    /// <paramref name="max"/>, clamped. Takes the colours' place until
    /// <see cref="ClearScalars"/>. A NaN draws grey.
    /// </summary>
    public void SetScalars(ReadOnlySpan<float> values, float min, float max, IReadOnlyList<(float Stop, string Color)>? colormap = null)
    {
        Check(values.Length, 1, "scalars");
        var map = Lut(colormap ?? Colormaps.Viridis);
        ScalarColors ??= new float[VertexCount * 3];
        float span = max - min;
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i];
            if (float.IsNaN(v)) { ScalarColors[3 * i] = ScalarColors[3 * i + 1] = ScalarColors[3 * i + 2] = 0.5f; continue; }
            float t = span == 0f ? 0f : Math.Clamp((v - min) / span, 0f, 1f);
            int k = (int)(t * 255f + 0.5f) * 3;
            (ScalarColors[3 * i], ScalarColors[3 * i + 1], ScalarColors[3 * i + 2]) = (map[k], map[k + 1], map[k + 2]);
        }
        Version++;
    }

    /// <summary>Back to the vertex colours.</summary>
    public void ClearScalars()
    {
        ScalarColors = null;
        Version++;
    }

    /// <summary>
    /// The picture texture coordinates point into: RGBA8, straight alpha, rows
    /// packed tight. May be replaced every frame, at any size (a video frame).
    /// It is multiplied by the vertex colour and lit.
    /// </summary>
    public void SetTexture(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"A texture is at least 1×1, not {width}×{height}.");
        if (rgba.Length < width * height * 4) throw new ArgumentException($"{rgba.Length} bytes do not hold a {width}×{height} RGBA8 picture.", nameof(rgba));
        if (Texture is null || Texture.Length != width * height) Texture = new uint[width * height];
        for (int i = 0; i < Texture.Length; i++)
            Texture[i] = (uint)rgba[4 * i + 3] << 24 | (uint)rgba[4 * i] << 16 | (uint)rgba[4 * i + 1] << 8 | rgba[4 * i + 2];
        (TextureWidth, TextureHeight) = (width, height);
        Version++;
    }

    /// <summary>A point in the texture a vertex, u, v: (0, 0) its top-left, (1, 1) its bottom-right. <see cref="VertexCount"/> × 2.</summary>
    public void SetTextureCoordinates(ReadOnlySpan<float> uv)
    {
        Check(uv.Length, 2, "texture coordinates");
        Uv ??= new float[VertexCount * 2];
        uv.CopyTo(Uv);
        Version++;
    }

    /// <summary>Draws without the texture.</summary>
    public void ClearTexture()
    {
        (Texture, Uv, TextureWidth, TextureHeight) = (null, null, 0, 0);
        Version++;
    }

    /// <summary>The box around the current positions.</summary>
    public (Vector3 Min, Vector3 Max) Bounds
    {
        get
        {
            Update();
            return (_min, _max);
        }
    }

    internal bool Textured => Texture is not null && Uv is not null;

    /// <summary>Normals and bounds for the current positions, once a change.</summary>
    internal void Update()
    {
        if (_normalsFor == Version) return;
        _normalsFor = Version;
        var p = Positions;
        var n = Normals;
        Array.Clear(n);
        // Each face adds its unnormalised normal (twice its area) to its three
        // vertices: big faces weigh more, and a sliver does not tip a vertex.
        var f = Faces;
        for (int i = 0; i < f.Length; i += 3)
        {
            int a = f[i] * 3, b = f[i + 1] * 3, c = f[i + 2] * 3;
            float ux = p[b] - p[a], uy = p[b + 1] - p[a + 1], uz = p[b + 2] - p[a + 2];
            float vx = p[c] - p[a], vy = p[c + 1] - p[a + 1], vz = p[c + 2] - p[a + 2];
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            n[a] += nx; n[a + 1] += ny; n[a + 2] += nz;
            n[b] += nx; n[b + 1] += ny; n[b + 2] += nz;
            n[c] += nx; n[c + 1] += ny; n[c + 2] += nz;
        }
        for (int i = 0; i < n.Length; i += 3)
        {
            float len = MathF.Sqrt(n[i] * n[i] + n[i + 1] * n[i + 1] + n[i + 2] * n[i + 2]);
            if (len > 0f) { n[i] /= len; n[i + 1] /= len; n[i + 2] /= len; }
        }
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (int i = 0; i < p.Length; i += 3)
        {
            var v = new Vector3(p[i], p[i + 1], p[i + 2]);
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) continue;
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        if (min.X > max.X) (min, max) = (Vector3.Zero, Vector3.Zero);
        (_min, _max) = (min, max);
    }

    private void Check(int length, int per, string what)
    {
        if (length != VertexCount * per)
            throw new ArgumentException($"{length} {what} for {VertexCount} vertices: {per} a vertex is {VertexCount * per}.");
    }

    // 256 colours along the colormap's stops, rgb 0..1.
    private static readonly Dictionary<object, float[]> s_luts = [];

    private static float[] Lut(IReadOnlyList<(float Stop, string Color)> stops)
    {
        lock (s_luts)
        {
            if (s_luts.TryGetValue(stops, out var hit)) return hit;
            var lut = new float[256 * 3];
            for (int k = 0; k < 256; k++)
            {
                float t = k / 255f;
                int j = 0;
                while (j < stops.Count - 2 && t > stops[j + 1].Stop) j++;
                var a = Rgba.Parse(stops[j].Color);
                var b = Rgba.Parse(stops[Math.Min(j + 1, stops.Count - 1)].Color);
                float s0 = stops[j].Stop, s1 = stops[Math.Min(j + 1, stops.Count - 1)].Stop;
                float u = s1 > s0 ? Math.Clamp((t - s0) / (s1 - s0), 0f, 1f) : 0f;
                lut[3 * k] = (a.R + (b.R - a.R) * u) / 255f;
                lut[3 * k + 1] = (a.G + (b.G - a.G) * u) / 255f;
                lut[3 * k + 2] = (a.B + (b.B - a.B) * u) / 255f;
            }
            return s_luts[stops] = lut;
        }
    }
}
