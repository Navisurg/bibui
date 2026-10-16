# Proposal: the render seam

**Status:** proposed. Library change: agree it before any step is implemented.

## Why

`IRasterBackend` does four jobs, and only one of them is drawing:

| Job | Members | Who depends on it |
|---|---|---|
| Text | `LoadTypeface`, `MatchFamily`, `MatchCharacter`, `DefaultTypeface`, `MeasureText`, `GetMetrics`, `ShapeText`, the canvas's `DrawText` and `DrawGlyphRun` | Layout, through `Fonts`: the rasterizer you pick moves geometry |
| Image files | `DecodeImage`, `EncodePng` | Frame output (`RenderOutput.PngFactory`), tests |
| Surfaces | `CreateSurface` (twice), `IRasterSurface.ReadPixels` | The hosts: Wayland's shm buffers, Silk's GL surface |
| Drawing | `IRasterCanvas` | `DrawContext`, and through it the painter and every custom draw |

Every backend implements all four. A custom draw reaches the canvas directly, so what it draws is tied to whichever backend is behind it. A platform that doesn't fit the interface can't have a renderer at all.

## The frame

**build → layout → draw ops → render**

1. **Build:** unchanged. The app describes the tree.
2. **Layout:** unchanged, except that text is measured by the core's own text engine (FreeType + HarfBuzz) rather than by a backend.
3. **Draw ops:** the core walks the laid-out tree, with the dropdowns and the tooltip linked last as today, and records a `DrawList`. Custom draw callbacks record into the same list at the same point in the walk.
4. **Render:** the code that owns the loop (`BibuiWindow`'s host, a test, a headless renderer) hands the `DrawList` to the renderer it chose. The runtime holds no backend.

A renderer is any code that reads a `DrawList`. There is no renderer interface. bibui ships two: Skia (`Bibui.Raster.Skia`) and the managed rasterizer (`Bibui.Raster.Managed.Backend`).

## The `DrawList`

One per runtime, reset when a frame's recording starts. Renderers read it through read-only spans; only the core writes it. It's valid from the end of the frame's recording until the next frame starts recording. Everything in it is physical pixels with resolved colours: the scale factor and the theme are applied while recording, as `DrawContext` does today.

```csharp
public sealed class DrawList
{
    // The ops, in paint order. Reset per frame; the arrays only grow.
    public ReadOnlySpan<DrawOp> Ops { get; }

    // Variable-size data, which an op indexes by (Start, Count).
    public ReadOnlySpan<Vector2> Points { get; }    // points, positions, path points
    public ReadOnlySpan<PathVerb> Verbs { get; }    // path verbs
    public ReadOnlySpan<float> Floats { get; }      // arc parameters, gradient stops
    public ReadOnlySpan<Rgba> Colors { get; }       // gradient colours
    public ReadOnlySpan<ushort> Glyphs { get; }     // glyph ids
    public ReadOnlySpan<char> Chars { get; }        // text, until glyphs replace it

    // Shared, longer-lived things an op names by id.
    public PathTable Paths { get; }                 // cached paths (icons, check marks, arcs)
    public FaceTable Faces { get; }                 // font faces (text step)
}
```

**`DrawOp` is an unmanaged struct.** It's fixed-size, every op uses the fields it needs and leaves the rest, and the list is one array reused from frame to frame. This is the shape the managed rasterizer's damage recorder already uses (`DamageFrame`/`DamageOp`), moved into the core:

```csharp
public struct DrawOp
{
    public DrawOpKind Kind;
    public PaintSpec Paint;          // colour, fill/stroke, width, cap, join, AA, filter quality
    public RectF Rect;               // round rect, clip, texture destination, backdrop clip
    public float A, B, C, D;         // radii; line ends; text origin; blur sigmas; opacity
    public Matrix3x2 Matrix;         // PushTransform
    public int Start, Count;         // payload range: points, verbs, glyphs, chars
    public int Start2, Count2;       // a second range: glyph positions, gradient stops
    public int Resource;             // PathTable id, FaceTable id, gradient index
    public TextureHandle Texture;    // DrawTexture
    public PixelRect Region;         // BackdropBlur's read region
    public DashPattern Dash;         // On == 0: no dash
    public Shading Shading;          // none, or a gradient (kind, start, end, radius, colour and stop ranges)
}
```

**Nothing in an op is a reference.** That's the difference from `DamageOp`, which holds `object? Ref` for paths and images, and from `RasterPaint`, which holds a `RasterShader` class. Each of those becomes a value or an id:

- **Paths.** Path data is verbs plus points, described by bibui and not owned by any backend.
  - A cached path (`ctx.Path(key, build)`: icons, check marks, arcs) lives in the `PathTable` under a stable id, and a renderer caches its native path by that id.
  - A one-frame path (`DrawPolyline`, `DrawFilledPath`: chart geometry that changes every frame) is written straight into the payload arrays and built fresh by the renderer.
  - `IPathBuilder` stays the way you describe a path; its target becomes bibui's own path data instead of a `RasterPath`.
- **Gradients.** Kind, start, end and radius sit inline in `Shading`; the colours and stops are ranges into `Colors` and `Floats`. A renderer caches its native shader by the content key that `CacheKey.OfGradient` already computes.
- **Font faces.** An op names a face by `FaceTable` id; the renderer keeps its own native typeface per id (Skia: `SKTypeface.FromData` over the face's font file).
- **Textures.** An op carries a `TextureHandle` value (see Textures, below).

**Recording allocates nothing in a steady-state frame.** `DrawList.Push(kind)` returns a `ref DrawOp` to fill in place (as `DamageFrame.NewOp` does), and payload is appended to arrays that double when full and are never shrunk. A test pins this down with `GC.GetAllocatedBytesForCurrentThread` across a warmed-up frame.

## The op set

Every call `DrawContext` and the painter make on `IRasterCanvas` today, as an op:

| Today (`IRasterCanvas`) | Op | Notes |
|---|---|---|
| `Clear` | `Clear` | The frame's first op, with the theme's background |
| `Save` + `ClipRect` | `PushClip` | |
| `Save` + `Translate` | `PushTranslate` | `PushTranslation`, and the icon, check mark and arc placement |
| `Save` + `Translate` + `Scale` | `PushScale` | |
| `Save` + `SetMatrix(m × TotalMatrix)` | `PushTransform` | Records the relative matrix `m`; the renderer composes it with its current one, as the canvas does today |
| `SaveLayerAlpha` | `PushOpacity` | |
| `Restore` | `Pop` | Undoes the matching push, whichever it was |
| `DrawRoundRect` | `RoundRect` | Fill or stroke, a gradient (`Shading`), a mask blur (shadows) |
| `DrawLine` | `Line` | Dashed for dashed edges |
| `DrawPath` | `Path` | `Resource` is a cached path, or a payload range for a one-frame path |
| `DrawPoints` | `Points` | |
| `DrawImage`, `DrawPixels` | `Texture` | See Textures |
| `DrawBackdropBlur` | `BackdropBlur` | A renderer that can't read back its target skips it, as today |
| `DrawText` | `Text` | Until the text step; then `Glyphs` replaces it |
| `DrawGlyphRun` | `Glyphs` | Face id, size, glyph range, position range, origin, colour |

Things that are the core's job, not the renderer's, stay on the core side: hover and press colour blending, theme resolution, logical-to-physical scaling, dash lengths, shadow geometry, and animation (`ctx.Animate`, `ctx.Sawtooth`, and the "still animating" answer the loop reads).

The vocabulary grows when an app needs more, as ops and paint fields every renderer executes: a 4×5 colour matrix, a 256-entry LUT, blend modes, a source rect on `Texture`, texture formats beyond RGBA. There's no escape-hatch op that hands a renderer's native canvas to the app. It can be added if something turns out to need one, and nothing does yet.

## Recording

`DrawContext` keeps its public methods. They record instead of drawing:

- **Transforms are relative.** `PushTransform` records `m` and the renderer applies `m × current`. That's what `Canvas.SetMatrix(m * Canvas.TotalMatrix)` computes today, so the core never needs to track the renderer's matrix.
- **Text advances come from `Fonts`.** Today `DrawRuns` moves its cursor by what `DrawText` returns. Both backends return their measured advance (Skia: `paint.MeasureText`; managed: the strike's width), which is the same number `Fonts`' measure cache holds. Recording advances by that.
- **Custom draws get their `Box`:** the callback is `delegate void DrawCallback(Box box, DrawContext ctx)`. It runs at the same point in the walk (after the box's children, so on top of them) and records into the same list. It reads `HotT`, `ActiveT` and `Id` from the box for animation, and the box's `Rect` is its canvas.
- **`DrawContext.Canvas` and `DrawContext.Backend` are removed.** Nothing in bibui's samples uses them.
- **`ctx.Path(key, build)` returns a path id instead of a `RasterPath`.** Callers that pass it straight to `ctx.DrawPath` don't change.

## Textures

The draw and UI layers know textures, never images or image files.

```csharp
public readonly record struct TextureHandle(ulong Value)
{
    public static TextureHandle None => default;
}
```

- **A renderer gives out handles and knows what they mean.** For example, an index into its own texture table plus a generation, so a stale handle is caught instead of drawing garbage. `None` (zero) draws nothing and is never an error, like rad's nil handles.
- **Usage code creates textures by calling its renderer directly**, e.g. `SkiaTextures.Create(width, height, format, pixels)`, and owns the lifetime: create, update, release. bibui has no image component. How image bytes reach memory (a file, a video decoder, the network) is the app's business, and an app that shows images knows which renderer it runs on. This is what the RAD debugger does: its bitmap view calls `r_tex2d_alloc` itself and caches the handle.
- **A GL renderer creates textures on its context's thread.** The build runs on the render thread in every bibui host today, so creating a texture while building works. That stays the caller's responsibility, as it is in rad.
- **The codecs stay in bibui for now** (the PNG codec, frame PNG output). Only the draw layer stops knowing them.
- **Kinds come later.** Static (uploaded once), Stream (one texture re-uploaded wholesale, e.g. video) and Dynamic (updated a region at a time, e.g. an atlas) are a separate step after the seam is rebuilt. Until then every texture behaves as Static.

## Rendering

```csharp
// Skia: onto a canvas the caller owns, GPU or CPU.
SkiaDraw.Execute(DrawList list, SKCanvas canvas);

// Managed rasterizer: onto pixels the caller owns. Damage tracking happens here.
ManagedDraw.Execute(DrawList list, RasterPixels target);
```

- **The hosts own their renderer.** Silk draws with Skia: on its GPU surface, or on a CPU surface it then uploads. Wayland draws into its shm buffers with the managed rasterizer, or with Skia on the CPU. `IPlatformHost.Present` calls the runtime to build, lay out and record, then executes the list itself. Choosing the renderer moves from the runtime (`BibuiRuntime.Create().WithSkia()`) to the window and host (`BibuiWindow.Create(runtime).WithSilk()` picks Skia).
- **Headless rendering and tests** call a renderer directly. `Bibui.Testing` (screenshots, PNG frames) renders with Skia on the CPU, so `RenderOutput` and frame PNGs move out of the core to where a renderer is.
- **Damage tracking stays in the managed rasterizer only.** Before rasterizing, it compares this frame's `DrawList` with the last one, as `DamageFrame` does today with the calls it recorded. The hashes, bounds and replay logic stay; only their input changes. Cached paths hash once, by table id and version. A texture hashes by its handle plus a content version the renderer bumps when the texture is updated. Skia doesn't track damage: drawing is likely cheaper than working out what changed.

## Getting there without changing a pixel

Each step's gate is the same: the runtime tests (with the byte-exact `RenderTo`/`Render` parity test), `damage-tests`, and the fixture suites, all unchanged.

1. **Text out of the rasterizer.** `Fonts` runs on the core's FreeType + HarfBuzz text engine, and the canvas's only text call becomes `DrawGlyphs`. This is already decided (the text-engine proposal).
2. **Textures replace images in the draw layer.** `TextureHandle`, `DrawTexture`, and each renderer's texture functions. `DrawImage`, `DrawBitmap` and `DrawPixels` leave `DrawContext`.
3. **The `DrawList`.** `DrawContext` records. A replay function executes the list onto the existing `IRasterCanvas`, so the interface survives this step but only the replay calls it, and byte-identical pixels prove the op set is complete. Anything not yet converted to a value (a `RasterImage` before step 2, a backend typeface before step 1) rides along as an index into a per-frame object list that steps 1, 2 and 5 empty.
4. **Custom draws get the `Box`** (`DrawCallback(Box box, DrawContext ctx)`) and lose `Canvas` and `Backend`.
5. **Renderers replace `IRasterBackend`.** `SkiaDraw` and `ManagedDraw`, the hosts executing the list, damage tracking over `DrawList`s, and `IRasterBackend` and `IRasterCanvas` deleted.
6. **Paint vocabulary:** colour matrix, LUT, blend modes, source rect, texture formats.
7. **Texture kinds:** Static, Stream, Dynamic.

1, 2 and 3 can run in parallel. 3 carries text as `Text` ops replayed through the old `DrawText` until 1 lands. 4 needs 3, and 5 needs 1 to 4.

## Decided

- **Callbacks** are `DrawCallback(Box box, DrawContext ctx)`.
- **`DrawList` access** is read-only spans; only the core writes the list.

## Open questions

1. **Owner ids:** ops carry no reference to the box that recorded them. One could be added (an `int` per op) if a draw-list dump or the damage-debug overlay needs to name boxes; nothing needs it yet.
2. **Frames and pixels:** `runtime.Render()` both runs a frame and turns it into pixels. The proposed split:
   - `runtime.Frame()` builds, lays out and records the `DrawList`;
   - pixels come from a renderer (`SkiaDraw.Render(list, size)` returns a `RenderOutput`, and likewise for `ManagedDraw`);
   - `RenderOutput` stays a plain data type in the core, with PNG through the core codec;
   - `Bibui.Testing`'s `BibuiPage` takes a renderer when it is created (Skia on the CPU by default) and keeps `Screenshot()`.
