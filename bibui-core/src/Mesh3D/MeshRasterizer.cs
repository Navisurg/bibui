using System.Numerics;
using Bibui.Core.Draw;

namespace Bibui.Core;

/// <summary>
/// Draws a <see cref="MeshScene"/> into a <see cref="PixelImage"/> on the CPU:
/// vertices placed by their mesh's transform and projected through an
/// <see cref="OrbitCamera"/>, faces crossing the near plane clipped to it, then
/// triangles filled with a z-buffer (1/depth), colours, normals and texture
/// coordinates interpolated perspective-correct. Each pixel also keeps the face
/// drawn there, for picking and the id buffers. Triangles are sorted into bands
/// of rows, and the bands drawn in parallel; each band owns its pixels, so
/// nothing is shared while they run. Scratch is kept between frames: after the
/// first frame at a size, a frame allocates nothing but the parallel loops' own
/// bookkeeping.
/// </summary>
internal sealed class MeshRasterizer
{
    // ── Per vertex: the scene's meshes one after another, then the vertices clipping made ──
    // Screen position (physical px) and 1/depth, 0 when not in front of the near plane.
    private float[] _sx = [], _sy = [], _iz = [];
    // Camera space: along the view's right, its up, and ahead.
    private float[] _px = [], _py = [], _pz = [];
    // Interpolated across faces, each times 1/depth for perspective-correct
    // interpolation where the vertex is in front of the near plane, and as it is
    // behind it (where only clipping reads it): the colour, lit by the headlight
    // or, under a point light, the surface's own; texture coordinates; the
    // normal in camera space, unit length, drawn per pixel only.
    private float[] _r = [], _g = [], _b = [];
    private float[] _u = [], _v = [];
    private float[] _nx = [], _ny = [], _nz = [];
    private int _vertexCapacity;

    // ── Per triangle: the scene's faces, then those clipping made ──
    private int[] _tri = [];             // three vertex indices a triangle
    private int[] _clipSource = [];      // per triangle clipping made, the scene face it was cut from
    private int[] _box = [];             // per triangle, its pixel box: minY, maxY, minX, maxX (minY > maxY: nothing)
    private int _triCapacity;

    // ── Per mesh, and the scene's faces: what the scene's meshes are, rebuilt when they change ──
    private MeshScene? _topologyOf;
    private uint _topologyVersion;
    private int[] _meshVertexStart = [0], _meshFaceStart = [0];  // one past the last mesh too
    private int[] _faceMesh = [];                                // per scene face, its mesh
    private bool[] _meshVisible = [];
    private int _sceneVertices, _sceneFaces;

    // ── Bands of rows, and the triangles that reach each ──
    private int[] _chunkBand = [], _bandStart = [], _binned = [];

    // ── Per pixel, at the drawn size (supersampled when asked) ──
    private float[] _depth = [];         // 1/depth, 0 where nothing was drawn
    private int[] _face = [];            // scene face + 1, 0 where nothing was drawn
    private float[] _normal = [];        // 3 a pixel, camera space, while normals are asked for
    private int _depthW, _depthH, _ss = 1;
    private uint[] _fine = [];

    // Under a point light, what each pixel's ray alone decides of its light: |ray|^−(n + 3), the ray a unit of depth long.
    private float[] _rayLight = [];
    private (int W, int H, float Fpx, float Spot) _rayLightFor;

    // The camera last drawn with, for picking.
    private Vector3 _eye, _right, _up, _fwd;
    private float _fpx;

    /// <summary>How far outside a face, in pixels, a pixel centre may be and still be drawn by it.</summary>
    private const float EdgeSlack = 0.01f;

    /// <summary>A face is drawn with its other side to the eye this much darker.</summary>
    private const float BackSide = 0.72f;

    /// <summary>Marks a triangle's box as crossing the near plane: it is clipped before it is drawn.</summary>
    private const int ClipMark = int.MaxValue;

    /// <summary>The fewest rows a band has, so a band's setup is worth its triangles.</summary>
    private const int MinBandRows = 8;

    /// <summary>Bands a core: enough that a band of many faces does not hold the others up.</summary>
    private const int BandsPerCore = 4;

    /// <summary>Triangles a chunk while sorting into bands.</summary>
    private const int BinChunk = 16384;

    /// <summary>The most vertices clipping a triangle to one plane can leave.</summary>
    private const int ClipPolygonMax = 4;

    /// <summary>A point's screen position, and its depth along the view's axis, when it lies in front of the near plane.</summary>
    internal static bool Project(in OrbitCamera cam, Vector3 p, int width, int height, out Vector2 screen, out float depth)
    {
        Basis(cam, height, out var eye, out var right, out var up, out var fwd, out float fpx);
        var d = p - eye;
        depth = Vector3.Dot(d, fwd);
        if (!(depth >= cam.NearPlane)) { screen = default; return false; }
        screen = new Vector2(width * 0.5f + Vector3.Dot(d, right) / depth * fpx, height * 0.5f - Vector3.Dot(d, up) / depth * fpx);
        return true;
    }

    private static void Basis(in OrbitCamera cam, int height, out Vector3 eye, out Vector3 right, out Vector3 up, out Vector3 fwd, out float fpx)
    {
        eye = cam.Eye;
        fwd = cam.Forward;
        right = cam.Right;
        up = Vector3.Cross(right, fwd);
        fpx = height * 0.5f / MathF.Tan(Math.Clamp(cam.FovY, 1f, 170f) * 0.5f * MathF.PI / 180f);
    }

    // ── What the last render left, for the view's overlay, picking and MeshBuffers ──

    internal int DrawnWidth => _depthW;
    internal int DrawnHeight => _depthH;
    internal int Supersampling => _ss;
    internal ReadOnlySpan<float> InverseDepth => _depth.AsSpan(0, _depthW * _depthH);
    internal ReadOnlySpan<int> FacePlusOne => _face.AsSpan(0, _depthW * _depthH);
    internal ReadOnlySpan<float> CameraNormals => _normal.AsSpan(0, Math.Min(_normal.Length, 3 * _depthW * _depthH));
    internal ReadOnlySpan<int> FaceMesh => _faceMesh;
    internal ReadOnlySpan<int> MeshFaceStart => _meshFaceStart;
    internal float FocalPixels => _fpx;

    public void Render(MeshScene scene, in OrbitCamera cam, Mesh3DOptions options, PixelImage image, int width, int height, int supersample, bool normals)
    {
        image.Resize(width, height);
        // Supersampled: drawn at ss × ss the pixels, then each block averaged into one.
        int ss = Math.Clamp(supersample, 1, 4);
        _ss = ss;
        int w = image.Width * ss, h = image.Height * ss;
        if (ss > 1) Grow(ref _fine, w * h);
        uint[] px = ss > 1 ? _fine : image.Argb;
        Topology(scene);
        foreach (var mesh in scene.Meshes) mesh.Update();
        Grow(ref _depth, w * h);
        Grow(ref _face, w * h);
        if (normals) Grow(ref _normal, 3 * w * h);
        else _normal = [];

        uint background = options.Background is { } bg ? Premul(bg) : 0u;
        px.AsSpan(0, w * h).Fill(background);
        _depth.AsSpan(0, w * h).Clear();
        _face.AsSpan(0, w * h).Clear();
        if (normals) _normal.AsSpan(0, 3 * w * h).Clear();
        (_depthW, _depthH) = (w, h);
        Basis(cam, h, out _eye, out _right, out _up, out _fwd, out _fpx);
        if (!options.ShowMesh)
        {
            image.Pixels.Fill(background);
            image.Touch();
            return;
        }

        var light = options.Light;
        bool perPixel = light is not null || normals;
        float near = cam.NearPlane;
        float ambient = Math.Clamp(options.Ambient, 0f, 1f);
        float cx = w * 0.5f, cy = h * 0.5f;
        EnsureVertices(_sceneVertices, keep: false);
        Vertices(scene, near, ambient, light is null, perPixel, cx, cy);

        // Scene faces: the pixel box each covers, none, or a mark that it crosses the near plane.
        int nf = _sceneFaces;
        int[] tri = _tri, box = _box, faceMesh = _faceMesh;
        bool[] visible = _meshVisible;
        float[] iz = _iz, ppx = _px, ppy = _py, ppz = _pz;
        bool crossing = false;
        Chunks(nf, 8192, (lo, hi) =>
        {
            for (int f = lo; f < hi; f++)
            {
                if (!visible[faceMesh[f]]) { Empty(box, f); continue; }
                int a = tri[3 * f], b = tri[3 * f + 1], c = tri[3 * f + 2];
                if (iz[a] != 0f && iz[b] != 0f && iz[c] != 0f) { Box(f, a, b, c, w, h); continue; }
                Empty(box, f);
                // Partly in front, and every corner a number: cut to the near plane below.
                if ((iz[a] != 0f || iz[b] != 0f || iz[c] != 0f)
                    && float.IsFinite(ppx[a] + ppy[a] + ppz[a] + ppx[b] + ppy[b] + ppz[b] + ppx[c] + ppy[c] + ppz[c]))
                {
                    box[4 * f] = ClipMark;
                    crossing = true;   // only ever set: the loop's end orders it before the read
                }
            }
        });
        int nt = nf + (crossing ? Clip(near, w, h, cx, cy, _fpx, perPixel) : 0);

        int bands = Bin(nt, h, out int bandRows);
        Fill(scene, options, light, normals, perPixel, ambient, px, w, h, cx, cy, bands, bandRows);
        if (ss > 1) Downsample(px, w, image, ss);
        image.Touch();
    }

    // The scene's faces as one list of vertex indices into the scene's vertices, and the mesh of each face.
    private void Topology(MeshScene scene)
    {
        int meshes = scene.Meshes.Count;
        if (_meshVisible.Length != meshes) _meshVisible = new bool[meshes];
        for (int m = 0; m < meshes; m++) _meshVisible[m] = scene.Visible[m];
        if (ReferenceEquals(_topologyOf, scene) && _topologyVersion == scene.TopologyVersion) return;
        (_topologyOf, _topologyVersion) = (scene, scene.TopologyVersion);
        _meshVertexStart = new int[meshes + 1];
        _meshFaceStart = new int[meshes + 1];
        for (int m = 0; m < meshes; m++)
        {
            _meshVertexStart[m + 1] = _meshVertexStart[m] + scene.Meshes[m].VertexCount;
            _meshFaceStart[m + 1] = _meshFaceStart[m] + scene.Meshes[m].FaceCount;
        }
        _sceneVertices = _meshVertexStart[meshes];
        _sceneFaces = _meshFaceStart[meshes];
        _faceMesh = new int[_sceneFaces];
        _triCapacity = 0;
        EnsureTriangles(_sceneFaces, keep: false);
        int[] tri = _tri, faceMesh = _faceMesh, vs = _meshVertexStart, fs = _meshFaceStart;
        Parallel.For(0, meshes, m =>
        {
            var faces = scene.Meshes[m].Faces;
            int offset = vs[m], first = fs[m];
            for (int i = 0; i < faces.Length; i++) tri[3 * first + i] = faces[i] + offset;
            faceMesh.AsSpan(first, fs[m + 1] - first).Fill(m);
        });
    }

    // Each visible mesh's vertices: placed, into camera space, projected and lit.
    private void Vertices(MeshScene scene, float near, float ambient, bool headlight, bool perPixel, float cx, float cy)
    {
        Vector3 eye = _eye, right = _right, up = _up, fwd = _fwd;
        float fpx = _fpx;
        float[] sx = _sx, sy = _sy, iz = _iz, ppx = _px, ppy = _py, ppz = _pz, rr = _r, gg = _g, bb = _b, uu = _u, vv = _v, nnx = _nx, nny = _ny, nnz = _nz;
        for (int m = 0; m < scene.Meshes.Count; m++)
        {
            if (!_meshVisible[m]) continue;
            var mesh = scene.Meshes[m];
            var model = scene.Transforms[m];
            bool placed = !model.IsIdentity;
            // Normals are carried by the inverse transpose, so a scaled mesh keeps them square to its faces.
            var normalModel = placed && Matrix4x4.Invert(model, out var inverse) ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;
            var colors = mesh.ScalarColors ?? mesh.Colors;
            var pos = mesh.Positions;
            var nrm = mesh.Normals;
            var uv = mesh.Textured ? mesh.Uv : null;
            int start = _meshVertexStart[m];
            Chunks(mesh.VertexCount, 4096, (lo, hi) =>
            {
                for (int k = lo; k < hi; k++)
                {
                    int i = start + k;
                    var p = new Vector3(pos[3 * k], pos[3 * k + 1], pos[3 * k + 2]);
                    var n = new Vector3(nrm[3 * k], nrm[3 * k + 1], nrm[3 * k + 2]);
                    if (placed)
                    {
                        p = Vector3.Transform(p, model);
                        n = Vector3.TransformNormal(n, normalModel);
                        float len = n.Length();
                        if (len > 0f) n /= len;
                    }
                    var d = p - eye;
                    float depth = Vector3.Dot(d, fwd), across = Vector3.Dot(d, right), rise = Vector3.Dot(d, up);
                    (ppx[i], ppy[i], ppz[i]) = (across, rise, depth);
                    // In front of the near plane (and not NaN): attributes over depth. Behind it: as they are.
                    bool front = depth >= near;
                    float inv = front ? 1f / depth : 1f;
                    iz[i] = front ? inv : 0f;
                    if (front)
                    {
                        sx[i] = cx + across * inv * fpx;
                        sy[i] = cy - rise * inv * fpx;
                    }
                    // The headlight: from the eye toward the vertex, both sides lit alike.
                    float shade = headlight ? (ambient + (1f - ambient) * (MathF.Abs(Vector3.Dot(n, d)) / MathF.Max(d.Length(), 1e-12f))) * inv : inv;
                    rr[i] = colors[3 * k] * shade;
                    gg[i] = colors[3 * k + 1] * shade;
                    bb[i] = colors[3 * k + 2] * shade;
                    if (uv is not null) { uu[i] = uv[2 * k] * inv; vv[i] = uv[2 * k + 1] * inv; }
                    if (perPixel) (nnx[i], nny[i], nnz[i]) = (Vector3.Dot(n, right) * inv, Vector3.Dot(n, up) * inv, Vector3.Dot(n, fwd) * inv);
                }
            });
        }
    }

    // A triangle's pixel box: pixel centres at +0.5 inside it, as far out as the edge slack reaches, clamped to the image.
    private void Box(int t, int a, int b, int c, int w, int h)
    {
        float[] sx = _sx, sy = _sy;
        float x0 = MathF.Min(sx[a], MathF.Min(sx[b], sx[c])), x1 = MathF.Max(sx[a], MathF.Max(sx[b], sx[c]));
        float y0 = MathF.Min(sy[a], MathF.Min(sy[b], sy[c])), y1 = MathF.Max(sy[a], MathF.Max(sy[b], sy[c]));
        int minY = (int)MathF.Max(MathF.Ceiling(y0 - 0.5f - EdgeSlack), 0f), maxY = (int)MathF.Min(MathF.Floor(y1 - 0.5f + EdgeSlack), h - 1);
        int minX = (int)MathF.Max(MathF.Ceiling(x0 - 0.5f - EdgeSlack), 0f), maxX = (int)MathF.Min(MathF.Floor(x1 - 0.5f + EdgeSlack), w - 1);
        if (minX > maxX || minY > maxY || !(x1 >= 0f) || x0 > w) { Empty(_box, t); return; }
        _box[4 * t] = minY;
        _box[4 * t + 1] = maxY;
        _box[4 * t + 2] = minX;
        _box[4 * t + 3] = maxX;
    }

    private static void Empty(int[] box, int t)
    {
        box[4 * t] = 1;
        box[4 * t + 1] = 0;
    }

    // Faces crossing the near plane, cut to it: the part in front, as one or two
    // triangles after the scene's faces, through vertices on the plane after the
    // scene's vertices. Returns how many triangles it made.
    private int Clip(float near, int w, int h, float cx, float cy, float fpx, bool perPixel)
    {
        int nf = _sceneFaces, nv = _sceneVertices;
        int made = 0, added = 0;
        Span<int> poly = stackalloc int[ClipPolygonMax];
        Span<int> corner = stackalloc int[3];
        for (int f = 0; f < nf; f++)
        {
            if (_box[4 * f] != ClipMark) continue;
            Empty(_box, f);
            corner[0] = _tri[3 * f]; corner[1] = _tri[3 * f + 1]; corner[2] = _tri[3 * f + 2];
            int n = 0;
            for (int k = 0; k < corner.Length; k++)
            {
                int i = corner[k], j = corner[(k + 1) % corner.Length];
                bool inI = _iz[i] != 0f, inJ = _iz[j] != 0f;
                if (inI) poly[n++] = i;
                if (inI == inJ) continue;
                // Where the edge meets the plane, and everything there in proportion.
                float t = (near - _pz[i]) / (_pz[j] - _pz[i]);
                int v = nv + added++;
                EnsureVertices(v + 1, keep: true);
                float inv = 1f / near;
                Lerp(v, i, j, t, inv, perPixel);
                _pz[v] = near;
                _iz[v] = inv;
                _sx[v] = cx + _px[v] * inv * fpx;
                _sy[v] = cy - _py[v] * inv * fpx;
                poly[n++] = v;
            }
            // A fan from the first corner keeps the face's winding.
            for (int k = 1; k + 1 < n; k++)
            {
                int t = nf + made++;
                EnsureTriangles(t + 1, keep: true);
                (_tri[3 * t], _tri[3 * t + 1], _tri[3 * t + 2]) = (poly[0], poly[k], poly[k + 1]);
                _clipSource[t - nf] = f;
                Box(t, poly[0], poly[k], poly[k + 1], w, h);
            }
        }
        return made;
    }

    // Vertex v, t of the way from i to j: camera-space position, and the attributes, over its depth (1/inv).
    private void Lerp(int v, int i, int j, float t, float inv, bool perPixel)
    {
        float si = _iz[i] != 0f ? 1f / _iz[i] : 1f, sj = _iz[j] != 0f ? 1f / _iz[j] : 1f;   // undo the over-depth
        float L(float[] a) => a[i] * si + (a[j] * sj - a[i] * si) * t;
        _px[v] = _px[i] + (_px[j] - _px[i]) * t;
        _py[v] = _py[i] + (_py[j] - _py[i]) * t;
        _r[v] = L(_r) * inv;
        _g[v] = L(_g) * inv;
        _b[v] = L(_b) * inv;
        _u[v] = L(_u) * inv;
        _v[v] = L(_v) * inv;
        if (perPixel)
        {
            var n = new Vector3(L(_nx), L(_ny), L(_nz));
            float len = n.Length();
            if (len > 0f) n /= len;
            (_nx[v], _ny[v], _nz[v]) = (n.X * inv, n.Y * inv, n.Z * inv);
        }
    }

    // Sorts the triangles into bands of rows: each band's list holds every
    // triangle that reaches one of its rows, in drawing order, so a band reads
    // only its own triangles. Counted a chunk at a time in parallel, then placed.
    private int Bin(int nt, int h, out int bandRows)
    {
        int maxBands = Math.Max(1, Environment.ProcessorCount * BandsPerCore);
        int rows = bandRows = Math.Max(MinBandRows, (h + maxBands - 1) / maxBands);
        int bands = (h + rows - 1) / rows;
        int chunks = Math.Max(1, (nt + BinChunk - 1) / BinChunk);
        Grow(ref _chunkBand, chunks * bands);
        Grow(ref _bandStart, bands + 1);
        var counts = _chunkBand;
        var box = _box;
        counts.AsSpan(0, chunks * bands).Clear();
        Parallel.For(0, chunks, k =>
        {
            int lo = k * BinChunk, hi = Math.Min(nt, lo + BinChunk), row = k * bands;
            for (int t = lo; t < hi; t++)
            {
                int minY = box[4 * t], maxY = box[4 * t + 1];
                if (minY > maxY) continue;
                for (int band = minY / rows, last = maxY / rows; band <= last; band++) counts[row + band]++;
            }
        });
        // Band by band, chunk by chunk: each count becomes where that chunk's triangles start in the band.
        int total = 0;
        for (int band = 0; band < bands; band++)
        {
            _bandStart[band] = total;
            for (int k = 0; k < chunks; k++)
            {
                int n = counts[k * bands + band];
                counts[k * bands + band] = total;
                total += n;
            }
        }
        _bandStart[bands] = total;
        Grow(ref _binned, total);
        var binned = _binned;
        Parallel.For(0, chunks, k =>
        {
            int lo = k * BinChunk, hi = Math.Min(nt, lo + BinChunk), row = k * bands;
            for (int t = lo; t < hi; t++)
            {
                int minY = box[4 * t], maxY = box[4 * t + 1];
                if (minY > maxY) continue;
                for (int band = minY / rows, last = maxY / rows; band <= last; band++) binned[counts[row + band]++] = t;
            }
        });
        return bands;
    }

    // The bands, each filled by one worker from the triangles that reach it.
    private void Fill(MeshScene scene, Mesh3DOptions options, CameraLight? light, bool normals, bool perPixel, float ambient,
        uint[] px, int w, int h, float cx, float cy, int bands, int bandRows)
    {
        int nf = _sceneFaces;
        int[] tri = _tri, box = _box, binned = _binned, bandStart = _bandStart, clipSource = _clipSource, faceMesh = _faceMesh, faceBuf = _face;
        float[] sx = _sx, sy = _sy, iz = _iz, rr = _r, gg = _g, bb = _b, uu = _u, vv = _v, nnx = _nx, nny = _ny, nnz = _nz;
        float[] depthBuf = _depth, normalBuf = _normal;
        bool wire = options.Wireframe;
        var wc = options.WireframeColor;
        float wa = wc.A / 255f, wr = wc.R / 255f, wg = wc.G / 255f, wb = wc.B / 255f;
        int ss = _ss;
        float invF = 1f / _fpx;
        bool lit = light is not null;
        float intensity = light?.Intensity ?? 0f;
        float[] rayLight = lit ? RayLight(w, h, cx, cy, light!.Value.SpotExponent) : [];
        var meshes = scene.Meshes;
        Parallel.For(0, bands, band =>
        {
            int rowLo = band * bandRows, rowHi = Math.Min(h, rowLo + bandRows) - 1;
            for (int k = bandStart[band], end = bandStart[band + 1]; k < end; k++)
            {
                int t = binned[k];
                int y0 = Math.Max(box[4 * t], rowLo), y1 = Math.Min(box[4 * t + 1], rowHi);
                int x0 = box[4 * t + 2], x1 = box[4 * t + 3];
                int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
                int face = t < nf ? t : clipSource[t - nf];
                var mesh = meshes[faceMesh[face]];
                bool textured = mesh.Textured;
                var tex = mesh.Texture;
                int tw = mesh.TextureWidth, th = mesh.TextureHeight;
                float ax = sx[a], ay = sy[a], bx = sx[b], by = sy[b], qx = sx[c], qy = sy[c];
                // Relative to a: products of screen coordinates (a thousand pixels
                // squared) would lose the hundredths of a pixel an edge is decided by.
                float ux = bx - ax, uy = by - ay, vx = qx - ax, vy = qy - ay;
                float area = ux * vy - vx * uy;
                if (area == 0f || !float.IsFinite(area)) continue;
                // Facing away: the winding reads the other way on screen (y down). Darker under
                // the headlight; a point light lights both sides alike, as its formula says.
                float side = area > 0f && !lit ? BackSide : 1f;
                float inv = 1f / area;
                // The weights of b and c are linear in the offset from a; a's is what is left.
                float e1x = vy * inv, e1y = -vx * inv;
                float e2x = -uy * inv, e2y = ux * inv;
                // Pixels from an edge per unit of its weight: a pixel centre a hair
                // outside both faces of a shared edge (rounding) is still drawn, so
                // a surface shows no cracks; the wireframe reads the same distance.
                float aa = MathF.Abs(area);
                float l0 = aa / MathF.Sqrt((qx - bx) * (qx - bx) + (qy - by) * (qy - by));
                float l1 = aa / MathF.Sqrt(vx * vx + vy * vy);
                float l2 = aa / MathF.Sqrt(ux * ux + uy * uy);
                float iza = iz[a], izb = iz[b], izc = iz[c];
                int id = face + 1;
                for (int y = y0; y <= y1; y++)
                {
                    float dy = y + 0.5f - ay;
                    float rayY = -(y + 0.5f - cy) * invF;   // the pixel's camera-space up, a unit of depth ahead
                    int row = y * w;
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = x + 0.5f - ax;
                        float w1 = e1x * dx + e1y * dy;
                        float w2 = e2x * dx + e2y * dy;
                        float w0 = 1f - w1 - w2;
                        if (w0 * l0 < -EdgeSlack || w1 * l1 < -EdgeSlack || w2 * l2 < -EdgeSlack) continue;
                        float z = w0 * iza + w1 * izb + w2 * izc;
                        if (z <= depthBuf[row + x]) continue;
                        depthBuf[row + x] = z;
                        faceBuf[row + x] = id;
                        // Attributes over depth, interpolated on screen and divided by the interpolated 1/depth: right in space.
                        float iw = side / z;
                        float r = (w0 * rr[a] + w1 * rr[b] + w2 * rr[c]) * iw;
                        float g = (w0 * gg[a] + w1 * gg[b] + w2 * gg[c]) * iw;
                        float bl = (w0 * bb[a] + w1 * bb[b] + w2 * bb[c]) * iw;
                        if (textured)
                        {
                            float tu = (w0 * uu[a] + w1 * uu[b] + w2 * uu[c]) / z;
                            float tv = (w0 * vv[a] + w1 * vv[b] + w2 * vv[c]) / z;
                            Sample(tex!, tw, th, tu, tv, out float sr, out float sg, out float sb);
                            r *= sr; g *= sg; bl *= sb;
                        }
                        if (perPixel)
                        {
                            var n = new Vector3(w0 * nnx[a] + w1 * nnx[b] + w2 * nnx[c], w0 * nny[a] + w1 * nny[b] + w2 * nny[c], w0 * nnz[a] + w1 * nnz[b] + w2 * nnz[c]);
                            float len = n.Length();
                            if (len > 0f) n /= len;
                            if (normals) (normalBuf[3 * (row + x)], normalBuf[3 * (row + x) + 1], normalBuf[3 * (row + x) + 2]) = (n.X, n.Y, n.Z);
                            if (lit)
                            {
                                // The point is the ray (a unit of depth long) times the depth 1/z: r = |ray| / z,
                                // cos θ = |n · ray| / |ray| and cos α = 1 / |ray|, so I · cos θ · cosⁿ α / r² is
                                // I · |n · ray| · z² · |ray|^−(n + 3), the last factor the pixel's own.
                                var ray = new Vector3((x + 0.5f - cx) * invF, rayY, 1f);
                                float e = ambient + intensity * MathF.Abs(Vector3.Dot(n, ray)) * z * z * rayLight[row + x];
                                r *= e; g *= e; bl *= e;
                            }
                        }
                        if (wire)
                        {
                            float dist = MathF.Min(w0 * l0, MathF.Min(w1 * l1, w2 * l2));
                            float cov = Math.Clamp(1f - dist / ss, 0f, 1f) * wa;   // a pixel of the image wide, whatever the sampling
                            r += (wr - r) * cov; g += (wg - g) * cov; bl += (wb - bl) * cov;
                        }
                        px[row + x] = 0xFF000000u | (uint)(Math.Clamp(r, 0f, 1f) * 255f + 0.5f) << 16
                            | (uint)(Math.Clamp(g, 0f, 1f) * 255f + 0.5f) << 8 | (uint)(Math.Clamp(bl, 0f, 1f) * 255f + 0.5f);
                    }
                }
            }
        });
    }

    // |ray|^−(n + 3) for each pixel's ray, a unit of depth long: made again only when the size, the field of view or n changes.
    private float[] RayLight(int w, int h, float cx, float cy, float spot)
    {
        var key = (w, h, _fpx, spot);
        if (_rayLightFor == key && _rayLight.Length >= w * h) return _rayLight;
        _rayLightFor = key;
        Grow(ref _rayLight, w * h);
        float[] table = _rayLight;
        float invF = 1f / _fpx, power = -(spot + 3f) * 0.5f;
        Chunks(h, 16, (lo, hi) =>
        {
            for (int y = lo; y < hi; y++)
            {
                float ry = (y + 0.5f - cy) * invF;
                for (int x = 0; x < w; x++)
                {
                    float rx = (x + 0.5f - cx) * invF;
                    table[y * w + x] = MathF.Pow(1f + rx * rx + ry * ry, power);
                }
            }
        });
        return table;
    }

    /// <summary>
    /// What was drawn at <paramref name="point"/>, in the drawn pixels (supersampled
    /// when asked): the face there, and where the ray through the point meets it.
    /// </summary>
    public MeshHit? Pick(Vector2 point)
    {
        int ix = (int)MathF.Floor(point.X), iy = (int)MathF.Floor(point.Y);
        if (ix < 0 || iy < 0 || ix >= _depthW || iy >= _depthH) return null;
        int face = _face[iy * _depthW + ix] - 1;
        if (face < 0) return null;
        int mesh = _faceMesh[face];
        int a = _tri[3 * face], b = _tri[3 * face + 1], c = _tri[3 * face + 2];
        var p0 = new Vector3(_px[a], _py[a], _pz[a]);
        var p1 = new Vector3(_px[b], _py[b], _pz[b]);
        var p2 = new Vector3(_px[c], _py[c], _pz[c]);
        // The ray from the eye through the point, in camera space, a unit of depth long.
        var dir = new Vector3((point.X - _depthW * 0.5f) / _fpx, -(point.Y - _depthH * 0.5f) / _fpx, 1f);
        Vector3 hit, bary;
        // Where the ray meets the face's plane, in the face's own weights.
        var e1 = p1 - p0;
        var e2 = p2 - p0;
        var pv = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, pv);
        if (det != 0f && float.IsFinite(1f / det))
        {
            float inv = 1f / det;
            var s = -p0;
            float u = Vector3.Dot(s, pv) * inv;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(dir, q) * inv;
            hit = dir * (Vector3.Dot(e2, q) * inv);
            bary = new Vector3(1f - u - v, u, v);
        }
        else
        {
            // Edge on to the eye: the depth drawn, and the face's middle.
            hit = dir / _depth[iy * _depthW + ix];
            bary = new Vector3(1f / 3f);
        }
        var world = _eye + _right * hit.X + _up * hit.Y + _fwd * hit.Z;
        return new MeshHit(mesh, face - _meshFaceStart[mesh], bary, world, hit.Z);
    }

    // Each ss × ss block of the fine pixels averaged, premultiplied, into one of the image's.
    private static void Downsample(uint[] fine, int fineWidth, PixelImage image, int ss)
    {
        var dst = image.Argb;
        int w = image.Width;
        int n = ss * ss, half = n / 2;
        Chunks(image.Height, 16, (lo, hi) =>
        {
            for (int y = lo; y < hi; y++)
                for (int x = 0; x < w; x++)
                {
                    uint a = 0, r = 0, g = 0, b = 0;
                    for (int j = 0; j < ss; j++)
                    {
                        int row = (y * ss + j) * fineWidth + x * ss;
                        for (int i = 0; i < ss; i++)
                        {
                            uint p = fine[row + i];
                            a += p >> 24; r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255;
                        }
                    }
                    dst[y * w + x] = (uint)((a + half) / n) << 24 | (uint)((r + half) / n) << 16 | (uint)((g + half) / n) << 8 | (uint)((b + half) / n);
                }
        });
    }

    /// <summary>
    /// Whether a point <paramref name="depth"/> from the eye at <paramref name="screen"/>
    /// is in front of the mesh last drawn there, within <paramref name="tolerance"/>
    /// of its depth. Off the image, or where no face was drawn, it is.
    /// </summary>
    public bool InFront(Vector2 screen, float depth, float tolerance)
    {
        int x = (int)MathF.Floor(screen.X * _ss), y = (int)MathF.Floor(screen.Y * _ss);
        if (x < 0 || y < 0 || x >= _depthW || y >= _depthH) return true;
        float surface = _depth[y * _depthW + x];   // 1/depth, 0 for none
        return surface == 0f || depth * surface <= 1f + tolerance;
    }

    // Bilinear, clamped at the edges; (0, 0) is the top-left corner of the picture.
    private static void Sample(uint[] tex, int tw, int th, float u, float v, out float r, out float g, out float b)
    {
        float fx = Math.Clamp(u, 0f, 1f) * tw - 0.5f, fy = Math.Clamp(v, 0f, 1f) * th - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        int xa = Math.Clamp(x0, 0, tw - 1), xb = Math.Clamp(x0 + 1, 0, tw - 1);
        int ya = Math.Clamp(y0, 0, th - 1) * tw, yb = Math.Clamp(y0 + 1, 0, th - 1) * tw;
        uint p00 = tex[ya + xa], p10 = tex[ya + xb], p01 = tex[yb + xa], p11 = tex[yb + xb];
        float w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
        const float k = 1f / 255f;
        r = (((p00 >> 16) & 255) * w00 + ((p10 >> 16) & 255) * w10 + ((p01 >> 16) & 255) * w01 + ((p11 >> 16) & 255) * w11) * k;
        g = (((p00 >> 8) & 255) * w00 + ((p10 >> 8) & 255) * w10 + ((p01 >> 8) & 255) * w01 + ((p11 >> 8) & 255) * w11) * k;
        b = ((p00 & 255) * w00 + (p10 & 255) * w10 + (p01 & 255) * w01 + (p11 & 255) * w11) * k;
    }

    private static uint Premul(Rgba c)
    {
        uint a = c.A;
        return a << 24 | (uint)(c.R * a / 255) << 16 | (uint)(c.G * a / 255) << 8 | (uint)(c.B * a / 255);
    }

    // Room for n vertices, grown by half again; what is there is kept when asked (clipping appends).
    private void EnsureVertices(int n, bool keep)
    {
        if (n <= _vertexCapacity) return;
        int cap = Math.Max(n, _vertexCapacity + _vertexCapacity / 2);
        _sx = Resized(_sx, cap, keep); _sy = Resized(_sy, cap, keep); _iz = Resized(_iz, cap, keep);
        _px = Resized(_px, cap, keep); _py = Resized(_py, cap, keep); _pz = Resized(_pz, cap, keep);
        _r = Resized(_r, cap, keep); _g = Resized(_g, cap, keep); _b = Resized(_b, cap, keep);
        _u = Resized(_u, cap, keep); _v = Resized(_v, cap, keep);
        _nx = Resized(_nx, cap, keep); _ny = Resized(_ny, cap, keep); _nz = Resized(_nz, cap, keep);
        _vertexCapacity = cap;
    }

    // Room for n triangles, with their boxes and, past the scene's faces, where each was cut from.
    private void EnsureTriangles(int n, bool keep)
    {
        if (n <= _triCapacity) return;
        int cap = Math.Max(n, _triCapacity + _triCapacity / 2);
        _tri = Resized(_tri, 3 * cap, keep);
        _box = Resized(_box, 4 * cap, keep);
        _clipSource = Resized(_clipSource, cap - _sceneFaces, keep);
        _triCapacity = cap;
    }

    private static T[] Resized<T>(T[] a, int n, bool keep)
    {
        if (a.Length >= n) return a;
        var b = new T[n];
        if (keep) a.CopyTo(b, 0);
        return b;
    }

    private static void Grow<T>(ref T[] array, int n)
    {
        if (array.Length < n) array = new T[n];
    }

    // A loop over [0, n) in chunks, in parallel when there is enough of it.
    private static void Chunks(int n, int chunk, Action<int, int> body)
    {
        if (n <= chunk) { body(0, n); return; }
        int count = (n + chunk - 1) / chunk;
        Parallel.For(0, count, k => body(k * chunk, Math.Min(n, (k + 1) * chunk)));
    }
}
