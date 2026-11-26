using System.Diagnostics;
using System.Numerics;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.Theming;
using Bibui.Raster.Managed.Backend;

/// <summary>
/// Mesh3D: a synthetic sphere and a wavy surface drawn headless through the
/// renderers, the orbit camera driven by real pointer events, and a frame time
/// at 100k vertices. Pictures go to $BIBUI_MESH3D_OUT when it is set.
/// </summary>
static class Mesh3DTests
{
    const int W = 320, H = 240;

    static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance) throw new Exception($"{message}: expected {expected} ± {tolerance} but got {actual}");
    }

    static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // ── Synthetic meshes ─────────────────────────────────────────────────────

    /// <summary>A UV sphere of radius r at the origin, faces wound counter-clockwise seen from outside.</summary>
    internal static (float[] Xyz, uint[] Faces) Sphere(int rings, int segments, float r = 1f)
    {
        var xyz = new List<float>();
        for (int i = 0; i <= rings; i++)
        {
            float th = MathF.PI * i / rings;
            for (int j = 0; j <= segments; j++)
            {
                float ph = 2f * MathF.PI * j / segments;
                xyz.AddRange([r * MathF.Sin(th) * MathF.Cos(ph), r * MathF.Cos(th), r * MathF.Sin(th) * MathF.Sin(ph)]);
            }
        }
        var faces = new List<uint>();
        int row = segments + 1;
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < segments; j++)
            {
                uint a = (uint)(i * row + j), b = a + 1, c = (uint)((i + 1) * row + j), d = c + 1;
                faces.AddRange([a, b, c, b, d, c]);
            }
        return (xyz.ToArray(), faces.ToArray());
    }

    /// <summary>An n × n grid over [-1, 1]² in x and z, height y a moving ripple at time t.</summary>
    internal static uint[] GridFaces(int n)
    {
        var faces = new uint[(n - 1) * (n - 1) * 6];
        int k = 0;
        for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < n - 1; j++)
            {
                uint a = (uint)(i * n + j), b = a + 1, c = (uint)((i + 1) * n + j), d = c + 1;
                faces[k++] = a; faces[k++] = c; faces[k++] = b;
                faces[k++] = b; faces[k++] = c; faces[k++] = d;
            }
        return faces;
    }

    internal static void Wave(float[] xyz, int n, float t)
    {
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float x = -1f + 2f * j / (n - 1), z = -1f + 2f * i / (n - 1);
                float r = MathF.Sqrt(x * x + z * z);
                int v = 3 * (i * n + j);
                (xyz[v], xyz[v + 1], xyz[v + 2]) = (x, 0.12f * MathF.Sin(9f * r - 3f * t) * MathF.Exp(-r), z);
            }
    }

    // ── A view in a test runtime ─────────────────────────────────────────────

    sealed class Lab
    {
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();
        public readonly TriangleMesh Mesh;
        public readonly Mesh3DOptions Options;
        public Mesh3DWidget Last;

        public Lab(TriangleMesh mesh, Mesh3DOptions options, float scale = 1f)
        {
            Mesh = mesh;
            Options = options;
            Runtime.SetBuild(() =>
            {
                var page = Runtime.BoxFromId("page").Sized(Size.Fill, Size.Fill);
                using (Runtime.Parent(page)) Last = Runtime.Mesh3D("view", Mesh, Options);
                return page;
            }, ThemeRegistry.Load("bibui-dark"), TestBuild.Window(W, H, scale));
            Runtime.Step();
            Runtime.Step();   // laid out: the view has its rect
        }

        public void Frame(params WindowEvent[] events) => Runtime.Step([.. events]);
    }

    static TriangleMesh SphereMesh(int rings = 48, int segments = 96)
    {
        var (xyz, faces) = Sphere(rings, segments);
        var mesh = new TriangleMesh(faces, xyz.Length / 3);
        mesh.SetPositions(xyz);
        mesh.SetColor(new Rgba(220, 120, 100));
        return mesh;
    }

    static void Save(Pixels pixels, string name)
    {
        var dir = Environment.GetEnvironmentVariable("BIBUI_MESH3D_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), pixels.PngBytes);
    }

    static (byte B, byte G, byte R, byte A) At(Pixels p, int x, int y)
    {
        int i = (y * p.Width + x) * 4;
        return (p.RawBgraBytes[i], p.RawBgraBytes[i + 1], p.RawBgraBytes[i + 2], p.RawBgraBytes[i + 3]);
    }

    // Pixels that differ from the background colour at the corner.
    static int Covered(Pixels p)
    {
        var bg = At(p, 0, 0);
        int n = 0;
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
                if (At(p, x, y) != bg) n++;
        return n;
    }

    static double Angle(Vector3 a, Vector3 b) =>
        Math.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * 180.0 / Math.PI;

    public static void Register(Action<string, Action> run)
    {
        run("Mesh3D: a sphere's normals point out of it", () =>
        {
            var mesh = SphereMesh(16, 32);
            mesh.Update();
            for (int v = 40; v < mesh.VertexCount - 40; v += 7)
            {
                var p = new Vector3(mesh.Positions[3 * v], mesh.Positions[3 * v + 1], mesh.Positions[3 * v + 2]);
                var n = new Vector3(mesh.Normals[3 * v], mesh.Normals[3 * v + 1], mesh.Normals[3 * v + 2]);
                Near(1.0, Vector3.Dot(Vector3.Normalize(p), n), 0.01, $"vertex {v}'s normal along its radius");
            }
        });

        run("Mesh3D: the faces are checked against the vertex count", () =>
        {
            bool threw = false;
            try { _ = new TriangleMesh([0u, 1u, 3u], 3); } catch (ArgumentException) { threw = true; }
            True(threw, "an index past the vertices throws");
        });

        run("Mesh3D: a sphere fitted in view fills a disc of the size the camera says, the same on Skia and managed", () =>
        {
            var lab = new Lab(SphereMesh(), new Mesh3DOptions());
            var skia = lab.Runtime.Frame();
            using var managed = new ManagedRenderer();
            var mine = lab.Runtime.LastFrame(managed.Draw);
            Save(skia, "sphere-front-skia");
            Save(mine, "sphere-front-managed");
            // Fit puts the unit sphere's bounding sphere (radius √3) at 1.05/sin(22.5°) of it: the unit
            // sphere's silhouette is a disc of angular radius asin(1/d) in a 45° view.
            double d = Math.Sqrt(3) / Math.Sin(22.5 * Math.PI / 180) * 1.05;
            double fpx = H / 2.0 / Math.Tan(22.5 * Math.PI / 180);
            double rpx = Math.Tan(Math.Asin(1 / d)) * fpx;
            Near(Math.PI * rpx * rpx, Covered(skia), Math.PI * rpx * rpx * 0.04, "the disc's area in pixels");
            var centre = At(skia, W / 2, H / 2);
            True(centre.R > 180 && centre.G > 90 && centre.R > centre.B, $"the centre is lit and red-ish, not {centre}");
            // The rim is lit at a grazing angle: darker than the middle, but the ambient light keeps it.
            var rim = At(skia, W / 2 + (int)(rpx * 0.92), H / 2);
            True(rim.R < centre.R && rim.R > 40, $"the rim {rim} is darker than the centre {centre}, and not black");
            int diff = 0;
            for (int i = 0; i < skia.RawBgraBytes.Length; i++) diff = Math.Max(diff, Math.Abs(skia.RawBgraBytes[i] - mine.RawBgraBytes[i]));
            True(diff <= 2, $"Skia and the managed rasteriser draw the same pixels, but differ by {diff}");
        });

        run("Mesh3D: from several orbit angles the sphere stays a centred disc", () =>
        {
            var lab = new Lab(SphereMesh(), new Mesh3DOptions { Wireframe = true });
            var home = lab.Runtime.Mesh3DCamera("view")!.Value;
            int baseline = Covered(lab.Runtime.Frame());
            foreach (var (yaw, pitch) in new[] { (0f, 0f), (45f, 0f), (90f, 30f), (200f, -60f) })
            {
                lab.Runtime.SetMesh3DCamera("view", home.Orbit(yaw, pitch));
                var p = lab.Runtime.Frame();
                Save(p, $"sphere-wire-yaw{yaw}-pitch{pitch}");
                Near(baseline, Covered(p), baseline * 0.03, $"the disc at yaw {yaw}, pitch {pitch}");
                True(At(p, W / 2, H / 2).A == 255 && At(p, W / 2, H / 2) != At(p, 0, 0), "the centre is mesh");
            }
        });

        run("Mesh3D: a wavy surface moves when its positions are replaced, and is not drawn again when nothing changed", () =>
        {
            const int n = 120;
            var xyz = new float[n * n * 3];
            Wave(xyz, n, 0f);
            var mesh = new TriangleMesh(GridFaces(n), n * n);
            mesh.SetPositions(xyz);
            var colours = new float[n * n];
            for (int i = 0; i < colours.Length; i++) colours[i] = xyz[3 * i + 1];
            mesh.SetScalars(colours, -0.12f, 0.12f, Colormaps.Turbo);
            var options = new Mesh3DOptions
            {
                Home = new OrbitCamera(new Vector3(0f, 1.6f, 2.4f), Vector3.Zero, Vector3.UnitY),
                Overlay = new MeshOverlay(),
            };
            options.Overlay.LineStrip([new(-1f, 0f, -1f), new(1f, 0f, -1f), new(1f, 0f, 1f), new(-1f, 0f, 1f), new(-1f, 0f, -1f)], new Rgba(255, 255, 255), 2f);
            options.Overlay.Points([new(0f, 0.3f, 0f)], new Rgba(255, 60, 60), 10f);
            var lab = new Lab(mesh, options);
            var first = lab.Runtime.Frame();
            Save(first, "wave-t0");
            // No cracks: every pixel well inside the surface is the surface, none the background
            // showing through between two faces that share an edge.
            var bg = At(first, 0, 0);
            int holes = 0;
            for (int y = H / 2 - 20; y < H / 2 + 50; y++)
                for (int x = W / 2 - 90; x < W / 2 + 90; x++)
                    if (At(first, x, y) == bg) holes++;
            True(holes == 0, $"{holes} pixels inside the surface show the background through cracks");
            uint drawn = ImageVersion(lab);
            lab.Runtime.Frame();
            True(ImageVersion(lab) == drawn, "an unchanged frame draws the mesh again");
            Wave(xyz, n, 1f);
            mesh.SetPositions(xyz);
            var second = lab.Runtime.Frame();
            Save(second, "wave-t1");
            True(ImageVersion(lab) != drawn, "new positions draw the mesh again");
            int changed = 0;
            for (int i = 0; i < first.RawBgraBytes.Length; i += 4) if (first.RawBgraBytes[i + 2] != second.RawBgraBytes[i + 2]) changed++;
            True(changed > 2000, $"the ripple moved: {changed} pixels changed");
            // The overlay dot is drawn over the surface at its projection.
            True(MeshRasterizer.Project(options.Home.Value, new(0f, 0.3f, 0f), W, H, out var dot, out _), "the dot is in front of the eye");
            var red = At(first, (int)dot.X, (int)dot.Y);
            True(red.R > 200 && red.G < 120, $"the overlay dot is red, not {red}");
        });

        run("Mesh3D: y-down axes, an eye at the origin looking down +z, draw -y at the top", () =>
        {
            // One triangle above the axis in OpenCV terms (y negative), 100 mm ahead.
            var mesh = new TriangleMesh([0u, 1u, 2u], 3);
            mesh.SetPositions([-10f, -30f, 100f, 10f, -30f, 100f, 0f, -10f, 100f]);
            var lab = new Lab(mesh, new Mesh3DOptions { Home = new OrbitCamera(Vector3.Zero, new Vector3(0f, 0f, 100f), -Vector3.UnitY) });
            var p = lab.Runtime.Frame();
            Save(p, "opencv-triangle");
            int top = 0, bottom = 0;
            var bg = At(p, 0, 0);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (At(p, x, y) != bg) { if (y < H / 2) top++; else bottom++; }
            True(top > 50 && bottom == 0, $"the triangle is above the middle: {top} pixels above, {bottom} below");
            // And x to the right: a point at +x lands right of the middle.
            True(MeshRasterizer.Project(lab.Last.Camera, new(10f, 0f, 100f), W, H, out var s, out _) && s.X > W / 2f, "+x is to the right");
        });

        run("Mesh3D: left-drag orbits, right-drag pans, the wheel zooms, a double-click goes home (real pointer events)", () =>
        {
            var lab = new Lab(SphereMesh(16, 32), new Mesh3DOptions());
            var home = lab.Last.Camera;
            float cx = W / 2f, cy = H / 2f;
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseMove, cx, cy, MouseButton.Unknown), WindowEvent.Mouse(WindowEventKind.MouseDown, cx, cy, MouseButton.Left));
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseMove, cx + 50f, cy, MouseButton.Left));
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseMove, cx + 100f, cy, MouseButton.Left));
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseUp, cx + 100f, cy, MouseButton.Left));
            var orbited = lab.Last.Camera;
            Near(100 * Mesh3DExtensions.OrbitDegreesPerPixel, Angle(home.Eye - home.Target, orbited.Eye - orbited.Target), 0.05, "degrees turned by a 100 px drag across");
            Near(home.Distance, orbited.Distance, 1e-3, "the distance an orbit keeps");
            True(orbited.Target == home.Target, "an orbit keeps the target");
            // Dragging up tilts the eye down toward the pole, and stops short of it.
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseDown, cx, cy, MouseButton.Left));
            for (int k = 1; k <= 10; k++) lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseMove, cx, cy + 60f * k, MouseButton.Left));
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseUp, cx, cy + 600f, MouseButton.Left));
            var tilted = lab.Last.Camera;
            double fromUp = Angle(tilted.Eye - tilted.Target, Vector3.UnitY);
            Near(1.0, fromUp, 0.01, "the pitch stops 1° short of the pole");

            // Right-drag: the target moves, the view's direction does not.
            var before = lab.Last.Camera;
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseDown, cx, cy, MouseButton.Right));
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseMove, cx + 40f, cy, MouseButton.Right));
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseUp, cx + 40f, cy, MouseButton.Right));
            var panned = lab.Last.Camera;
            double perPixel = 2 * before.Distance * Math.Tan(before.FovY * Math.PI / 360) / H;
            Near(40 * perPixel, Vector3.Distance(before.Target, panned.Target), 1e-3, "the target moves a pixel's worth a pixel");
            Near(0, Angle(before.Forward, panned.Forward), 1e-3, "a pan does not turn the view");

            // The wheel: a notch up (away from the user) is 10 % closer.
            lab.Frame(WindowEvent.Wheel(cx, cy, 0f, 1f));
            Near(panned.Distance * 0.9, lab.Last.Camera.Distance, 1e-3, "one notch zooms in by a tenth");
            lab.Frame(WindowEvent.Wheel(cx, cy, 0f, -2f));
            Near(panned.Distance * 0.9 / 0.81, lab.Last.Camera.Distance, 1e-3, "two back zoom out");

            // A double-click: two presses at one spot, quickly.
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseDown, cx, cy, MouseButton.Left));
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseUp, cx, cy, MouseButton.Left));
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseDown, cx, cy, MouseButton.Left));
            lab.Frame(WindowEvent.Mouse(WindowEventKind.MouseUp, cx, cy, MouseButton.Left));
            True(lab.Last.Camera == home, $"a double-click goes home: {lab.Last.Camera} against {home}");
        });

        run("Mesh3D: a camera set from code is the one drawn, before the first build too", () =>
        {
            var runtime = BibuiRuntime.Create();
            var mesh = SphereMesh(8, 16);
            var cam = new OrbitCamera(new Vector3(0f, 0f, 9f), Vector3.Zero, Vector3.UnitY, 30f);
            runtime.SetMesh3DCamera("v", cam);
            Mesh3DWidget got = default;
            runtime.SetBuild(() => got = runtime.Mesh3D("v", mesh), ThemeRegistry.Load("bibui-dark"), TestBuild.Window(W, H));
            runtime.Step();
            True(got.Camera == cam, "the set camera survives the first build");
            True(runtime.Mesh3DCamera("v") == cam, "and reads back");
        });

        run("Mesh3D: overlay points behind the mesh hide when asked, and show without the mesh", () =>
        {
            var front = new Vector3(0.5f, 0f, 0.87f) * 1.01f;
            var back = new Vector3(-0.5f, 0f, -0.87f) * 1.01f;
            var overlay = new MeshOverlay();
            overlay.Points([front, back], new Rgba(40, 255, 40), 10f);
            var options = new Mesh3DOptions { Home = new OrbitCamera(new Vector3(0f, 0f, 4f), Vector3.Zero, Vector3.UnitY), Overlay = overlay };
            var lab = new Lab(SphereMesh(), options);
            True(MeshRasterizer.Project(options.Home.Value, front, W, H, out var f, out _), "the front point projects");
            True(MeshRasterizer.Project(options.Home.Value, back, W, H, out var b, out _), "the back point projects");
            static bool Green((byte B, byte G, byte R, byte A) c) => c.G > 200 && c.R < 120;
            var all = lab.Runtime.Frame();
            True(Green(At(all, (int)f.X, (int)f.Y)) && Green(At(all, (int)b.X, (int)b.Y)), "without the depth test both show");
            options.OverlayHiddenByMesh = true;
            var tested = lab.Runtime.Frame();
            Save(tested, "overlay-depth-tested");
            True(Green(At(tested, (int)f.X, (int)f.Y)), "the point on the near side shows");
            True(!Green(At(tested, (int)b.X, (int)b.Y)), "the point behind the sphere is hidden");
            options.ShowMesh = false;
            var bare = lab.Runtime.Frame();
            True(Green(At(bare, (int)b.X, (int)b.Y)), "without the mesh nothing hides it");
            True(At(bare, W / 2, H / 2) == At(bare, 0, 0), "and no mesh is drawn");
        });

        run("Mesh3D: supersampled, the outline is partly covered pixels; one sample a pixel, none are", () =>
        {
            static (int Partial, int Covered) Coverage(Bibui.Core.Draw.PixelImage image)
            {
                int partial = 0, covered = 0;
                foreach (uint p in image.Pixels)
                {
                    uint a = p >> 24;
                    if (a > 0 && a < 255) partial++;
                    if (a > 0) covered++;
                }
                return (partial, covered);
            }
            var options = new Mesh3DOptions();
            var lab = new Lab(SphereMesh(), options);
            lab.Runtime.Frame();
            True(lab.Runtime.TryMeshView("view", out var view), "the view is kept");
            var one = Coverage(view.Image);
            options.Supersample = 2;
            var smooth = lab.Runtime.Frame();
            Save(smooth, "sphere-supersampled");
            var two = Coverage(view.Image);
            True(one.Partial == 0, $"one sample a pixel covers each pixel whole, but {one.Partial} are partial");
            True(two.Partial > 100, $"the outline is partly covered: {two.Partial} pixels");
            Near(one.Covered, two.Covered, one.Covered * 0.03, "the disc is as large either way");
        });

        run("Mesh3D: a texture is drawn through the texture coordinates", () =>
        {
            // A quad facing the eye (counter-clockwise from it), its left half red and its right half blue in the texture.
            var mesh = new TriangleMesh([0u, 2u, 1u, 0u, 3u, 2u], 4);
            mesh.SetPositions([-1f, 1f, 0f, 1f, 1f, 0f, 1f, -1f, 0f, -1f, -1f, 0f]);
            mesh.SetColor(Rgba.White);
            mesh.SetTextureCoordinates([0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f]);
            var tex = new byte[4 * 4 * 4];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    int i = 4 * (y * 4 + x);
                    (tex[i], tex[i + 1], tex[i + 2], tex[i + 3]) = x < 2 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255);
                }
            mesh.SetTexture(tex, 4, 4);
            var lab = new Lab(mesh, new Mesh3DOptions { Ambient = 1f, Home = new OrbitCamera(new Vector3(0f, 0f, 4f), Vector3.Zero, Vector3.UnitY) });
            var p = lab.Runtime.Frame();
            Save(p, "textured-quad");
            var left = At(p, W / 2 - 30, H / 2);
            var right = At(p, W / 2 + 30, H / 2);
            True(left.R > 200 && left.B < 40, $"the left half is red, not {left}");
            True(right.B > 200 && right.R < 40, $"the right half is blue, not {right}");
        });

        run("Mesh3D: 100k vertices, positions replaced every frame (time printed, not asserted)", () =>
        {
            const int n = 317;   // 100,489 vertices, 199,712 triangles
            var xyz = new float[n * n * 3];
            var mesh = new TriangleMesh(GridFaces(n), n * n);
            var lab = new Lab(mesh, new Mesh3DOptions { Home = new OrbitCamera(new Vector3(0f, 1.4f, 2.2f), Vector3.Zero, Vector3.UnitY) }, scale: 1f);
            lab.Runtime.SetWindow(TestBuild.Window(1280, 800, 1f));
            for (int f = 0; f < 5; f++) { Wave(xyz, n, f * 0.1f); mesh.SetPositions(xyz); lab.Frame(); }   // warm
            var sw = Stopwatch.StartNew();
            const int frames = 30;
            for (int f = 0; f < frames; f++)
            {
                Wave(xyz, n, f * 0.05f);
                mesh.SetPositions(xyz);
                lab.Frame();   // builds, lays out and paints: the mesh is rasterised in the paint
            }
            // Faces of a few pixels each, many of them meeting on a pixel centre: no cracks either.
            var last = lab.Runtime.LastFrame();
            Save(last, "wave-100k");
            var bg = At(last, 0, 0);
            int holes = 0;
            for (int y = 300; y < 600; y++)
                for (int x = 400; x < 880; x++)
                    if (At(last, x, y) == bg) holes++;
            True(holes == 0, $"{holes} pixels inside the 100k surface show the background through cracks");
            Console.WriteLine($"         100,489 vertices, 199,712 triangles at 1280×800: {sw.Elapsed.TotalMilliseconds / frames:F1} ms a frame (build + normals + raster), {Environment.ProcessorCount} cores");
        });
    }

    static uint ImageVersion(Lab lab) =>
        lab.Runtime.MeshViewImageVersion("view");
}
