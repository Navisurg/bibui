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
    public ReadOnlySpan<Vector2> Points { get; }    // points, glyph positions
    public ReadOnlySpan<PathVerb> Verbs { get; }    // path verbs
    public ReadOnlySpan<float> Floats { get; }      // path verb arguments, gradient stops
    public ReadOnlySpan<Rgba> Colors { get; }       // gradient colours
    public ReadOnlySpan<ushort> Glyphs { get; }     // glyph ids

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

- **Paths.** A path is bibui's own `Path`: a sealed class with `MoveTo`, `LineTo`, `QuadTo`, `CubicTo`, `ArcTo`, `AddArc` and `Close`, which writes verbs and their float arguments. It's today's internal `RecordedPath`, made public. `IPathBuilder` and `RasterPath` go: with paths as core data there's one kind of path, so nothing to abstract over.
  - A cached path (`ctx.Path(key, build)`: icons, check marks, arcs) lives in the `PathTable` under a stable id, and a renderer caches its native path by that id. `build` is handed a `Path`, so callers that call its methods don't change.
  - A one-frame path (`DrawPolyline`, `DrawFilledPath`: chart geometry that changes every frame) is written straight into the payload arrays and built fresh by the renderer.
  - The SVG icon parser writes into a `Path` directly.
  - A table entry can be a path scaled by a factor (an icon at its size). The renderer builds the native path and then scales it, as `AddPath(source, scale)` does today. Skia turns `ArcTo` into curves before the scale, so scaling the arguments first would move pixels.
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

- **A host is a module that runs its own loop, with its own renderer.** `SilkHost.Run(runtime, …)` pumps the window's events into the runtime, calls `runtime.EndBuild()`, and executes the list with Skia: on its GPU surface, or on a CPU surface it then uploads. `WaylandHost.Run(runtime, …)` does the same into its shm buffers with the managed rasterizer, or with Skia on the CPU.
  - **`IPlatformHost` and `ISkiaGpuPlatformHost` go.** `BibuiWindow` only exists to drive a host through `IPlatformHost`. Its shared loop code (frame pacing, the frame profile) becomes functions each host calls.
  - **Choosing the renderer moves from the runtime to the host.** `BibuiRuntime.Create().WithSkia()` and `BibuiWindow.Create(runtime).WithSilk()` become `SilkHost.Run(runtime, …)`, which draws with Skia. This changes the entry point every app uses.
  - Tests need no fake host (`ScriptedHost`): they call `runtime.EndBuild()` and render directly.
- **Headless rendering and tests** call a renderer directly: render the `DrawList` into pixels, and encode them as a PNG if wanted. `Bibui.Testing` does this with the renderer it is given to keep `Screenshot()`. `IBibuiPage` (one implementation, `BibuiPage`) goes.
- **Damage tracking stays in the managed rasterizer only.** Before rasterizing, it compares this frame's `DrawList` with the last one, as `DamageFrame` does today with the calls it recorded. The hashes, bounds and replay logic stay; only their input changes. Cached paths hash once, by table id and version. A texture hashes by its handle plus a content version the renderer bumps when the texture is updated. Skia doesn't track damage: drawing is likely cheaper than working out what changed.

## Interfaces

bibui has 16 interfaces. What happens to each:

| Interface | Implementations | What happens |
|---|---|---|
| `IRasterBackend`, `IRasterCanvas`, `IRasterSurface` | Skia, managed | Go: the `DrawList` and the renderers replace them |
| `IPathBuilder` | `RasterPath`, `RecordedPath` | Goes: one concrete `Path` |
| `IPlatformHost`, `ISkiaGpuPlatformHost` | Silk, Wayland, a test host | Go: hosts are modules that run their own loop |
| `IBibuiPage` | `BibuiPage` | Goes: one implementation |
| `IPathStroker`, `IMaskBlur` | one each, plus a test fake | Go, outside this rebuild: the managed rasterizer calls its stroker and blur directly instead of through optional properties |
| `IWaylandFeature`, `IPointerCursorHooks`, `ICursorSink`, `IClipboardWire`, `IBufferAllocator` | Wayland's clipboard, input, cursor and buffers, plus test fakes | Go, outside this rebuild: known features called directly, a cursor kind chosen by an enum, and tests over the data the wire carries instead of injected fakes |
| `ICoverageSink`, `IHairlineSink` | the managed rasterizer's sinks | **Stay.** They're used only as generic constraints (`where TSink : struct, ICoverageSink`), so each sink gets its own inlined copy of the scanline loop with no virtual calls. That's compile-time specialization, not dispatch |

## Getting there without changing a pixel

Each step's gate is the same: the runtime tests, `damage-tests`, and the fixture suites, all unchanged. Until step 5, the runtime tests include the byte-exact `RenderTo`/`Render` parity test; it is deleted with those two methods, and from then on the fixture suites hold the pixels.

1. **Text out of the rasterizer.** `Fonts` runs on the core's FreeType + HarfBuzz text engine, and the canvas's only text call becomes `DrawGlyphs`. This is already decided (the text-engine proposal).
2. **Textures replace images in the draw layer.** `TextureHandle`, `DrawTexture`, and each renderer's texture functions. `DrawImage`, `DrawBitmap` and `DrawPixels` leave `DrawContext`.
3. **The `DrawList`.** `DrawContext` records. A replay function executes the list onto the existing `IRasterCanvas`, so the interface survives this step but only the replay calls it, and byte-identical pixels prove the op set is complete. Text and textures are already values by then (steps 1 and 2), so every op is a value from the start.
4. **Custom draws get the `Box`** (`DrawCallback(Box box, DrawContext ctx)`) and lose `Canvas` and `Backend`.
5. **Renderers replace `IRasterBackend`.** `SkiaDraw` and `ManagedDraw`; damage tracking over `DrawList`s; hosts as modules that run their own loop and execute the list. `IRasterBackend`, `IRasterCanvas`, `IRasterSurface`, `IPlatformHost`, `ISkiaGpuPlatformHost` and `IBibuiPage` are deleted.
6. **Paint vocabulary:** colour matrix, LUT, blend modes, source rect, texture formats.
7. **Texture kinds:** Static, Stream, Dynamic.

1, 2 and 3 run in that order. Text goes first: layout stops depending on the rasterizer, and the riskiest step (measuring with FreeType, proven only by the fixtures) is retired before anything builds on it. 4 needs 3, and 5 needs 1 to 4.

## Decided

- **Callbacks** are `DrawCallback(Box box, DrawContext ctx)`.
- **`DrawList` access** is read-only spans; only the core writes the list.
- **No owner ids.** An op doesn't record which box drew it. One can be added (an `int` per op) if a draw-list dump or the damage-debug overlay ever needs to name boxes.
- **`runtime.EndBuild()` ends a frame's building:** it runs the build function given to `Mount`, lays out, and issues the draw calls, recording the `DrawList`. `Mount` stays how an app hands the runtime its UI. When it returns, the frame's input has been consumed, the tree is laid out, and `runtime.DrawList` describes the frame; nothing has been drawn. Drawing is the caller's next step, with its renderer:

  ```csharp
  runtime.EndBuild();
  SkiaDraw.Execute(runtime.DrawList, canvas);
  ```

  `runtime.Render()` and `runtime.RenderTo(surface)` are removed. Today they build, lay out, paint and (for `Render()`) read the pixels back in one call. The runtime's internal `BeginBuild`/`EndBuild` frame markers (tooltip and dropdown bookkeeping, sweeping unused retained boxes) are renamed to make room. The `DrawList` is valid until the next frame starts building.
- **Pixels and PNGs come from rendering the `DrawList`,** like any other frame: a renderer draws it into pixels in memory, and whoever wants a PNG encodes those (the codec stays in the core). There is no frame-output path in the core: `RenderOutput` and `PngFactory` go. `Bibui.Testing` renders with a renderer it is given (Skia on the CPU by default) to keep `Screenshot()`.

## Later

- **Cached paths drawn as textures.** Icons and check marks are the same shape every frame. A renderer may rasterize a cached path once into its own mask atlas (keyed by path id, scale and stroke) and draw quads from it, tinted by the op's colour. This is the renderer's choice, behind the op: the core keeps drawing paths, and creates no textures. A mask is drawn at whole-pixel offsets where icons today land on fractional ones, so pixels would shift slightly. It's an optimization to measure after the rebuild, not part of it. Content an app draws itself (a chart, a dome) can be drawn once into a texture by the app, which needs a renderer to draw ops into a texture (an off-screen target), not yet in any step.

- **A frame bracket:** `runtime.BeginBuild(); BuildUi(runtime); runtime.EndBuild();`, with the app running its own build code between the two calls instead of handing the runtime a callback with `Mount`. It changes every app's entry point, so it is not part of this rebuild.
