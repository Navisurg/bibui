using System.Numerics;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Theming;

/// <summary>
/// Mesh3D: a sphere or a rippling surface, made here, in an orbit view. Drag
/// with the left button to turn it, with the right or middle to pan, the wheel
/// to zoom, double-click to go back. The surface's positions are replaced every
/// frame while it moves, as an app replays a recording's frames.
/// </summary>
internal static class DemoMesh3D
{
    private const int Grid = 140;

    private static TriangleMesh? s_sphere, s_wave;
    private static float[] s_waveXyz = [];
    private static float[] s_heights = [];
    private static readonly MeshOverlay s_overlay = new();
    private static readonly Mesh3DOptions s_options = new() { Overlay = s_overlay };
    private static bool s_showWave = true, s_wire, s_moving = true, s_heightColours = true;
    private static float s_time;

    internal static void Build(BibuiRuntime runtime)
    {
        if (s_sphere is null) Make();

        var controls = runtime.BeginParent(runtime.Row());
        if (runtime.Switch("mesh3d-wave", s_showWave, "Rippling surface").Clicked) s_showWave = !s_showWave;
        runtime.Gap(20f);
        if (runtime.Switch("mesh3d-wire", s_wire, "Wireframe").Clicked) s_wire = !s_wire;
        runtime.Gap(20f);
        if (runtime.Switch("mesh3d-moving", s_moving, "Moving").Clicked) s_moving = !s_moving;
        runtime.Gap(20f);
        if (runtime.Switch("mesh3d-height", s_heightColours, "Colour by height").Clicked) s_heightColours = !s_heightColours;
        runtime.EndParent(controls);
        runtime.Gap(16f);

        var mesh = s_showWave ? s_wave! : s_sphere!;
        if (s_showWave)
        {
            if (s_moving)
            {
                s_time += runtime.Dt;
                Ripple(s_waveXyz, s_time);
                mesh.SetPositions(s_waveXyz);
                runtime.RequestFrame();
            }
            if (s_heightColours)
            {
                for (int i = 0; i < s_heights.Length; i++) s_heights[i] = s_waveXyz[3 * i + 1];
                mesh.SetScalars(s_heights, -0.12f, 0.12f, Colormaps.Turbo);
            }
            else mesh.ClearScalars();
        }

        // A track over the surface: its ripple's crest along x, and a dot at its centre.
        s_overlay.Clear();
        if (s_showWave)
        {
            Span<Vector3> track = stackalloc Vector3[Grid];
            for (int j = 0; j < Grid; j++) track[j] = At(s_waveXyz, Grid / 2, j) + new Vector3(0f, 0.01f, 0f);
            s_overlay.LineStrip(track, new Rgba(255, 255, 255, 220), 2f);
            s_overlay.Points([At(s_waveXyz, Grid / 2, Grid / 2) + new Vector3(0f, 0.01f, 0f)], new Rgba(255, 80, 80), 9f);
        }

        s_options.Wireframe = s_wire;
        s_options.Home = s_showWave
            ? new OrbitCamera(new Vector3(0f, 1.5f, 2.3f), Vector3.Zero, Vector3.UnitY)
            : new OrbitCamera(new Vector3(0f, 0.6f, 3.2f), Vector3.Zero, Vector3.UnitY);
        var view = runtime.Mesh3D(s_showWave ? "mesh3d-wave-view" : "mesh3d-sphere-view", mesh, s_options);
        view.Node.Sized(Size.Fill, Size.Px(420f));
        view.Node.Style.BackgroundColor = UiColor.SurfaceRaised;
        view.Node.Style.CornerRadius = 8f;

        runtime.Gap(10f);
        var c = view.Camera;
        Span<char> line = stackalloc char[128];
        line.TryWrite($"{mesh.VertexCount:N0} vertices, {mesh.FaceCount:N0} triangles · eye {c.Eye.X:F2}, {c.Eye.Y:F2}, {c.Eye.Z:F2} · {c.Distance:F2} from the target", out int n);
        runtime.Text(line[..n], ComponentSize.Sm, TextColor.Secondary);
    }

    private static Vector3 At(float[] xyz, int i, int j)
    {
        int v = 3 * (i * Grid + j);
        return new Vector3(xyz[v], xyz[v + 1], xyz[v + 2]);
    }

    private static void Make()
    {
        // A UV sphere, counter-clockwise seen from outside, coloured by its normal.
        const int rings = 48, segments = 96;
        var xyz = new List<float>();
        var rgb = new List<float>();
        for (int i = 0; i <= rings; i++)
            for (int j = 0; j <= segments; j++)
            {
                float th = MathF.PI * i / rings, ph = 2f * MathF.PI * j / segments;
                float x = MathF.Sin(th) * MathF.Cos(ph), y = MathF.Cos(th), z = MathF.Sin(th) * MathF.Sin(ph);
                xyz.AddRange([x, y, z]);
                rgb.AddRange([0.55f + 0.4f * x, 0.55f + 0.4f * y, 0.55f + 0.4f * z]);
            }
        var faces = new List<uint>();
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < segments; j++)
            {
                uint a = (uint)(i * (segments + 1) + j), b = a + 1, c = a + segments + 1, d = c + 1;
                faces.AddRange([a, b, c, b, d, c]);
            }
        s_sphere = new TriangleMesh(faces.ToArray(), xyz.Count / 3);
        s_sphere.SetPositions(xyz.ToArray());
        s_sphere.SetColors(rgb.ToArray());

        var grid = new uint[(Grid - 1) * (Grid - 1) * 6];
        int k = 0;
        for (int i = 0; i < Grid - 1; i++)
            for (int j = 0; j < Grid - 1; j++)
            {
                uint a = (uint)(i * Grid + j), b = a + 1, c = a + Grid, d = c + 1;
                (grid[k], grid[k + 1], grid[k + 2], grid[k + 3], grid[k + 4], grid[k + 5]) = (a, c, b, b, c, d);
                k += 6;
            }
        s_wave = new TriangleMesh(grid, Grid * Grid);
        s_wave.SetColor(new Rgba(200, 200, 210));
        s_waveXyz = new float[Grid * Grid * 3];
        s_heights = new float[Grid * Grid];
        Ripple(s_waveXyz, 0f);
        s_wave.SetPositions(s_waveXyz);
    }

    // A ripple spreading from the middle of [-1, 1]² in x and z, height in y.
    private static void Ripple(float[] xyz, float t)
    {
        for (int i = 0; i < Grid; i++)
            for (int j = 0; j < Grid; j++)
            {
                float x = -1f + 2f * j / (Grid - 1), z = -1f + 2f * i / (Grid - 1);
                float r = MathF.Sqrt(x * x + z * z);
                int v = 3 * (i * Grid + j);
                (xyz[v], xyz[v + 1], xyz[v + 2]) = (x, 0.12f * MathF.Sin(9f * r - 3f * t) * MathF.Exp(-r), z);
            }
    }
}
