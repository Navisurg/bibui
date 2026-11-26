using System.Numerics;
using Bibui.Core.Draw;

namespace Bibui.Core.Runtime;

/// <summary>
/// A 3D view and what happened to it this frame: the node to place, the
/// camera after this frame's input, and the interaction.
/// </summary>
public readonly record struct Mesh3DWidget(UiNode Node, OrbitCamera Camera, Interaction Interaction)
{
    public static implicit operator UiNode(Mesh3DWidget widget) => widget.Node;
}

public sealed partial class BibuiRuntime
{
    // A view's camera and its drawing: kept by id while the view is built.
    private readonly ResourceCache<string, Mesh3DView> _meshViews = new(keepFrames: 60);

    internal Mesh3DView MeshView(string id) => _meshViews.FromKey(id, 0, static _ => new Mesh3DView());

    internal bool TryMeshView(string id, out Mesh3DView view) => _meshViews.TryGet(id, out view!);

    /// <summary>The version of the pixels the view named <paramref name="id"/> last drew: what a test reads to see whether it drew again.</summary>
    internal uint MeshViewImageVersion(string id) => TryMeshView(id, out var view) ? view.Image.Version : 0u;
}

/// <summary>What a view keeps between frames: its camera, the pointer it was dragged from, its pixels.</summary>
internal sealed class Mesh3DView
{
    public OrbitCamera Camera;
    public bool HasCamera;
    public Vector2 Last;
    public TriangleMesh? Mesh;
    public Mesh3DOptions Options = new();
    public readonly PixelImage Image = new();
    public readonly MeshRasterizer Raster = new();
    public readonly DrawCallback Draw;

    // What the pixels were drawn from: unchanged, they are not drawn again.
    private TriangleMesh? _drawnMesh;
    private uint _drawnVersion;
    private OrbitCamera _drawnCamera;
    private int _drawnW, _drawnH;
    private (bool, Rgba, float, Rgba?, bool, int) _drawnLook;

    public Mesh3DView() => Draw = (_, ctx) => Paint(ctx);

    private static readonly Paint Plain = new() { Fill = Rgba.White, Filter = FilterQuality.None, Antialias = false };

    private void Paint(DrawContext ctx)
    {
        if (Mesh is null) return;
        int w = (int)MathF.Round(ctx.Rect.Width), h = (int)MathF.Round(ctx.Rect.Height);
        if (w <= 0 || h <= 0) return;
        var look = (Options.Wireframe, Options.WireframeColor, Options.Ambient, Options.Background, Options.ShowMesh, Options.Supersample);
        if (!ReferenceEquals(_drawnMesh, Mesh) || _drawnVersion != Mesh.Version || _drawnCamera != Camera
            || _drawnW != w || _drawnH != h || _drawnLook != look)
        {
            Raster.Render(Mesh, Camera, Options, Image, w, h);
            (_drawnMesh, _drawnVersion, _drawnCamera, _drawnW, _drawnH, _drawnLook) = (Mesh, Mesh.Version, Camera, w, h, look);
        }
        ctx.DrawPixels(Image, new RectF(0f, 0f, w, h), Plain);
        if (Options.Overlay is { } overlay) DrawOverlay(ctx, overlay, w, h);
    }

    private Vector2[] _points = new Vector2[64];

    /// <summary>A share of a point's depth it may be behind the surface and still show: a track drawn on it.</summary>
    private const float OverlayTolerance = 0.01f;

    // Projected, and in sight: in front of the eye and, when the options ask, of the mesh.
    private bool Visible(Vector3 p, int w, int h, out Vector2 s) =>
        MeshRasterizer.Project(Camera, p, w, h, out s, out float depth)
        && (!Options.OverlayHiddenByMesh || Raster.InFront(s, depth, OverlayTolerance));

    private void DrawOverlay(DrawContext ctx, MeshOverlay overlay, int w, int h)
    {
        var verts = overlay.Vertices;
        foreach (var item in overlay.Items)
        {
            if (_points.Length < item.Count) _points = new Vector2[Math.Max(item.Count, _points.Length * 2)];
            float size = item.Size * ctx.Scale;
            switch (item.Kind)
            {
                case MeshOverlay.Kind.Points:
                {
                    int n = 0;
                    for (int i = 0; i < item.Count; i++)
                        if (Visible(verts[item.Start + i], w, h, out var s)) _points[n++] = s;
                    if (n > 0)
                        ctx.DrawPoints(PointMode.Points, _points.AsSpan(0, n),
                            new Paint { Stroke = item.Color, StrokeWidth = size, Cap = StrokeCap.Round });
                    break;
                }
                case MeshOverlay.Kind.Strip:
                {
                    // A run of points in sight is one polyline; a point out of sight breaks the line.
                    var paint = new Paint { Stroke = item.Color, StrokeWidth = size, Cap = StrokeCap.Round, Join = StrokeJoin.Round };
                    int n = 0;
                    for (int i = 0; i < item.Count; i++)
                    {
                        if (Visible(verts[item.Start + i], w, h, out var s)) { _points[n++] = s; continue; }
                        if (n > 1) ctx.DrawPoints(PointMode.Polygon, _points.AsSpan(0, n), paint);
                        n = 0;
                    }
                    if (n > 1) ctx.DrawPoints(PointMode.Polygon, _points.AsSpan(0, n), paint);
                    break;
                }
                case MeshOverlay.Kind.Lines:
                {
                    int n = 0;
                    for (int i = 0; i + 1 < item.Count; i += 2)
                    {
                        if (Visible(verts[item.Start + i], w, h, out var s0) && Visible(verts[item.Start + i + 1], w, h, out var s1))
                        {
                            _points[n++] = s0;
                            _points[n++] = s1;
                        }
                    }
                    if (n > 0)
                        ctx.DrawPoints(PointMode.Lines, _points.AsSpan(0, n),
                            new Paint { Stroke = item.Color, StrokeWidth = size, Cap = StrokeCap.Round });
                    break;
                }
            }
        }
    }
}

/// <summary>
/// A triangle mesh in a box, seen through an orbit camera: left-drag orbits,
/// right- or middle-drag pans, the wheel zooms, a double-click goes home. The
/// mesh is drawn by the core on the CPU into pixels every renderer draws, so it
/// looks the same headless and in a window.
/// </summary>
public static class Mesh3DExtensions
{
    /// <summary>Degrees of orbit a logical pixel of drag.</summary>
    public const float OrbitDegreesPerPixel = 0.4f;

    /// <summary>The share the distance to the target shrinks by a wheel notch.</summary>
    public const float ZoomPerNotch = 0.1f;

    /// <summary>
    /// Builds the view named <paramref name="id"/> under the parent in scope,
    /// filling it unless the caller sizes the node. The mesh and the options
    /// are read when the frame is painted.
    /// </summary>
    public static Mesh3DWidget Mesh3D(this BibuiRuntime runtime, string id, TriangleMesh mesh, Mesh3DOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new Mesh3DOptions();

        var view = runtime.MeshView(id);
        view.Mesh = mesh;
        view.Options = options;
        if (!view.HasCamera)
        {
            view.Camera = Home(mesh, options);
            view.HasCamera = true;
        }

        var claims = options.Interactive
            ? InteractionKinds.LeftPress | InteractionKinds.RightPress | InteractionKinds.MiddlePress | InteractionKinds.Scroll
            : InteractionKinds.None;
        var box = runtime.BoxFromId(id, claims);
        box.Sized(Size.Fill, Size.Fill);
        var i = runtime.InteractionsFor(box);

        if (options.Interactive)
        {
            var at = new Vector2(i.X, i.Y);
            if (i.Pressed && i.PressCount >= 2) view.Camera = Home(mesh, options);
            else if (i.Pressed || i.RightPressed || i.MiddlePressed) view.Last = at;
            else if (i.Dragging || i.RightDragging || i.MiddleDragging || i.Released || i.RightReleased || i.MiddleReleased)
            {
                var d = at - view.Last;
                view.Last = at;
                if (d != Vector2.Zero)
                {
                    if (i.Dragging || i.Released)
                        view.Camera = view.Camera.Orbit(-d.X * OrbitDegreesPerPixel, d.Y * OrbitDegreesPerPixel);
                    else
                    {
                        // A pixel at the target's depth, in world units: the point under the pointer stays under it.
                        float height = MathF.Max(box.Extent.Y, 1f);
                        float perPixel = 2f * view.Camera.Distance * MathF.Tan(view.Camera.FovY * 0.5f * MathF.PI / 180f) / height;
                        view.Camera = view.Camera.Pan(-d.X * perPixel, d.Y * perPixel);
                    }
                }
            }
            // The wheel comes in logical px, 32 a notch, positive down: up (away from you) zooms in.
            if (i.ScrollY != 0f) view.Camera = view.Camera.Zoom(MathF.Pow(1f - ZoomPerNotch, -i.ScrollY / 32f));
        }

        box.Draw = view.Draw;
        return new Mesh3DWidget(box, view.Camera, i);
    }

    /// <summary>The camera of the view named <paramref name="id"/>, or null when it has none yet.</summary>
    public static OrbitCamera? Mesh3DCamera(this BibuiRuntime runtime, string id)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.TryMeshView(id, out var view) && view.HasCamera ? view.Camera : null;
    }

    /// <summary>Puts the view named <paramref name="id"/> at <paramref name="camera"/>: before its first build too.</summary>
    public static void SetMesh3DCamera(this BibuiRuntime runtime, string id, OrbitCamera camera)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var view = runtime.MeshView(id);
        view.Camera = camera;
        view.HasCamera = true;
    }

    private static OrbitCamera Home(TriangleMesh mesh, Mesh3DOptions options)
    {
        if (options.Home is { } home) return home;
        var (min, max) = mesh.Bounds;
        return OrbitCamera.Fit(min, max, options.ViewDirection, options.Up);
    }
}
