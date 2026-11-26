# Mesh3D

A triangle mesh in a box, seen through an orbit camera: drag to turn it round,
pan and zoom. The faces are fixed; the vertex positions can be replaced every
frame, so the mesh can move and deform. It is lit from the camera (a
headlight and a little ambient light) with normals computed from the current
positions, and both sides of a face are drawn. Vertices carry a colour, or a
scalar through a colormap, or texture coordinates into a picture. Points and
line strips can be drawn over it, and its edges as a wireframe.

> **Status.** On the branch `bib/add-a-3d-mesh-view-to-bibui-an-o` until it is
> merged. Every renderer draws it the same way: the core rasterises the mesh on
> the CPU, z-buffered and spread over the cores, into pixels the renderer draws
> as an image (`DrawOpKind.Pixels`). There is no GPU path yet.

## Build

```cs
public static Mesh3DWidget Mesh3D(
    this BibuiRuntime runtime,
    string id,
    TriangleMesh mesh,
    Mesh3DOptions? options = null)

public readonly record struct Mesh3DWidget(UiNode Node, OrbitCamera Camera, Interaction Interaction);

// The camera of the view named id, read and set from code.
public static OrbitCamera? Mesh3DCamera(this BibuiRuntime runtime, string id)
public static void SetMesh3DCamera(this BibuiRuntime runtime, string id, OrbitCamera camera)
```

| Parameter | Meaning |
|---|---|
| `id` | The view's retained box, and the key its camera is kept under. |
| `mesh` | What to draw. The app owns it and changes it between frames. |
| `options` | How to draw it: home camera, wireframe, light, overlay. Null is the defaults. |

The node fills its parent on both axes unless you size it (`Node.Sized(...)`).

### The mesh

```cs
public sealed class TriangleMesh
{
    public TriangleMesh(ReadOnlySpan<uint> faces, int vertexCount);   // faces: F × 3 vertex indices
    public int VertexCount { get; }
    public int FaceCount { get; }

    public void SetPositions(ReadOnlySpan<float> xyz);                // V × 3, replaces them all
    public void SetPositions(ReadOnlySpan<Vector3> positions);        // the same, as vectors
    public void SetColors(ReadOnlySpan<byte> rgb);                    // V × 3 bytes
    public void SetColors(ReadOnlySpan<float> rgb);                   // V × 3 floats, 0..1
    public void SetColor(Rgba color);                                 // one colour for every vertex
    public void SetScalars(ReadOnlySpan<float> values, float min, float max,
        IReadOnlyList<(float Stop, string Color)>? colormap = null);  // V values through a colormap (Viridis)
    public void ClearScalars();                                       // back to the colours
    public void SetTexture(ReadOnlySpan<byte> rgba, int width, int height);  // RGBA8, rows tight
    public void SetTextureCoordinates(ReadOnlySpan<float> uv);        // V × 2; (0, 0) top-left, (1, 1) bottom-right
    public void ClearTexture();
    public (Vector3 Min, Vector3 Max) Bounds { get; }                 // of the current positions
}
```

- Faces are fixed for the mesh's life; each index must be below `vertexCount`
  (the constructor throws otherwise). Winding does not matter: both sides are
  lit, the back a little darker.
- `SetPositions` copies. Call it each frame the mesh moves, with a slice of
  frames you keep in memory; the faces, colours and texture stay. Normals are
  recomputed from the new positions when the mesh is next drawn.
- Colours start light grey. Scalars, while set, take the colours' place.
  A texture, with coordinates, is multiplied by the vertex colour (leave it
  white to see the picture as it is) and lit the same way. The texture may be
  replaced every frame, at the same or a new size (a video frame).
- Units are whatever the positions are in (millimetres, say); the camera fits
  near and far planes to the mesh's bounds.

### The camera

```cs
public readonly record struct OrbitCamera(Vector3 Eye, Vector3 Target, Vector3 Up, float FovY = 45f)
{
    public float Distance { get; }                                    // |Eye − Target|
    public OrbitCamera Orbit(float yawDegrees, float pitchDegrees);   // about Up, then about the view's right
    public OrbitCamera Pan(float right, float up);                    // moves Eye and Target, in world units
    public OrbitCamera Zoom(float factor);                            // Distance × factor, toward Target
    public static OrbitCamera Fit(Vector3 min, Vector3 max, Vector3 viewDirection, Vector3 up, float fovY = 45f);
}
```

`Up` is the world's up, the axis the view orbits about: `(0, 1, 0)` for y-up
data, `(0, -1, 0)` for OpenCV camera axes (x right, y down, z forward), with
which `Eye = (0, 0, 0)` and a `Target` ahead on +z is the recording camera's own
view. `FovY` is the vertical field of view in degrees.

The camera is kept under `id`, the way a retained box keeps its state, for as
long as the view is built. Its first value is `options.Home`, or, without one,
`OrbitCamera.Fit` of the mesh's bounds along `options.ViewDirection` with
`options.Up`. `SetMesh3DCamera` replaces it (for a scripted view or a screenshot);
`Mesh3DWidget.Camera` and `Mesh3DCamera` read it. An app that owns the camera
sets it before the call each frame and reads `Camera` back after it.

### Options

```cs
public sealed class Mesh3DOptions
{
    public OrbitCamera? Home { get; set; }            // the first camera, and what a double-click goes back to
    public Vector3 Up { get; set; } = Vector3.UnitY;  // without a Home: the fitted camera's up
    public Vector3 ViewDirection { get; set; } = -Vector3.UnitZ;  // without a Home: the way the fitted camera looks
    public bool Wireframe { get; set; }               // draws every edge over the faces
    public Rgba WireframeColor { get; set; } = new(0, 0, 0, 110);
    public float Ambient { get; set; } = 0.25f;       // light everywhere; the headlight gives the rest
    public Rgba? Background { get; set; }             // null: the box shows through where there is no mesh
    public bool Interactive { get; set; } = true;     // false: the view takes no input
    public MeshOverlay? Overlay { get; set; }         // points and lines drawn over the mesh
}

public sealed class MeshOverlay
{
    public void Clear();
    public void Points(ReadOnlySpan<Vector3> points, Rgba color, float size = 6f);   // round dots, size in px
    public void LineStrip(ReadOnlySpan<Vector3> points, Rgba color, float width = 2f); // a polyline, width in px
    public void Lines(ReadOnlySpan<Vector3> pairs, Rgba color, float width = 2f);    // separate segments, two points each
}
```

Overlay points and lines are drawn after the mesh, antialiased, and not hidden
by it: a track behind the surface still shows. Fill the overlay again whenever
what it shows changes (`Clear`, then add), and keep the instance.

## Claims and reports

- The view claims `LeftPress | RightPress | MiddlePress | Scroll`, or nothing
  when `Interactive` is false.
- **Left-drag** orbits: 0.4° a pixel, across about `Up`, down about the view's
  right; the pitch stops short of the poles.
- **Right- or middle-drag** pans: the point under the pointer stays under it at
  the target's depth.
- **The wheel** zooms toward the target, 10 % a notch.
- **A double-click** goes back to `Home` (or the fitted camera).
- It returns a `Mesh3DWidget`: the node, the camera after this frame's input,
  and the interaction (for a hover readout or a click of your own).

## State the caller owns

The `TriangleMesh`, the `Mesh3DOptions` and the `MeshOverlay`: keep each one
and change it between frames. The camera is kept by the runtime under `id`; set
it with `SetMesh3DCamera`.

## Speed

The mesh is drawn at the box's size in physical pixels. Normals and the
vertex transform are recomputed only when the positions, the camera or the size
change; triangles are rasterised in horizontal bands on every core. See the
measurements in the pull request that added it.

## Example

```csharp
using System.Numerics;
using Bibui.Core;
using Bibui.Core.Runtime;

sealed class SurfaceView
{
    readonly TriangleMesh _mesh;
    readonly float[][] _frames;                    // the app's positions, V × 3 a frame
    readonly Mesh3DOptions _options = new()
    {
        // OpenCV camera axes: y down, z forward. Look from the camera at the surface.
        Home = new OrbitCamera(Eye: Vector3.Zero, Target: new Vector3(0f, 0f, 110f), Up: -Vector3.UnitY),
    };
    int _frame;
    bool _wire;

    public SurfaceView(uint[] faces, float[][] frames)
    {
        _frames = frames;
        _mesh = new TriangleMesh(faces, frames[0].Length / 3);
        _mesh.SetPositions(frames[0]);
    }

    public void Build(BibuiRuntime runtime)
    {
        _mesh.SetPositions(_frames[_frame]);       // only the positions change frame to frame
        _options.Wireframe = _wire;
        var view = runtime.Mesh3D("surface", _mesh, _options);
        view.Node.Sized(Size.Fill, Size.Px(480f));
    }
}
```
