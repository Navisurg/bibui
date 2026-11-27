using System.Numerics;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Theming;

/// <summary>
/// Mesh3D with a scene: an organ-like body and a tree of vessels swept into
/// tubes over it, each tube a mesh of its own. Seen from outside with the orbit
/// camera, or through an endoscope: a camera set from a pose each frame, wide
/// and close, lit by a point light at its tip that falls off with distance.
/// A click picks a vessel and lights it up. Everything is made here.
/// </summary>
internal static class DemoMesh3DScene
{
    private const int OrganRings = 160, TubeRings = 48, TubeSides = 20, Generations = 5;

    private static MeshScene? s_scene;
    private static readonly List<Rgba> s_colours = [];
    private static readonly Mesh3DOptions s_orbit = new()
    {
        Home = new OrbitCamera(new Vector3(0f, 150f, 110f), Vector3.Zero, Vector3.UnitY),
        Background = new Rgba(14, 14, 18),
    };
    private static readonly Mesh3DOptions s_scope = new()
    {
        Interactive = false,
        Ambient = 0f,
        Light = new CameraLight(150f, SpotExponent: 2f),
        Background = new Rgba(0, 0, 0),
    };
    private static bool s_endoscope = true, s_moving = true;
    private static float s_time;
    private static MeshHit? s_picked;
    private static int s_lit = -1;

    internal static void Build(BibuiRuntime runtime)
    {
        s_scene ??= Make();

        var controls = runtime.BeginParent(runtime.Row());
        if (runtime.Switch("mesh3d-scene-endoscope", s_endoscope, "Endoscope view").Clicked) s_endoscope = !s_endoscope;
        runtime.Gap(20f);
        if (runtime.Switch("mesh3d-scene-moving", s_moving, "Moving").Clicked) s_moving = !s_moving;
        runtime.EndParent(controls);
        runtime.Gap(16f);

        string id = s_endoscope ? "mesh3d-scene-scope" : "mesh3d-scene-orbit";
        if (s_endoscope)
        {
            if (s_moving)
            {
                s_time += runtime.Dt;
                runtime.RequestFrame();
            }
            runtime.SetMesh3DCamera(id, Endoscope(s_time));
        }
        var view = runtime.Mesh3D(id, s_scene, s_endoscope ? s_scope : s_orbit);
        view.Node.Sized(Size.Fill, Size.Px(440f));
        view.Node.Style.CornerRadius = 8f;

        // A click picks what is under it; a vessel picked lights up.
        var i = view.Interaction;
        if (i.Clicked)
        {
            s_picked = runtime.Mesh3DPick(id, new Vector2(i.X, i.Y));
            Light(s_picked is { Mesh: > 0 } hit ? hit.Mesh : -1);
        }

        runtime.Gap(10f);
        var c = view.Camera;
        Span<char> line = stackalloc char[200];
        int n;
        if (s_picked is { } p)
            line.TryWrite($"Picked {(p.Mesh == 0 ? "the organ" : $"vessel {p.Mesh}")}, face {p.Face:N0}, at {p.Point.X:F1}, {p.Point.Y:F1}, {p.Point.Z:F1}, {p.Depth:F1} ahead", out n);
        else
            line.TryWrite($"{s_scene.Count} meshes, {Triangles(s_scene):N0} triangles · {c.FovY:F0}° · click a vessel to pick it", out n);
        runtime.Text(line[..n], ComponentSize.Sm, TextColor.Secondary);
    }

    // The endoscope's pose at time t: circling over the top of the organ, a little above it, looking ahead and down.
    private static OrbitCamera Endoscope(float t)
    {
        float a = 0.25f * t;
        var at = Surface(new Vector3(0.6f * MathF.Cos(a), 1f, 0.6f * MathF.Sin(a))) * 1.3f;
        var ahead = Surface(new Vector3(0.6f * MathF.Cos(a + 0.9f), 1f, 0.6f * MathF.Sin(a + 0.9f)));
        return OrbitCamera.FromPose(at, LookAt(ahead - at, -Vector3.UnitY), 100f, 20f) with { Near = 0.5f };
    }

    // The turn that points a camera's z along forward, its y (down) as near worldDown as it can.
    private static Quaternion LookAt(Vector3 forward, Vector3 worldDown)
    {
        var z = Vector3.Normalize(forward);
        var x = Vector3.Normalize(Vector3.Cross(worldDown, z));
        var y = Vector3.Cross(z, x);
        return Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f));
    }

    private static void Light(int mesh)
    {
        if (s_lit > 0) s_scene![s_lit].SetColor(s_colours[s_lit]);
        s_lit = mesh;
        if (mesh > 0) s_scene![mesh].SetColor(new Rgba(255, 230, 90));
    }

    private static int Triangles(MeshScene scene)
    {
        int n = 0;
        for (int m = 0; m < scene.Count; m++) n += scene[m].FaceCount;
        return n;
    }

    // The organ's surface along a direction from its middle: an ellipsoid with gentle lobes.
    private static Vector3 Surface(Vector3 direction)
    {
        var d = Vector3.Normalize(direction);
        float bumps = 1f + 0.06f * MathF.Sin(3f * d.X + 1f) * MathF.Cos(4f * d.Z) + 0.04f * MathF.Sin(5f * d.Y);
        return new Vector3(60f * d.X, 34f * d.Y, 46f * d.Z) * bumps;
    }

    private static MeshScene Make()
    {
        var scene = new MeshScene();
        // The organ: a sphere of rings pushed out to its surface.
        int segments = 2 * OrganRings;
        var xyz = new float[(OrganRings + 1) * (segments + 1) * 3];
        for (int i = 0, v = 0; i <= OrganRings; i++)
            for (int j = 0; j <= segments; j++, v += 3)
            {
                float th = MathF.PI * i / OrganRings, ph = 2f * MathF.PI * j / segments;
                var p = Surface(new Vector3(MathF.Sin(th) * MathF.Cos(ph), MathF.Cos(th), MathF.Sin(th) * MathF.Sin(ph)));
                (xyz[v], xyz[v + 1], xyz[v + 2]) = (p.X, p.Y, p.Z);
            }
        var faces = new uint[OrganRings * segments * 6];
        for (int i = 0, k = 0; i < OrganRings; i++)
            for (int j = 0; j < segments; j++, k += 6)
            {
                uint a = (uint)(i * (segments + 1) + j), b = a + 1, c = a + (uint)segments + 1, d = c + 1;
                (faces[k], faces[k + 1], faces[k + 2], faces[k + 3], faces[k + 4], faces[k + 5]) = (a, b, c, b, d, c);
            }
        var organ = new TriangleMesh(faces, xyz.Length / 3);
        organ.SetPositions(xyz);
        organ.SetColor(new Rgba(214, 140, 128));
        scene.Add(organ);
        s_colours.Add(new Rgba(214, 140, 128));

        // The vessels: a tree branching over the top of the organ, each branch a tube on its surface.
        Branch(scene, Vector3.UnitY, new Vector3(1f, 0f, 0.2f), 3.2f, 0);
        Branch(scene, Vector3.UnitY, new Vector3(-1f, 0f, -0.3f), 3.2f, 0);
        return scene;
    }

    // A branch from the organ's surface along the direction from, heading along heading over it (both
    // directions from the organ's middle), then two children from its end.
    private static void Branch(MeshScene scene, Vector3 from, Vector3 heading, float radius, int generation)
    {
        var path = new Vector3[TubeRings];
        var directions = new Vector3[TubeRings];
        var dir = Vector3.Normalize(from);
        float length = 0.55f / (1f + generation * 0.3f), bend = generation % 2 == 0 ? 0.6f : -0.6f;
        for (int i = 0; i < TubeRings; i++)
        {
            float s = i / (TubeRings - 1f);
            var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(dir, heading)), length * s);
            var along = Vector3.Transform(dir, turn);
            along = Vector3.Transform(along, Quaternion.CreateFromAxisAngle(dir, bend * s * length));
            directions[i] = along;
            path[i] = Surface(along) + Vector3.Normalize(along) * radius;
        }
        var tube = Tube(path, radius, TubeSides);
        var colour = generation % 2 == 0 ? new Rgba(176, 36, 48) : new Rgba(150, 30, 60);
        tube.SetColor(colour);
        scene.Add(tube);
        s_colours.Add(colour);
        if (generation + 1 >= Generations) return;
        var end = directions[^1];
        var onward = Vector3.Normalize(directions[^1] - directions[^4]);
        var side = Vector3.Normalize(Vector3.Cross(end, onward));
        Branch(scene, end, onward + 0.7f * side, radius * 0.72f, generation + 1);
        Branch(scene, end, onward - 0.7f * side, radius * 0.72f, generation + 1);
    }

    // A tube of the radius along the path, sides vertices round each ring.
    private static TriangleMesh Tube(Vector3[] path, float radius, int sides)
    {
        int rings = path.Length;
        var xyz = new float[rings * sides * 3];
        var normal = Vector3.UnitY;
        for (int i = 0, v = 0; i < rings; i++)
        {
            var along = Vector3.Normalize(path[Math.Min(i + 1, rings - 1)] - path[Math.Max(i - 1, 0)]);
            // Carried along the path, so the rings do not twist.
            normal = Vector3.Normalize(normal - along * Vector3.Dot(normal, along));
            var other = Vector3.Cross(along, normal);
            for (int k = 0; k < sides; k++, v += 3)
            {
                float a = 2f * MathF.PI * k / sides;
                var p = path[i] + radius * (MathF.Cos(a) * normal + MathF.Sin(a) * other);
                (xyz[v], xyz[v + 1], xyz[v + 2]) = (p.X, p.Y, p.Z);
            }
        }
        var faces = new uint[(rings - 1) * sides * 6];
        for (int i = 0, f = 0; i + 1 < rings; i++)
            for (int k = 0; k < sides; k++, f += 6)
            {
                uint a = (uint)(i * sides + k), b = (uint)(i * sides + (k + 1) % sides), c = a + (uint)sides, d = b + (uint)sides;
                (faces[f], faces[f + 1], faces[f + 2], faces[f + 3], faces[f + 4], faces[f + 5]) = (a, b, c, b, d, c);
            }
        var tube = new TriangleMesh(faces, rings * sides);
        tube.SetPositions(xyz);
        return tube;
    }
}
