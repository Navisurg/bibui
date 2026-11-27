# Mesh3D

A triangle mesh in a box, or a scene of several, seen through an orbit camera:
drag to turn it round, pan and zoom, or set the camera from code by a pose. The faces are fixed; the vertex positions can be replaced every
frame, so the mesh can move and deform. It is lit from the camera (a
headlight and a little ambient light, or a point light that falls off with
distance, as an endoscope's) with normals computed from the current
positions, and both sides of a face are drawn. Vertices carry a colour, or a
scalar through a colormap, or texture coordinates into a picture. Points and
line strips can be drawn over it, and its edges as a wireframe. A point in the
view can be picked (which mesh, which face, where on it), and the same
drawing can be made without a window into buffers of colour, depth, mesh and
face ids and normals.

> **Status.** On the branch `bib/add-a-3d-mesh-view-to-bibui-an-o` until it is
> merged. Every renderer draws it the same way: the core rasterises the mesh on
> the CPU, z-buffered and spread over the cores, into pixels the renderer draws
> as an image (`DrawOpKind.Pixels`): Skia on the CPU, Skia on GL (which uploads
> them each frame the mesh changes) and the managed rasteriser. There is no
> GPU rasteriser for the mesh itself yet; see [Speed](#speed).

## Build

```cs
public static Mesh3DWidget Mesh3D(
    this BibuiRuntime runtime,
    string id,
    TriangleMesh mesh,
    Mesh3DOptions? options = null)

public static Mesh3DWidget Mesh3D(
    this BibuiRuntime runtime,
    string id,
    MeshScene scene,
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
| `scene` | Several meshes to draw, each placed by a transform; see [A scene](#a-scene). |
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

### A scene

```cs
public sealed class MeshScene
{
    public int Add(TriangleMesh mesh, Matrix4x4? transform = null);   // the mesh's id: 0, 1, 2… in the order added
    public int Count { get; }
    public TriangleMesh this[int id] { get; }
    public Matrix4x4 TransformOf(int id);
    public void SetTransform(int id, Matrix4x4 transform);              // the mesh's coordinates to the world's
    public bool IsVisible(int id);
    public void SetVisible(int id, bool visible);
    public void Clear();                                                // no meshes; ids start at 0 again
    public (Vector3 Min, Vector3 Max) Bounds { get; }                   // of the visible meshes, in the world
}
```

- A mesh's id is the order it was added in, and is what picking and the id
  buffer answer. Build the scene once and change it between frames; a mesh
  may be in several scenes, or twice in one with two transforms.
- `transform` maps the mesh's own coordinates to the world's, as
  `System.Numerics` writes it (a row vector times the matrix: scale, then
  rotate, then translate is `S * R * T`). It may scale, unevenly too: normals
  are carried by its inverse transpose. Null is the identity.
- Each mesh keeps its own colours, scalars or texture. A hidden mesh is not
  drawn, picked or counted in `Bounds`.
- `runtime.Mesh3D(id, mesh)` is a scene of one, with the identity transform.

### The camera

```cs
public readonly record struct OrbitCamera(Vector3 Eye, Vector3 Target, Vector3 Up, float FovY = 45f)
{
    public float Near { get; init; }                                  // the near plane's distance; 0: 0.1 % of Distance
    public float Distance { get; }                                    // |Eye − Target|
    public Quaternion Orientation { get; }                            // the camera's axes in the world (below)
    public OrbitCamera Orbit(float yawDegrees, float pitchDegrees);   // about Up, then about the view's right
    public OrbitCamera Pan(float right, float up);                    // moves Eye and Target, in world units
    public OrbitCamera Zoom(float factor);                            // Distance × factor, toward Target
    public static OrbitCamera Fit(Vector3 min, Vector3 max, Vector3 viewDirection, Vector3 up, float fovY = 45f);
    public static OrbitCamera FromPose(Vector3 position, Quaternion orientation, float fovY, float targetDistance);
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

#### A camera from a pose

An endoscope, a tracked instrument or a recorded camera has a pose: where it
is and which way it is turned. `OrbitCamera.FromPose` makes the camera that
sees what it sees:

- `orientation` turns the camera's own axes into the world's. The camera's
  axes are OpenCV's: x to the right of the picture, y down it, z straight
  ahead. So `Quaternion.Identity` at the origin looks along +z with -y up,
  and the camera's z axis in the world is `Vector3.Transform(Vector3.UnitZ, orientation)`.
- `fovY` is the vertical field of view in degrees, the full angle; wide
  angles to 120° and more are drawn without distortion other than the
  perspective's own.
- `targetDistance` places the target that far ahead: what an orbit turns
  about and a zoom moves toward, if the view is interactive.

`Orientation` reads the pose back from any camera, and `FromPose(c.Eye,
c.Orientation, c.FovY, c.Distance)` is `c` again (with its up made square to
the view). A camera's `Up` is the axis it orbits about; a camera from a pose
orbits about its own up.

#### The near plane

Whatever is closer to the eye than `Near`, along the view's axis, is cut
away: a face that crosses the plane is clipped to it, so a surface the eye
is close to shows its cut edge and no hole. `Near` is 0 by default, which
means 0.1 % of the distance to the target. A camera from a pose should set
it to what the real camera can see, e.g. `FromPose(p, q, 100f, 30f) with
{ Near = 0.5f }` in millimetres. Depth is kept as 1/z in 32-bit floats, so
a small `Near` costs no precision far away.

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
    public CameraLight? Light { get; set; }           // null: the headlight; set: a point light at the eye
    public Rgba? Background { get; set; }             // null: the box shows through where there is no mesh
    public bool Interactive { get; set; } = true;     // false: the view takes no input
    public MeshOverlay? Overlay { get; set; }         // points and lines drawn over the mesh
    public bool OverlayHiddenByMesh { get; set; }     // overlay points behind the mesh are not drawn
    public bool ShowMesh { get; set; } = true;        // false: only the overlay
    public int Supersample { get; set; } = 1;         // 2: a smooth outline and wireframe, for 4× the rasterising
}

public sealed class MeshOverlay
{
    public void Clear();
    public void Points(ReadOnlySpan<Vector3> points, Rgba color, float size = 6f);   // round dots, size in px
    public void LineStrip(ReadOnlySpan<Vector3> points, Rgba color, float width = 2f); // a polyline, width in px
    public void Lines(ReadOnlySpan<Vector3> pairs, Rgba color, float width = 2f);    // separate segments, two points each
}
```

Overlay points and lines are drawn after the mesh, antialiased. By default
the mesh does not hide them: a track behind the surface still shows. With
`OverlayHiddenByMesh`, each overlay point is tested against the mesh's depth
where it lands, with a tolerance of 1 % of its depth so a track drawn on the
surface stays; a hidden point is not drawn and a line breaks there (a segment
between two visible points is drawn whole). Fill the overlay again whenever
what it shows changes (`Clear`, then add), and keep the instance.

Overlay points closer than the near plane are left out, and so is a line
segment that reaches one; faces are clipped to it (see [The near plane](#the-near-plane)).

### The light

By default the view is lit by a headlight: light from the eye that does not
weaken with distance, so a surface is as bright far off as close up, and
`Ambient` adds light everywhere. With `Light` set, the light is a point at
the eye, as an endoscope's is, and it falls off with the square of the
distance:

```cs
public readonly record struct CameraLight(float Intensity, float SpotExponent = 0f);
```

A surface point at distance `r` from the eye, whose normal makes the angle
`θ` with the way back to the eye, and which lies at the angle `α` off the
view's axis, is lit

```
Ambient + Intensity · cos θ · cosⁿ α / r²        (n = SpotExponent)
```

times its colour (and texture), each channel clamped at 1; nothing maps it
through a gamma. Both sides of a face are lit alike. `Intensity` is the light a surface square to the axis gets
at distance 1, in the scene's units: `2500` lights a surface 50 mm off fully
when the scene is in millimetres. `SpotExponent` 0 lights every direction
alike; larger narrows the beam (cos¹⁰ α is half at 21°). Leave `Ambient` at 0
for an endoscope's picture, where nothing is lit but by its own light.

The point light is computed for every pixel, from the normal and the
distance there, so the falloff across a large face close to the eye is
right; the headlight is computed at the vertices.

## Picking

```cs
public static MeshHit? Mesh3DPick(this BibuiRuntime runtime, string id, Vector2 point)

public readonly record struct MeshHit(int Mesh, int Face, Vector3 Barycentric, Vector3 Point, float Depth);
```

What lies under `point` (logical pixels in the window, as `Interaction.X` and
`.Y` are) in the view named `id`, as last drawn, or null where it shows no
mesh:

- `Mesh` is the mesh's id in the scene (0 for a view of one mesh), and `Face`
  the face's index in that mesh.
- `Barycentric` weighs the face's three vertices, in the order the face
  names them, to give `Point`: `Point = b.X·p₀ + b.Y·p₁ + b.Z·p₂` with the
  vertices placed by the mesh's transform. Interpolate anything per vertex
  (a branch label, a scalar) with the same weights.
- `Point` is in the world, where the ray through `point` meets the face;
  `Depth` its z in the camera's axes (below).

A pick reads the face drawn at that pixel and intersects the ray with it,
so it is exact within the face and costs the same for any size of scene.
Pick on a click: `if (view.Interaction.Clicked) hit = runtime.Mesh3DPick(id,
new(view.Interaction.X, view.Interaction.Y));`.

## Without a window

```cs
public sealed class MeshBuffers
{
    public MeshBuffers(int width, int height);
    public int Width { get; }
    public int Height { get; }
    public bool Normals { get; set; }                  // also draw the normal at each pixel

    public void Render(MeshScene scene, OrbitCamera camera, Mesh3DOptions? options = null);

    public ReadOnlySpan<byte> Color { get; }           // RGBA8, straight alpha, rows tight, top row first
    public ReadOnlySpan<float> Depth { get; }          // z in the camera's axes; +∞ where no mesh
    public ReadOnlySpan<int> MeshIds { get; }          // the mesh's id in the scene; -1 where none
    public ReadOnlySpan<int> FaceIds { get; }          // the face's index in its mesh; -1 where none
    public ReadOnlySpan<float> NormalXyz { get; }      // 3 a pixel, in the camera's axes, facing the eye; 0 where none
    public MeshHit? Pick(Vector2 pixel);               // as Mesh3DPick, in this buffer's pixels
}
```

The same rasteriser the view uses, into buffers the app reads: a geometry
pass for a simulator, ground truth for a test, a picture for a
command-line tool. It needs no runtime, window or renderer. Pixel `(x, y)`
covers `[x, x + 1) × [y, y + 1)` and is sampled at its centre; the camera's
axis passes through the image's centre, `(Width / 2, Height / 2)`, and its
focal length in pixels is `Height / 2 / tan(FovY / 2)`.

- **Depth** is the distance along the camera's axis (z in camera
  coordinates), not along the ray: a plane square to the axis has one depth
  across the image. The distance along the ray is `Depth · |(x + ½ − cx, y + ½ − cy, f)| / f`.
- **The camera's axes** are OpenCV's, as a pose's are: x right, y down, z
  ahead. Normals are in them, unit length, turned toward the eye.
- `Color` is what the view would show (with `Background` where there is no
  mesh, transparent when null); the overlay is not drawn into it, and
  `Supersample` is not used: every buffer has one sample a pixel.
- Each buffer is converted from the rasteriser's own when it is first read
  after a `Render`, so a buffer the app does not read costs nothing.
  `Normals` costs a little in the rasteriser, so it is off until asked for.
- The spans are valid until the next `Render`; copy what must outlive it.

```cs
var buffers = new MeshBuffers(640, 480) { Normals = true };
var cam = OrbitCamera.FromPose(tip, tipOrientation, fovY: 100f, targetDistance: 30f) with { Near = 0.5f };
buffers.Render(scene, cam, new Mesh3DOptions { Ambient = 0f, Light = new CameraLight(2500f, SpotExponent: 2f) });
float z = buffers.Depth[240 * 640 + 320];          // how far ahead the surface is, at the centre
int vessel = buffers.MeshIds[240 * 640 + 320];      // which mesh is there
```

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

The `TriangleMesh` or `MeshScene`, the `Mesh3DOptions`, the `MeshOverlay`
and the `MeshBuffers`: keep each one and change it between frames. The camera is kept by the runtime under `id`; set
it with `SetMesh3DCamera`.

## Speed

The mesh is drawn at the box's size in physical pixels. It is drawn again
only when something it depends on changed: a mesh (any `Set…` call), the
scene (a mesh added, placed, shown or hidden), the camera, the size or the
look; a frame that changes none of them reuses the pixels. Normals are
recomputed once per change of positions, in parallel. Vertices are
transformed in parallel chunks; triangles are sorted into horizontal bands
of rows, and each band filled by one worker from its own triangles. Under a
point light, what each pixel's ray alone decides of its light is a table
made again only when the size, the field of view or the spot exponent
changes.

Measured on a 20-core laptop (Ryzen AI, Radeon 880M), a 317 × 317 grid
(100,489 vertices, 199,712 triangles) with every position replaced each
frame, at 1280 × 800:

| Path | Frame |
|---|---|
| Build, normals and rasterising (`tests/bibui-runtime-tests`, "Mesh3D: 100k") | 8–13 ms |
| The same on `SkiaGlRenderer`, GPU upload and draw included (`samples/mesh3d-gl-bench`) | 9.8 ms: 8.7 CPU + 1.1 GL |

A scene of an organ-sized ellipsoid and 40 tubes over it (41 meshes;
`tests/bibui-runtime-tests`, "Mesh3D scene: 1M and 2M"), drawn into
`MeshBuffers`, the median of nine frames with the camera turning. *Orbit*
sees the whole scene from outside; *endoscope* is a 100° pose camera close
to the surface under the point light, with faces clipped to the near plane;
*positions* is the orbit view with every mesh's positions replaced each
frame, so its normals are made again. On a 10-core, 20-thread Ryzen AI 9 365
shared with other work (load about 12), so these are upper bounds:

| Triangles | Size | Orbit | Endoscope | Positions |
|---|---|---|---|---|
| 1.0 M | 640 × 480 | 14 ms | 20 ms | 18–21 ms |
| 1.0 M | 1280 × 800 | 16–18 ms | 31–36 ms | 16–17 ms |
| 1.0 M | 1920 × 1080 | 12–15 ms | 38–41 ms | 17–18 ms |
| 2.0 M | 640 × 480 | 11–14 ms | 19–22 ms | 20–21 ms |
| 2.0 M | 1280 × 800 | 16 ms | 30–32 ms | 24–29 ms |
| 2.0 M | 1920 × 1080 | 19 ms | 43–47 ms | 26–28 ms |

At two million triangles the orbit view's frame is mostly per-vertex and
per-face work (projecting 1.2 M vertices, boxing and sorting 2 M faces), so
it hardly grows with the size. The endoscope view's is mostly filling: a
face close to the eye covers much of the picture, every pixel of its box is
tested, and the light is worked out at each pixel a face draws, hidden ones
included.

### A GPU path

The CPU path is the default and the one every renderer has. A view drawn by
the GPU instead would need:

- a draw op that asks the renderer to draw the scene in place, beside
  `DrawOpKind.Pixels`, which renderers without a GPU (Skia on the CPU, the
  managed rasteriser) answer with today's pixels;
- in `SkiaGlRenderer`, a vertex and index buffer for each mesh, uploaded
  when the mesh's `Version` changes (indices once), the transform and the
  camera as uniforms, and one shader pair that projects as `Project` does
  and lights as the headlight or `CameraLight` do, per pixel;
- for `MeshBuffers` and picking, a framebuffer with several colour
  attachments (colour, mesh and face id as integers, normal) and depth,
  read back with `glReadPixels`, all of it for buffers and one pixel for a
  pick; a GL context without a window (EGL with a pbuffer) for headless use;
- tests that hold the GPU's pictures to the CPU's within a tolerance, not
  byte for byte: GPUs rasterise edges and round depth their own way.

It would draw two million triangles in a few milliseconds. It is not built:
at the sizes above the CPU path keeps a viewer interactive, and the
simulator's offscreen frames need no window.

`Supersample = 2` rasterises four times the pixels: use it for meshes of a
few tens of thousands of vertices, or a smaller box.

`samples/mesh3d-gl-bench` opens a GLFW window that is never shown and draws
into a framebuffer of its own, so it runs without putting anything on screen:

```bash
dotnet run --project samples/mesh3d-gl-bench -c Release -- 120 /tmp/mesh3d-gl.png
```

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
    int _frame;                                    // the frame shown: a time slider sets it
    bool _wire;                                    // a switch sets it

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

        var slider = runtime.Slider("frame", _frame, min: 0f, max: _frames.Length - 1, step: 1f);
        _frame = (int)slider.Value;
        if (runtime.Switch("wire", _wire, "Wireframe").Clicked) _wire = !_wire;
    }
}
```
