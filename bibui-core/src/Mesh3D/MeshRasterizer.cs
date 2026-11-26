using System.Numerics;
using Bibui.Core.Draw;

namespace Bibui.Core;

/// <summary>
/// Draws a <see cref="TriangleMesh"/> into a <see cref="PixelImage"/> on the
/// CPU: vertices projected through an <see cref="OrbitCamera"/> and lit by a
/// headlight, then triangles filled with a z-buffer (1/depth), colours and
/// texture coordinates interpolated perspective-correct. Rows are split into
/// bands drawn in parallel; each band owns its pixels, so nothing is shared
/// while they run. Scratch is kept between frames: after the first frame at a
/// size, a frame allocates nothing but the parallel loop's own bookkeeping.
/// </summary>
internal sealed class MeshRasterizer
{
    // Per vertex: screen position (physical px), 1/depth (0 when behind the
    // eye), and lit colour premultiplied by 1/depth for perspective-correct
    // interpolation; texture coordinates likewise.
    private float[] _sx = [], _sy = [], _iz = [], _r = [], _g = [], _b = [], _u = [], _v = [];
    // Per face: its pixel box, rows minY..maxY (minY > maxY when it draws nothing).
    private int[] _box = [];
    private float[] _depth = [];

    /// <summary>The near plane, as a share of the camera distance: what is closer to the eye is not drawn.</summary>
    private const float NearShare = 1e-3f;

    /// <summary>How far outside a face, in pixels, a pixel centre may be and still be drawn by it.</summary>
    private const float EdgeSlack = 0.01f;

    /// <summary>A pixel's centre to screen point, and whether it lies in front of the eye.</summary>
    internal static bool Project(in OrbitCamera cam, Vector3 p, int width, int height, out Vector2 screen, out float depth)
    {
        Basis(cam, height, out var eye, out var right, out var up, out var fwd, out float fpx);
        var d = p - eye;
        depth = Vector3.Dot(d, fwd);
        float near = NearShare * MathF.Max(cam.Distance, 1e-6f);
        if (depth < near) { screen = default; return false; }
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

    public void Render(TriangleMesh mesh, in OrbitCamera cam, Mesh3DOptions options, PixelImage image, int width, int height)
    {
        image.Resize(width, height);
        int w = image.Width, h = image.Height;
        mesh.Update();
        int nv = mesh.VertexCount, nf = mesh.FaceCount;
        Grow(ref _sx, nv); Grow(ref _sy, nv); Grow(ref _iz, nv);
        Grow(ref _r, nv); Grow(ref _g, nv); Grow(ref _b, nv);
        bool textured = mesh.Textured;
        if (textured) { Grow(ref _u, nv); Grow(ref _v, nv); }
        Grow(ref _box, nf * 4);
        Grow(ref _depth, w * h);

        uint background = options.Background is { } bg ? Premul(bg) : 0u;
        image.Pixels.Fill(background);
        _depth.AsSpan(0, w * h).Clear();

        Basis(cam, h, out var eye, out var right, out var up, out var fwd, out float fpx);
        float near = NearShare * MathF.Max(cam.Distance, 1e-6f);
        float ambient = Math.Clamp(options.Ambient, 0f, 1f);
        float cx = w * 0.5f, cy = h * 0.5f;
        var colors = mesh.ScalarColors ?? mesh.Colors;
        var pos = mesh.Positions;
        var nrm = mesh.Normals;
        var uv = mesh.Uv;
        float[] sx = _sx, sy = _sy, iz = _iz, rr = _r, gg = _g, bb = _b, uu = _u, vv = _v;

        // Vertices: project, light, and premultiply the attributes by 1/depth.
        Chunks(nv, 4096, (lo, hi) =>
        {
            for (int i = lo; i < hi; i++)
            {
                var p = new Vector3(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]);
                var d = p - eye;
                float depth = Vector3.Dot(d, fwd);
                if (!(depth >= near)) { iz[i] = 0f; continue; }   // behind the near plane, or NaN
                float inv = 1f / depth;
                iz[i] = inv;
                sx[i] = cx + Vector3.Dot(d, right) * inv * fpx;
                sy[i] = cy - Vector3.Dot(d, up) * inv * fpx;
                // The headlight: from the eye toward the vertex, both sides lit alike.
                var n = new Vector3(nrm[3 * i], nrm[3 * i + 1], nrm[3 * i + 2]);
                float lambert = MathF.Abs(Vector3.Dot(n, d)) / MathF.Max(d.Length(), 1e-12f);
                float shade = (ambient + (1f - ambient) * lambert) * inv;
                rr[i] = colors[3 * i] * shade;
                gg[i] = colors[3 * i + 1] * shade;
                bb[i] = colors[3 * i + 2] * shade;
                if (textured) { uu[i] = uv![2 * i] * inv; vv[i] = uv[2 * i + 1] * inv; }
            }
        });

        // Faces: the pixel box each covers, or none.
        var faces = mesh.Faces;
        int[] box = _box;
        Chunks(nf, 8192, (lo, hi) =>
        {
            for (int f = lo; f < hi; f++)
            {
                int a = faces[3 * f], b = faces[3 * f + 1], c = faces[3 * f + 2];
                ref int b0 = ref box[4 * f];
                if (iz[a] == 0f || iz[b] == 0f || iz[c] == 0f) { b0 = 1; box[4 * f + 1] = 0; continue; }
                float x0 = MathF.Min(sx[a], MathF.Min(sx[b], sx[c])), x1 = MathF.Max(sx[a], MathF.Max(sx[b], sx[c]));
                float y0 = MathF.Min(sy[a], MathF.Min(sy[b], sy[c])), y1 = MathF.Max(sy[a], MathF.Max(sy[b], sy[c]));
                // Pixel centres at +0.5 inside the box; clamped to the image.
                int minY = (int)MathF.Max(MathF.Ceiling(y0 - 0.5f), 0f), maxY = (int)MathF.Min(MathF.Floor(y1 - 0.5f), h - 1);
                int minX = (int)MathF.Max(MathF.Ceiling(x0 - 0.5f), 0f), maxX = (int)MathF.Min(MathF.Floor(x1 - 0.5f), w - 1);
                if (minX > maxX || !(x1 >= 0f) || x0 > w) { b0 = 1; box[4 * f + 1] = 0; continue; }
                b0 = minY;
                box[4 * f + 1] = maxY;
                box[4 * f + 2] = minX;
                box[4 * f + 3] = maxX;
            }
        });

        // Bands of rows, each filled by one worker from every face that reaches it.
        var px = image.Argb;
        var depthBuf = _depth;
        bool wire = options.Wireframe;
        var wc = options.WireframeColor;
        float wa = wc.A / 255f, wr = wc.R / 255f, wg = wc.G / 255f, wb = wc.B / 255f;
        var tex = mesh.Texture;
        int tw = mesh.TextureWidth, th = mesh.TextureHeight;
        int bands = Math.Clamp(h / 8, 1, Environment.ProcessorCount * 4);
        Parallel.For(0, bands, band =>
        {
            int rowLo = (int)((long)h * band / bands), rowHi = (int)((long)h * (band + 1) / bands) - 1;
            for (int f = 0; f < nf; f++)
            {
                int minY = box[4 * f], maxY = box[4 * f + 1];
                if (maxY < rowLo || minY > rowHi || minY > maxY) continue;
                int y0 = Math.Max(minY, rowLo), y1 = Math.Min(maxY, rowHi);
                int x0 = box[4 * f + 2], x1 = box[4 * f + 3];
                int a = faces[3 * f], b = faces[3 * f + 1], c = faces[3 * f + 2];
                float ax = sx[a], ay = sy[a], bx = sx[b], by = sy[b], qx = sx[c], qy = sy[c];
                float area = (bx - ax) * (qy - ay) - (qx - ax) * (by - ay);
                if (area == 0f || !float.IsFinite(area)) continue;
                // Facing away: the winding reads the other way on screen (y down). Darker.
                float side = area > 0f ? 0.72f : 1f;
                float inv = 1f / area;
                // Edge functions, each 0 on an edge and positive inside once signed by the area:
                // e0 opposite a, e1 opposite b, e2 opposite c.
                float e0x = (by - qy) * inv, e0y = (qx - bx) * inv, e0c = (bx * qy - qx * by) * inv;
                float e1x = (qy - ay) * inv, e1y = (ax - qx) * inv, e1c = (qx * ay - ax * qy) * inv;
                float e2x = (ay - by) * inv, e2y = (bx - ax) * inv, e2c = (ax * by - bx * ay) * inv;
                // Pixels from an edge per unit of its weight: a pixel centre a hair
                // outside both faces of a shared edge (rounding) is still drawn, so
                // a surface shows no cracks; the wireframe reads the same distance.
                float l0 = 1f / MathF.Sqrt(e0x * e0x + e0y * e0y);
                float l1 = 1f / MathF.Sqrt(e1x * e1x + e1y * e1y);
                float l2 = 1f / MathF.Sqrt(e2x * e2x + e2y * e2y);
                float iza = iz[a], izb = iz[b], izc = iz[c];
                for (int y = y0; y <= y1; y++)
                {
                    float fy = y + 0.5f;
                    int row = y * w;
                    for (int x = x0; x <= x1; x++)
                    {
                        float fx = x + 0.5f;
                        float w0 = e0x * fx + e0y * fy + e0c;
                        float w1 = e1x * fx + e1y * fy + e1c;
                        float w2 = 1f - w0 - w1;
                        if (w0 * l0 < -EdgeSlack || w1 * l1 < -EdgeSlack || w2 * l2 < -EdgeSlack) continue;
                        float z = w0 * iza + w1 * izb + w2 * izc;
                        if (z <= depthBuf[row + x]) continue;
                        depthBuf[row + x] = z;
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
                        if (wire)
                        {
                            float dist = MathF.Min(w0 * l0, MathF.Min(w1 * l1, w2 * l2));
                            float cov = Math.Clamp(1f - dist, 0f, 1f) * wa;
                            r += (wr - r) * cov; g += (wg - g) * cov; bl += (wb - bl) * cov;
                        }
                        px[row + x] = 0xFF000000u | (uint)(Math.Clamp(r, 0f, 1f) * 255f + 0.5f) << 16
                            | (uint)(Math.Clamp(g, 0f, 1f) * 255f + 0.5f) << 8 | (uint)(Math.Clamp(bl, 0f, 1f) * 255f + 0.5f);
                    }
                }
            }
        });
        image.Touch();
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
