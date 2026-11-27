using System.Diagnostics;
using System.Numerics;
using Bibui.Core;
using Bibui.Core.Imaging;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.Theming;

/// <summary>
/// Mesh3D scenes: several meshes placed by transforms, a camera from a pose, the
/// endoscope's point light, near-plane clipping, picking and the offscreen
/// buffers. Expectations are worked out from the geometry here, not recorded:
/// the depth where a pixel's ray meets a plane, the light 1/r² gives. Pictures
/// go to $BIBUI_MESH3D_OUT when it is set.
/// </summary>
static class Mesh3DSceneTests
{
    const int W = 320, H = 240;

    static void Near(double expected, double actual, double tolerance, string message)
    {
        if (!(Math.Abs(expected - actual) <= tolerance)) throw new Exception($"{message}: expected {expected} ± {tolerance} but got {actual}");
    }

    static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    /// <summary>A square of side 2·half in its own z = 0 plane, centred on the origin: two faces, (0, 1, 2) and (0, 2, 3).</summary>
    static TriangleMesh Square(float half, Rgba color)
    {
        var mesh = new TriangleMesh([0u, 1u, 2u, 0u, 2u, 3u], 4);
        mesh.SetPositions([-half, -half, 0f, half, -half, 0f, half, half, 0f, -half, half, 0f]);
        mesh.SetColor(color);
        return mesh;
    }

    /// <summary>
    /// A tube of <paramref name="radius"/> along the polyline <paramref name="path"/>,
    /// <paramref name="sides"/> vertices round each ring: what an app sweeps a vessel's centreline into.
    /// </summary>
    internal static TriangleMesh Tube(ReadOnlySpan<Vector3> path, float radius, int sides)
    {
        int rings = path.Length;
        var xyz = new float[rings * sides * 3];
        var faces = new uint[(rings - 1) * sides * 6];
        var side = Vector3.UnitX;
        for (int i = 0; i < rings; i++)
        {
            var along = Vector3.Normalize(path[Math.Min(i + 1, rings - 1)] - path[Math.Max(i - 1, 0)]);
            // Carried along the path, so the rings do not twist.
            side = Vector3.Normalize(side - along * Vector3.Dot(side, along));
            if (!float.IsFinite(side.X)) side = Vector3.Normalize(Vector3.Cross(along, Vector3.UnitY));
            var other = Vector3.Cross(along, side);
            for (int k = 0; k < sides; k++)
            {
                float a = 2f * MathF.PI * k / sides;
                var p = path[i] + radius * (MathF.Cos(a) * side + MathF.Sin(a) * other);
                int v = 3 * (i * sides + k);
                (xyz[v], xyz[v + 1], xyz[v + 2]) = (p.X, p.Y, p.Z);
            }
        }
        int f = 0;
        for (int i = 0; i + 1 < rings; i++)
            for (int k = 0; k < sides; k++)
            {
                uint a = (uint)(i * sides + k), b = (uint)(i * sides + (k + 1) % sides), c = a + (uint)sides, d = b + (uint)sides;
                (faces[f], faces[f + 1], faces[f + 2], faces[f + 3], faces[f + 4], faces[f + 5]) = (a, b, c, b, d, c);
                f += 6;
            }
        var mesh = new TriangleMesh(faces, rings * sides);
        mesh.SetPositions(xyz);
        return mesh;
    }

    // A pose that is not the identity: the camera somewhere, turned about all three axes.
    static readonly Vector3 PosePosition = new(12f, -7f, 30f);
    static readonly Quaternion PoseOrientation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.4f, -0.3f, 0.2f));

    /// <summary>What places a mesh in the pose camera's own axes (x right, y down, z ahead) in the world.</summary>
    static Matrix4x4 InCamera(Vector3 offset, Quaternion turn) =>
        Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(offset) * Matrix4x4.CreateFromQuaternion(PoseOrientation) * Matrix4x4.CreateTranslation(PosePosition);

    /// <summary>The ray through pixel (x, y)'s centre, in camera axes, a unit of depth long.</summary>
    static Vector3 Ray(int x, int y, float fovY) => Ray(new Vector2(x + 0.5f, y + 0.5f), fovY);

    static Vector3 Ray(Vector2 p, float fovY)
    {
        double f = H / 2.0 / Math.Tan(fovY * Math.PI / 360.0);
        return new Vector3((float)((p.X - W / 2.0) / f), (float)((p.Y - H / 2.0) / f), 1f);
    }

    static (byte R, byte G, byte B, byte A) Rgba8(MeshBuffers buffers, int x, int y)
    {
        int i = 4 * (y * buffers.Width + x);
        var c = buffers.Color;
        return (c[i], c[i + 1], c[i + 2], c[i + 3]);
    }

    static void Save(MeshBuffers buffers, string name)
    {
        var dir = Environment.GetEnvironmentVariable("BIBUI_MESH3D_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"),
            Png.Encode(buffers.Color, buffers.Width, buffers.Height, 4 * buffers.Width, ChannelOrder.Rgba, AlphaMode.Straight));
    }

    public static void Register(Action<string, Action> run)
    {
        run("Mesh3D scene: a camera from a pose looks along the pose's z, x to the right, y down, and reads the pose back", () =>
        {
            var cam = OrbitCamera.FromPose(PosePosition, PoseOrientation, 80f, 25f);
            Near(0, Vector3.Distance(cam.Forward, Vector3.Transform(Vector3.UnitZ, PoseOrientation)), 1e-5, "forward is the pose's z");
            Near(0, Vector3.Distance(cam.Right, Vector3.Transform(Vector3.UnitX, PoseOrientation)), 1e-5, "right is the pose's x");
            Near(0, Vector3.Distance(cam.ViewUp, -Vector3.Transform(Vector3.UnitY, PoseOrientation)), 1e-5, "up is the pose's -y");
            Near(25, cam.Distance, 1e-4, "the target is as far ahead as asked");
            var q = cam.Orientation;
            Near(1, Math.Abs(Quaternion.Dot(q, PoseOrientation)), 1e-6, $"Orientation reads the pose back: {q} against {PoseOrientation}");
            var again = OrbitCamera.FromPose(cam.Eye, cam.Orientation, cam.FovY, cam.Distance);
            Near(0, Vector3.Distance(again.Target, cam.Target) + Vector3.Distance(again.Up, cam.Up), 1e-4, "FromPose of a camera's own pose is the camera");
        });

        run("Mesh3D scene: depth is z along the camera's axis, where each pixel's ray meets a known plane", () =>
        {
            // A square square to the axis 50 ahead, and one tilted 40° about the camera's x, 70 ahead, off to the left.
            const float fov = 70f;
            var scene = new MeshScene();
            scene.Add(Square(45f, new Rgba(200, 200, 200)), InCamera(new Vector3(8f, 0f, 50f), Quaternion.Identity));
            var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 40f * MathF.PI / 180f);
            scene.Add(Square(10f, new Rgba(200, 100, 100)), InCamera(new Vector3(-20f, 0f, 45f), tilt));
            var cam = OrbitCamera.FromPose(PosePosition, PoseOrientation, fov, 50f);
            var buffers = new MeshBuffers(W, H);
            buffers.Render(scene, cam, new Mesh3DOptions { Ambient = 0.3f });
            Save(buffers, "scene-depth");
            // The square ahead: one depth, 50, across it, whatever the ray's angle.
            foreach (var (x, y) in new[] { (W / 2 + 40, H / 2), (W - 20, 20), (W - 30, H - 10) })
            {
                Near(50, buffers.Depth[y * W + x], 1e-3, $"depth at ({x}, {y}) on the square ahead");
                True(buffers.MeshIds[y * W + x] == 0, $"mesh 0 at ({x}, {y}), not {buffers.MeshIds[y * W + x]}");
            }
            // The tilted square: the plane through (-20, 0, 45) with normal tilt·z; the ray (t·r) meets it where (t·r − p)·n = 0.
            var n = Vector3.Transform(Vector3.UnitZ, tilt);
            var p0 = new Vector3(-20f, 0f, 45f);
            int hits = 0;
            for (int y = 0; y < H; y += 7)
                for (int x = 0; x < W; x += 7)
                {
                    if (buffers.MeshIds[y * W + x] != 1) continue;
                    var r = Ray(x, y, fov);
                    float t = Vector3.Dot(p0, n) / Vector3.Dot(r, n);
                    Near(t, buffers.Depth[y * W + x], t * 1e-5, $"depth at ({x}, {y}) on the tilted square");
                    hits++;
                }
            True(hits > 20, $"the tilted square is drawn in front of the other ({hits} samples)");
            True(float.IsPositiveInfinity(buffers.Depth[0]) && buffers.MeshIds[0] == -1 && buffers.FaceIds[0] == -1 && Rgba8(buffers, 0, 0).A == 0,
                "where there is no mesh: depth +∞, ids -1, colour transparent");
        });

        run("Mesh3D scene: the id buffers name the mesh and the face, placed by each mesh's transform, and a hidden mesh is gone", () =>
        {
            var scene = new MeshScene();
            int back = scene.Add(Square(40f, new Rgba(90, 90, 90)), InCamera(new Vector3(0f, 0f, 60f), Quaternion.Identity));
            int left = scene.Add(Square(5f, new Rgba(220, 60, 60)), InCamera(new Vector3(-15f, 0f, 40f), Quaternion.Identity));
            int right = scene.Add(Square(5f, new Rgba(60, 60, 220)), InCamera(new Vector3(15f, 0f, 40f), Quaternion.Identity));
            const float fov = 60f;
            var cam = OrbitCamera.FromPose(PosePosition, PoseOrientation, fov, 40f);
            var buffers = new MeshBuffers(W, H);
            buffers.Render(scene, cam);
            Save(buffers, "scene-ids");
            double f = H / 2.0 / Math.Tan(fov * Math.PI / 360.0);
            // Where a camera-space point lands, and the pixel there.
            (int X, int Y) At(float x, float y, float z) => ((int)(W / 2.0 + x / z * f), (int)(H / 2.0 + y / z * f));
            // Face 0 of a square is (−,−) (+,−) (+,+): below its diagonal from (−,−) to (+,+), y < x. Face 1 is the rest.
            var lowerRight = At(15f + 3f, -3f, 40f);   // right square, x − 15 = 3 > y = −3: face 0
            var upperLeft = At(15f - 3f, 3f, 40f);     // y = 3 > x − 15 = −3: face 1
            True(buffers.MeshIds[lowerRight.Y * W + lowerRight.X] == right && buffers.FaceIds[lowerRight.Y * W + lowerRight.X] == 0,
                $"right square, face 0: got mesh {buffers.MeshIds[lowerRight.Y * W + lowerRight.X]}, face {buffers.FaceIds[lowerRight.Y * W + lowerRight.X]}");
            True(buffers.MeshIds[upperLeft.Y * W + upperLeft.X] == right && buffers.FaceIds[upperLeft.Y * W + upperLeft.X] == 1,
                $"right square, face 1: got mesh {buffers.MeshIds[upperLeft.Y * W + upperLeft.X]}, face {buffers.FaceIds[upperLeft.Y * W + upperLeft.X]}");
            var l = At(-15f, 0.5f, 40f);
            True(buffers.MeshIds[l.Y * W + l.X] == left, $"the left square is mesh {left}");
            var b = At(0f, 0f, 60f);
            True(buffers.MeshIds[b.Y * W + b.X] == back, "the back square between them");
            var redPixel = Rgba8(buffers, l.X, l.Y);
            True(redPixel.R > 3 * redPixel.B && redPixel.R > 3 * redPixel.G, $"each mesh keeps its own colour: the left one is red, not {redPixel}");

            scene.SetVisible(left, false);
            buffers.Render(scene, cam);
            True(buffers.MeshIds[l.Y * W + l.X] == back, "hidden, the left square shows what is behind it");
            True(buffers.Pick(new Vector2(l.X + 0.5f, l.Y + 0.5f))?.Mesh == back, "and is not picked");
            // Moved by its transform: the right square to where the left one was.
            scene.SetTransform(right, InCamera(new Vector3(-15f, 0f, 40f), Quaternion.Identity));
            buffers.Render(scene, cam);
            True(buffers.MeshIds[l.Y * W + l.X] == right, "a new transform moves the mesh");
        });

        run("Mesh3D scene: the point light falls off as 1/r² (two distances), and its lobe and the surface's angle as cosⁿ α · cos θ", () =>
        {
            const float fov = 90f, intensity = 1280f;
            float Centre(float distance, float spot, out MeshBuffers buffers)
            {
                var scene = new MeshScene();
                scene.Add(Square(200f, Rgba.White), InCamera(new Vector3(0f, 0f, distance), Quaternion.Identity));
                buffers = new MeshBuffers(W, H);
                buffers.Render(scene, OrbitCamera.FromPose(PosePosition, PoseOrientation, fov, distance),
                    new Mesh3DOptions { Ambient = 0f, Light = new CameraLight(intensity, spot) });
                // The pixels about the axis: (W/2 − 1, H/2 − 1) to (W/2, H/2), each half a pixel off it.
                return Rgba8(buffers, W / 2, H / 2).R;
            }
            float near = Centre(40f, 0f, out var at40), far = Centre(80f, 0f, out _);
            Save(at40, "light-40");
            // cos α at the centre pixels is 1 − 1e-5: as good as 1 in a byte.
            Near(255.0 * intensity / (40 * 40), near, 1.0, "lit 1280/40² at 40");
            Near(255.0 * intensity / (80 * 80), far, 1.0, "lit 1280/80² at 80");
            Near(4.0, near / far, 4.0 * 2.0 / far, "twice as far, a quarter of the light");

            // Off the axis, on the plane square to it: r = d / cos α and θ = α, so the light is I · cos θ · cosⁿ α · cos² α / d².
            const float d = 30f, n = 3f;
            Centre(d, n, out var spotLit);
            Save(spotLit, "light-spot");
            foreach (var (x, y) in new[] { (W / 2 + 60, H / 2), (W / 2 + 100, H / 2 - 70), (30, H - 30) })
            {
                var r = Ray(x, y, fov);
                double cosA = 1.0 / r.Length();
                double expected = 255.0 * Math.Min(1.0, intensity * cosA * Math.Pow(cosA, n) * cosA * cosA / (d * d));
                Near(expected, Rgba8(spotLit, x, y).R, 1.0, $"lit at ({x}, {y}), {Math.Acos(cosA) * 180 / Math.PI:F1}° off the axis");
            }
        });

        run("Mesh3D scene: a face crossing the near plane is clipped to it, not dropped, and the near plane is where the camera says", () =>
        {
            // A floor 1 below the eye (camera y is down), from 50 behind to 200 ahead: every face crosses the near plane.
            const float fov = 100f, nearPlane = 2f;
            var floor = Square(100f, new Rgba(180, 160, 150));
            var scene = new MeshScene();
            var lieFlat = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);   // its own z = 0 plane to camera y = const
            scene.Add(floor, InCamera(new Vector3(0f, 1f, 50f), lieFlat));
            var cam = OrbitCamera.FromPose(PosePosition, PoseOrientation, fov, 10f) with { Near = nearPlane };
            var buffers = new MeshBuffers(W, H) { Normals = true };
            buffers.Render(scene, cam, new Mesh3DOptions { Ambient = 0f, Light = new CameraLight(4f) });
            Save(buffers, "near-clipped-floor");
            // A ray (rx, ry, 1) meets the floor y = 1 at depth 1 / ry: drawn where that is past the near plane and inside the floor.
            int expected = 0, drawn = 0, bad = 0;
            for (int y = H / 2; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var r = Ray(x, y, fov);
                    float z = 1f / r.Y, across = r.X * z;
                    bool inside = z >= nearPlane * 1.01f && z <= 150f * 0.99f && MathF.Abs(across) <= 100f * 0.99f;
                    bool outside = z < nearPlane * 0.99f;
                    int i = y * W + x;
                    if (inside)
                    {
                        expected++;
                        if (buffers.MeshIds[i] == 0) drawn++;
                        if (MathF.Abs(buffers.Depth[i] - z) > z * 1e-4f) bad++;
                    }
                    if (outside && buffers.MeshIds[i] != -1) bad++;
                }
            True(expected > 10000, $"most of the lower half sees the floor ({expected} pixels)");
            True(drawn == expected, $"{expected - drawn} of {expected} pixels of the floor in front of the near plane are missing");
            True(bad == 0, $"{bad} pixels have the wrong depth, or show floor closer than the near plane");
            // The floor's normal is camera -y everywhere: up, toward the eye above it.
            int c = (H / 2 + 25) * W + W / 2;   // the floor 5 ahead
            True(buffers.MeshIds[c] == 0, "the floor is where its normal is read");
            Near(-1, buffers.NormalXyz[3 * c + 1], 1e-4, "the floor's normal points up (−y in camera axes)");
            Near(0, Math.Abs(buffers.NormalXyz[3 * c]) + Math.Abs(buffers.NormalXyz[3 * c + 2]), 1e-4, "and only up");
        });

        run("Mesh3D scene: a pick names the mesh and face under the point, and the barycentric weights give the point on the ray", () =>
        {
            const float fov = 75f;
            var scene = new MeshScene();
            scene.Add(Square(40f, new Rgba(100, 100, 100)), InCamera(new Vector3(0f, 0f, 70f), Quaternion.Identity));
            var turn = Quaternion.CreateFromYawPitchRoll(0.5f, 0.3f, 0.1f);
            var tile = Square(8f, new Rgba(220, 200, 80));
            scene.Add(tile, InCamera(new Vector3(6f, -4f, 35f), turn));
            var cam = OrbitCamera.FromPose(PosePosition, PoseOrientation, fov, 35f);
            var buffers = new MeshBuffers(W, H);
            buffers.Render(scene, cam);
            // A point off any pixel centre, on the tilted tile.
            double f = H / 2.0 / Math.Tan(fov * Math.PI / 360.0);
            var point = new Vector2((float)(W / 2.0 + 6.0 / 35.0 * f) + 0.3f, (float)(H / 2.0 - 4.0 / 35.0 * f) - 0.2f);
            var hit = buffers.Pick(point) ?? throw new Exception("nothing picked on the tile");
            True(hit.Mesh == 1, $"the tile is mesh 1, not {hit.Mesh}");
            int pi = (int)point.Y * W + (int)point.X;
            True(hit.Face == buffers.FaceIds[pi], $"the face drawn there, {buffers.FaceIds[pi]}, not {hit.Face}");
            // The face's corners in the world, weighted: the point.
            var model = scene.TransformOf(1);
            int[] corners = [0, 1, 2, 0, 2, 3];
            Vector3[] square = [new(-8f, -8f, 0f), new(8f, -8f, 0f), new(8f, 8f, 0f), new(-8f, 8f, 0f)];
            Vector3 Corner(int k) => Vector3.Transform(square[corners[3 * hit.Face + k]], model);
            var b = hit.Barycentric;
            True(b.X >= -1e-4f && b.Y >= -1e-4f && b.Z >= -1e-4f, $"the weights are inside the face: {b}");
            Near(1, b.X + b.Y + b.Z, 1e-5, "the weights sum to 1");
            var weighted = b.X * Corner(0) + b.Y * Corner(1) + b.Z * Corner(2);
            Near(0, Vector3.Distance(weighted, hit.Point), 1e-3, "the weights give the point");
            // The point is on the ray through the picked point, at the depth it says.
            var local = Vector3.Transform(hit.Point - PosePosition, Quaternion.Inverse(PoseOrientation));
            Near(hit.Depth, local.Z, 1e-3, "Depth is the point's z in camera axes");
            var ray = Ray(point, fov);
            Near(0, Vector3.Distance(local, ray * local.Z), 1e-3, "the point is on the ray through the picked point");
            var centre = buffers.Pick(new Vector2((int)point.X + 0.5f, (int)point.Y + 0.5f))!.Value;
            Near(centre.Depth, buffers.Depth[pi], centre.Depth * 1e-5, "at the pixel's centre, as deep as the depth buffer says");
            True(buffers.Pick(new Vector2(1f, 1f)) is null, "nothing under the corner");
        });

        run("Mesh3D scene: a view of a scene picks from a real click, redraws when a transform changes, and not when nothing did", () =>
        {
            var runtime = BibuiRuntime.Create();
            var scene = new MeshScene();
            var sphere = Mesh3DTests.Sphere(24, 48);
            var ball = new TriangleMesh(sphere.Faces, sphere.Xyz.Length / 3);
            ball.SetPositions(sphere.Xyz);
            int left = scene.Add(ball, Matrix4x4.CreateTranslation(-1.5f, 0f, 0f));
            int right = scene.Add(ball, Matrix4x4.CreateScale(0.5f, 1f, 0.5f) * Matrix4x4.CreateTranslation(1.5f, 0f, 0f));
            var options = new Mesh3DOptions { Home = new OrbitCamera(new Vector3(0f, 0f, 8f), Vector3.Zero, Vector3.UnitY) };
            Mesh3DWidget view = default;
            MeshHit? clicked = null;
            runtime.SetBuild(() =>
            {
                // A strip above the view, so that the view does not start at the window's corner.
                var page = runtime.Column().Sized(Size.Fill, Size.Fill);
                using (runtime.Parent(page))
                {
                    runtime.Box().Sized(Size.Fill, Size.Px(40f));
                    view = runtime.Mesh3D("scene", scene, options);
                    view.Node.Sized(Size.Fill, Size.Fill);
                    if (view.Interaction.Clicked) clicked = runtime.Mesh3DPick("scene", new Vector2(view.Interaction.X, view.Interaction.Y));
                }
                return page;
            }, ThemeRegistry.Load("bibui-dark"), TestBuild.Window(W, H + 40, 2f));
            runtime.Step();
            runtime.Step();
            uint drawn = runtime.MeshViewImageVersion("scene");
            runtime.Frame();
            True(runtime.MeshViewImageVersion("scene") == drawn, "an unchanged frame does not draw the scene again");
            // Click on the right ball's middle, in logical pixels in the window (the view starts 40 down).
            True(MeshRasterizer.Project(options.Home.Value, new Vector3(1.5f, 0.3f, 0.5f), W, H, out var s, out _), "the right ball projects");
            float cx = s.X, cy = s.Y + 40f;
            runtime.Step([WindowEvent.Mouse(WindowEventKind.MouseMove, cx, cy, MouseButton.Unknown),
                WindowEvent.Mouse(WindowEventKind.MouseDown, cx, cy, MouseButton.Left)]);
            runtime.Step([WindowEvent.Mouse(WindowEventKind.MouseUp, cx, cy, MouseButton.Left)]);
            var hit = clicked ?? throw new Exception("the click picked nothing");
            True(hit.Mesh == right, $"the click is on the right ball, mesh {right}, not {hit.Mesh}");
            // The squashed ball's surface: ((x − 1.5) / 0.5)² + y² + (z / 0.5)² = 1, the point on the ray toward the click.
            var q = hit.Point;
            Near(1, Math.Pow((q.X - 1.5) / 0.5, 2) + q.Y * q.Y + Math.Pow(q.Z / 0.5, 2), 0.02, $"the picked point {q} is on the squashed ball");
            True(q.Z > 0f, "on its near side");
            runtime.Frame();
            drawn = runtime.MeshViewImageVersion("scene");
            scene.SetTransform(left, Matrix4x4.CreateTranslation(-1.2f, 0f, 0f));
            runtime.Frame();
            True(runtime.MeshViewImageVersion("scene") != drawn, "a new transform draws the scene again");
        });

        run("Mesh3D scene: 1M and 2M triangles in 41 meshes, an orbit view and an endoscope view close in (time printed, not asserted)", () =>
        {
            foreach (int millions in new[] { 1, 2 })
            {
                var scene = OrganAndVessels(millions, out int triangles);
                var (min, max) = scene.Bounds;
                var outside = OrbitCamera.Fit(min, max, -Vector3.UnitZ, Vector3.UnitY);
                // Inside the organ's box, near its surface, looking along a vessel: wide and close.
                var inside = OrbitCamera.FromPose(new Vector3(0f, -10f, 52f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), 100f, 20f) with { Near = 0.5f };
                foreach (var (w, h) in new[] { (640, 480), (1280, 800), (1920, 1080) })
                {
                    var buffers = new MeshBuffers(w, h);
                    var orbit = Time(buffers, scene, outside, null, positions: false);
                    var scope = Time(buffers, scene, inside, new CameraLight(900f, 2f), positions: false);
                    var moving = Time(buffers, scene, outside, null, positions: true);
                    if (w == 1280) Save(buffers, $"scene-{triangles / 1000}k-orbit");
                    if (w == 1280) { Time(buffers, scene, inside, new CameraLight(900f, 2f), false); Save(buffers, $"scene-{triangles / 1000}k-endoscope"); }
                    Console.WriteLine($"         {triangles:N0} triangles, {scene.Count} meshes, {w}×{h}: orbit {orbit:F1} ms, endoscope {scope:F1} ms, every position replaced {moving:F1} ms");
                }
            }
        });
    }

    // A frame's time, the median of several: the camera turned a little each frame,
    // and, with positions, every mesh's positions replaced (so its normals are made again).
    static double Time(MeshBuffers buffers, MeshScene scene, OrbitCamera cam, CameraLight? light, bool positions)
    {
        var options = new Mesh3DOptions { Light = light, Ambient = light is null ? 0.25f : 0.05f, Background = new Rgba(10, 10, 14) };
        var copies = positions ? Enumerable.Range(0, scene.Count).Select(m => scene[m].Positions.ToArray()).ToArray() : null;
        const int frames = 9;
        var times = new double[frames];
        for (int f = -2; f < frames; f++)
        {
            if (copies is not null) for (int m = 0; m < scene.Count; m++) scene[m].SetPositions(copies[m]);
            var turned = cam with { Eye = cam.Target + Vector3.Transform(cam.Eye - cam.Target, Quaternion.CreateFromAxisAngle(cam.ViewUp, 0.002f * f)) };
            var sw = Stopwatch.StartNew();
            buffers.Render(scene, turned, options);
            if (f >= 0) times[f] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        return times[frames / 2];
    }

    /// <summary>
    /// About <paramref name="millions"/> million triangles: an organ-sized
    /// ellipsoid (radii 60, 40, 50) of three fifths of them, and 40 tubes over it
    /// of the rest, branching out from its middle.
    /// </summary>
    internal static MeshScene OrganAndVessels(int millions, out int triangles)
    {
        const int tubes = 40, sides = 40;
        // A sphere of r rings has 4r² triangles; a tube of n rings, 2·sides·(n − 1).
        int organRings = (int)MathF.Sqrt(millions * 600_000 / 4f);
        int tubeRings = millions * 400_000 / tubes / (2 * sides) + 1;
        var scene = new MeshScene();
        var (xyz, faces) = Mesh3DTests.Sphere(organRings, 2 * organRings, 1f);
        var organ = new TriangleMesh(faces, xyz.Length / 3);
        organ.SetPositions(xyz);
        organ.SetColor(new Rgba(200, 120, 110));
        scene.Add(organ, Matrix4x4.CreateScale(60f, 40f, 50f));
        triangles = organ.FaceCount;
        var rng = new Random(7);
        var path = new Vector3[tubeRings];
        for (int t = 0; t < tubes; t++)
        {
            // A branch: from near the middle out over the surface, curving.
            float a0 = t * 0.157f, lift = (float)rng.NextDouble() * 0.6f - 0.3f;
            for (int i = 0; i < tubeRings; i++)
            {
                float s = i / (tubeRings - 1f), a = a0 + 0.8f * s;
                path[i] = new Vector3(65f * s * MathF.Cos(a), 45f * (lift + 0.2f * MathF.Sin(4f * s + t)), 55f * s * MathF.Sin(a));
            }
            var tube = Tube(path, 2.5f - 1.5f * t / tubes, sides);
            tube.SetColor(t % 2 == 0 ? new Rgba(200, 40, 50) : new Rgba(60, 80, 200));
            scene.Add(tube);
            triangles += tube.FaceCount;
        }
        return scene;
    }
}
