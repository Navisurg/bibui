# Changelog

All notable changes to bibui are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and bibui follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

bibui 0.9, the first public release: a frame model of build, layout and
paint, the library split into packages by what they depend on, and a second
route to the screen with no native drawing dependency. The entries below are
the changes since the private, callback-based versions bibui was used as
before it was published.

### Changed

- **SkiaSharp 4.153.1** (from 2.88.8) for `Bibui.Raster.Skia`.
  - Images are drawn with Skia's sampling options; `FilterQuality` maps to
    them as Skia mapped its old filter qualities (None nearest, Low linear,
    Medium linear with mipmaps, High Mitchell cubic).
  - Text is drawn with an `SKFont` (subpixel, grayscale, full hinting), and
    native paths are made with `SKPathBuilder`. A path a frame carries in its
    payload is cached by its content, so one drawn the same way every frame is
    built once.
  - The fixture suites' pixels are unchanged. 2.88's CPU runtime effects
    crashed on recent CPUs; 4.x runs them.
  - **Slower gradients on the CPU (`SkiaCpuRenderer`).** Skia dropped the
    code generator (SkVM) its CPU gradients ran on: a full-window 1440×900
    linear gradient takes about 35 ms a frame instead of 1.2 ms, a radial one
    about 27 ms instead of 14 ms (measured on a Ryzen AI 9 365). On the GPU
    (`SkiaGlRenderer`) and with `ManagedRenderer` nothing changes: on the
    CPU, prefer `ManagedRenderer` for gradient-heavy screens.

- **Skia is two renderers, and GL is a layer of its own (breaking).**
  `SkiaRenderer` is `SkiaGlRenderer` (a window's GL context: `new
  SkiaGlRenderer(window.Gl)`, `SkiaGlRenderer.ForGl`) and `SkiaCpuRenderer`
  (memory: `new SkiaCpuRenderer()`, `new SkiaCpuRenderer(window.Cpu)`).
  - The new package `Bibui.Gl` holds `GlTarget` (moved from `Bibui.Draw` to
    `Bibui.Gl`) and `GlTextures`, the GL texture calls, through
    Silk.NET.OpenGL. `Bibui.Platform.Silk` makes the `GlTarget`;
    `SkiaGlRenderer` makes its textures with `GlTextures` and wraps each once
    for Skia. Neither references the other.
  - Drawing into a window is bracketed: `BeginDraw(window)` readies it (a
    Silk window's GL context current; a Wayland window's free `wl_shm` buffer
    in `window.Cpu`), the renderer draws, `EndDraw(window)` puts it on screen
    (a swap; a commit). `EndFrame` only paces the loop. `GlCpuFramebuffer`
    has the same pair, inside the Silk window's.
  - A Silk window has no `window.Cpu`: it shows only what is drawn into its GL
    framebuffer (`window.Gl`). A CPU renderer on a
    Silk window draws into `GlCpuFramebuffer(window.Gl).Cpu` (`Bibui.Gl`),
    whose `BeginDraw()` sizes the memory and `EndDraw()` uploads it into a BGRA8
    texture and blits it onto the framebuffer: no shaders. Reading
    `window.Cpu` used to switch on an upload every frame, over whatever a GPU
    renderer had drawn.
  - Every renderer has the same `CreateTexture`, `UpdateTexture`,
    `ReleaseTexture` and `Draw(DrawList)`, so an app can name its renderer in
    one place (`global using Renderer = ...`) and swap it there.

- **Textures have a format and a kind (breaking).**
  `CreateTexture(format, kind, width, height, pixels, rowBytes = 0)` replaces
  `CreateTexture(rgba, width, height)`, and `UpdateTexture(texture, pixels,
  rowBytes = 0)` takes the whole texture at the size and format it was made
  with.
  - Formats, rad's names: `RGBA8` and `BGRA8` (straight alpha), `R8`, `RG8`
    and `A8`. Each draws with a default look: R8 grey, A8 white coverage, RG8
    as red and green.
  - Kinds: a `Static` texture is made once; an update to it is ignored. A
    `Stream` texture is overwritten in place: on `SkiaGlRenderer` it is one GL
    texture each update is uploaded into, wrapped by one `SKImage` for its
    life, where an update used to make a new image and a new GPU texture.
  - `rowBytes` takes padded rows, as a video decoder's are.
  - A texture no longer changes size: release it and make another.

- **One thread owns frames and input: the app's loop (breaking).**
  - A frame's input goes in with the frame: `runtime.BeginBuild(window.Events)`
    turns what the keyboard and mouse did into the frame's events, and empties
    the list. `BeginFrame` no longer applies input, and `WindowInput` is gone.
  - Every `WindowEvent` carries the modifiers held when it happened, stamped by
    the platform from the OS's state at that moment; the runtime keeps no
    record of them. `SilkWindow.HeldModifiers` is gone.
  - `runtime.UnconsumedEvents` gives the events themselves:
    `foreach (var ev in runtime.UnconsumedEvents) if (...) ev.Eat();`.
    `BibUiEventHandle` is gone; `Eat`, `Consumed` and `Next` are on
    `BibUiEvent`. Events come from a pool the end of each frame empties, so
    input allocates nothing per frame; an event lives until `EndBuild`.
  - The app steps its `BibuiTestServer` from its loop:
    `runtime.BeginBuild(server?.Step(window.Events) ?? window.Events)`.
    `Step` runs the command in hand a frame at a time and, while one runs,
    gives the frame's keyboard and mouse events in place of the window's. A click is
    a move, a press and a release, each in a frame of its own; `press` with
    `ctrl` presses Ctrl around the key. Commands, screenshots and recordings
    all run on the loop's thread.
  - `runtime.FrameLock` and the runtime's internal locks are gone. The runtime
    is the loop's: only `RequestFrame` (and `WakeUp`), `FramesRequested` and
    `FrameCount` are safe from other threads.
  - `runtime.DispatchFill` is gone (`BibuiPage.Fill` does the same);
    `runtime.DispatchClick` says whether it clicked anything.

- **The app owns its loop (breaking).** A frame is
  `runtime.BeginBuild()`, the app's build, `runtime.EndBuild()`, then the
  app's renderer drawing `runtime.DrawList`, all inside a loop the app writes
  between the window's `BeginFrame` and `EndFrame`. `samples/counter` is the
  whole of it:
  ```csharp
  using var window = SilkWindow.Open(runtime, new("App", 800, 600));
  using var skia = new SkiaGlRenderer(window.Gl);
  while (SilkWindow.BeginFrame(window, runtime))
  {
      runtime.BeginBuild(window.Events); Build(); runtime.EndBuild();
      SilkWindow.BeginDraw(window);
      skia.Draw(runtime.DrawList);
      SilkWindow.EndDraw(window);
      SilkWindow.EndFrame(window, runtime);
  }
  ```
  - `BibuiWindow` (`Create`, `WithSilk`, `WithWayland`, `WithHost`, `Run`),
    `IPlatformHost`, `ISkiaGpuPlatformHost`, `SilkPlatformHost` and
    `WaylandPlatformHost` are gone. A window is `SilkWindow` (Bibui.Platform.Silk)
    or `WaylandWindow` (Bibui.Platform.Wayland): `Open`, then `BeginFrame`
    (waits for a frame to be wanted, with what the keyboard and mouse did in
    `window.Events`) and `EndFrame` (presents). The loop hands the events to
    `runtime.BeginBuild`.
  - Renderers are bound to a window's target when they are made:
    `new SkiaGlRenderer(window.Gl)` draws onto its GL framebuffer, and
    `new SkiaCpuRenderer(window.Cpu)` or `new ManagedRenderer(window.Cpu)` into
    its CPU pixels, which the window presents. `SkiaGlRenderer.ForGl` gives null
    where GL is unusable, so an app can fall back to the CPU target; nothing
    falls back for it. Bibui.Platform.Silk no longer references SkiaSharp.
  - `runtime.Mount`, `MountStateful` and `SetState<T>` are gone. The runtime
    owns the frame's root: what is built with nothing pushed lands under it,
    sized to `runtime.Surface`. State is the app's own fields; a handler
    writes one and the next frame builds from it.
  - `SilkWindow.OnRefresh` is called while a window is being resized (where
    GLFW blocks the loop); set it to draw a frame.
  - `WindowFrameProfile` and `BIBUI_PROFILE_FRAMES` are gone: time the blocks
    of the loop that interest you.
  - `BibuiTestServer` builds no frames of its own: its commands wait for the
    app's loop to run them (`runtime.FrameCount`).
  - `BibuiPage.Launch` takes a build, and `BibuiPage.From(runtime, build)` the
    function that builds a frame's tree; `page.Frame()` runs one.

- **Renderers draw the `DrawList`; the runtime holds no backend (breaking).**
  `runtime.EndBuild()` builds, lays out and records a frame into
  `runtime.DrawList`, and the loop owner draws it with the renderer it chose:
  `SkiaGlRenderer` or `SkiaCpuRenderer` (Bibui.Raster.Skia) on the GPU, onto an `SKSurface` or into CPU memory,
  or `ManagedRenderer` (Bibui.Raster.Managed.Backend) into CPU memory.
  `RasterPixels` describes memory the caller owns, and `DrawToPixels` is a
  renderer's `Draw` handed to code that only has pixels to fill.
  - `IRasterBackend`, `IRasterCanvas`, `IRasterSurface`, `RasterPath`,
    `SkiaRasterBackend` and `ManagedRasterBackend` are gone, and with them
    `WithSkia()`, `WithManagedRasterizer()`, `WithRaster(backend)`,
    `runtime.Raster` and `DrawContext.Backend`. `BibuiRuntime.Create()` is all
    a runtime needs.
  - `runtime.Render()`, `runtime.RenderTo()`, `BibuiRenderer.Render`,
    `BibuiRenderer.RenderTo`, `RenderOutput`, `LastOutput` and `CapturePixels`
    are gone. A PNG is a frame drawn into a CPU buffer and encoded with
    `Bibui.Imaging.Png`: `FramePixels.Png(runtime, draw)` does both, and
    `FramePixels.Bgra` gives the pixels. `BibuiRenderer` is gone: the runtime
    lays out and records its frames itself.
  - The draw layer is `Bibui.Draw` (`DrawList`, `VectorPath`, textures, paint
    values). `Bibui.Renderer` is gone: `AnimKey`, `ResourceCache` and
    `FontRegistry` are in `Bibui`.
  - Textures are made on the renderer: `skia.CreateTexture(...)` or
    `managed.CreateTexture(...)`, as before on the backends.
  - `BibuiTestServer.StartIfEnabled(runtime, port, draw)` draws its screenshots
    and recordings with the function it is given.
  - The managed renderer's damage tracking compares one frame's `DrawList` with
    the last before anything is drawn, and redraws only the changed rects from
    the list, instead of recording canvas calls; pixels are unchanged.
  - `Bibui.Testing`'s `BibuiPage` draws `Screenshot()` with Skia on the CPU, or
    with the function `BibuiPage.From(runtime, draw)` is given. `IBibuiPage`
    is gone.

- **A `Box.Draw` callback is handed its box, and draws in the box's space,
  clipped to it (breaking).** `DrawCallback` is `(Box box, DrawContext ctx)`:
  the box is there for its `HotT`, `ActiveT` and key. (0,0) is the box's
  top-left, `ctx.Rect` and `ctx.Logical` start at (0,0), and nothing a callback
  draws leaves its box. A clip, transform or opacity it leaves pushed is popped
  after it, and a pop past its own does nothing. Code that offset its draws by
  `ctx.Rect`/`ctx.Logical` is unchanged; a callback that drew at surface
  coordinates subtracts the box's position, and one that drew across its own
  edges (a stroke centred on the edge, a ring around an end point) insets.

- **Text is measured and shaped by bibui's own FreeType and HarfBuzz; a raster
  backend only draws glyphs (breaking).** Layout comes out the same whichever
  backend draws, and what is drawn is what was measured.
  - `Bibui.Text.FreeType` is folded into `Bibui`, as the namespace
    `Bibui.FreeType`, and the package is gone. `Bibui` now loads the system
    FreeType and HarfBuzz (`libfreetype6`, `libharfbuzz0b`) in every
    configuration.
  - The canvas's only text call is `DrawGlyphs(face, sizePx, glyphs,
    positions, x, baselineY, color)`. `IRasterCanvas.DrawText` and
    `DrawGlyphRun`, `IRasterBackend.ShapeText`, `RasterFont` and `ShapedRun`
    are gone.
  - `DrawContext.DrawText(text, x, top, font, color)` draws one line of text as
    a `Text` node is drawn: shaped by `Fonts`, with fallback runs, at a
    physical position. Use it rather than drawing glyphs by hand.
  - `FontFace` replaces `RasterTypeface`. `Fonts.Face(family, weight)` and
    `DrawContext.Fonts` give a draw that hands a raster canvas glyphs its face.
  - `IRasterBackend` loses `LoadTypeface`, `MatchFamily`, `MatchCharacter`,
    `DefaultTypeface`, `MeasureText` and `GetMetrics`. A backend makes its own
    face from `FontFace.FontFile` (Skia) or draws from `FontFace.FreeType`'s
    glyph masks (the managed rasterizer, which no longer has an engine of its
    own).
  - Installed fonts are no longer looked up, and there is no fallback per
    character yet. A family registered with `FontRegistry` is used, any other
    gets the built-in Inter, and a character Inter lacks draws as its missing
    glyph. Text no longer depends on the machine.

- **A frame is recorded into a `DrawList`, then drawn (breaking).** After
  layout, the painter and every custom draw record their draws, in tree
  order, into the runtime's `DrawList` (`runtime.DrawList`), and the
  renderer then executes it onto the surface: build → layout → draw ops →
  render. Pixels are unchanged.
  - A `DrawOp` is a fixed-size value with no references; points, glyphs, path
    verbs and gradient colours live in the list's payload, and kept paths and
    faces are named by index. The list is reused from frame to frame.
  - `DrawContext` records instead of drawing, and is primitives only, in
    physical px: `DrawRect(rect, radius)` (a plain rect at radius 0),
    `DrawPath`, `DrawLine`, `DrawPoints`, `DrawTexture` and `DrawText`, each in
    the current paint or a `Paint` of its own; `PushClip`, `PushTransform`
    (translations and scales are transforms; `PushTranslation` and `PushScale`
    are gone), `PushOpacity` and `PushPaint`, each with its pop. What a draw
    becomes in the list is the core's to decide, and a renderer may draw an op
    however it likes.
  - `Paint` is the look of a draw: fill and/or stroke colour, stroke width,
    cap, join, dash, gradient (`RasterShader`), blur (a soft shadow), sampling
    and antialiasing. It replaces `ctx.Paint`, `ctx.Fill`, `ctx.Stroke`,
    `ctx.LinearGradient` and `ctx.RadialGradient` (use `RasterShader.Linear`
    and `RasterShader.Radial`) and `DrawRoundRect`'s fill and stroke paints.
  - Shapes composed of primitives are usage code, in `DrawShapes`:
    `DrawRRect`, `DrawShadow`, `DrawEdge`, `DrawCheckMark`, `DrawArc`,
    `DrawPolyline`, `DrawFilledPath`, `DrawFilledPathGradient` and
    `DrawRRectGradient`, as extension methods written against the public
    `DrawContext` alone, in logical px and `Rgba` (their hex-string overloads
    and `DrawVerticalLine` are gone). `DrawIcon` and `BlurBackdrop` are the
    painter's own.
  - `DrawContext.Canvas` is gone, and with it `SkiaCustomDraw`: a custom draw
    uses `DrawContext`, which every backend can execute.
  - Paths are bibui's own `VectorPath` (verbs and arguments, `AddPath` under a
    transform), not a backend's. `ctx.Path(key, build)` builds and keeps one;
    `ctx.DrawPath` draws it, or copies any other path into the list.
    `IPathBuilder` is gone.

- **The draw layer knows textures, never images (breaking).** An app gets an
  image's pixels into memory its own way (`Bibui.Imaging.Png`, a video
  decoder, its own code) and makes a texture on the renderer it chose.
  - The renderers have `CreateTexture(format, kind,
    width, height, pixels, rowBytes)`, `UpdateTexture` and `ReleaseTexture`,
    which return and take a `TextureHandle`. The app owns the texture. A released handle, or
    `TextureHandle.None`, draws nothing.
  - `DrawContext.DrawTexture` replaces `DrawImage` and `DrawPixels`.
    `RasterImage` and `DrawContext.DrawBitmap` are gone: for a video frame,
    update one texture every frame and draw it.
  - Images are not decoded or encoded by a renderer: frame PNGs are encoded by
    `Bibui.Imaging.Png`, and the managed backend's own PNG codec is gone.
  - The component gallery's Texture page shows both: a texture made once, and
    one whose pixels change every frame.

- **Runtimes are made with `Create`, and the renderer is always chosen
  (breaking).** `BibuiRuntime` has no public constructor: write
  `BibuiRuntime.Create()`.
  - There is no default renderer: `RasterBackends` is gone, and
    Bibui.Raster.Skia no longer registers itself. Nothing is loaded by name, so
    nothing warns under trimming or AOT.
- **Components are called on the runtime (breaking).** Every component named
  by an id is an extension method on `BibuiRuntime`, and its static function
  is gone: `runtime.Button(id, "Save")` for `Button.Build(runtime, id, "Save")`.
  - `runtime.Button`, `Checkbox`, `Switch`, `Toggle`, `Slider`, `RadioGroup`,
    `ToggleGroup`, `Tabs`, `Expander`, `Scrollbar` and `Markdown` replace
    their `Build`; `runtime.MenuItem` replaces `Menu.Item`, `runtime.SelectItem`
    replaces `Select.Item`, and `runtime.TextField`, `MultilineTextField` and
    `EllipsisTextField` replace `TextField.SingleLine`, `Multiline` and
    `Ellipsis`.
  - The types `Button`, `Checkbox`, `Switch`, `Toggle`, `RadioGroup` and
    `ToggleGroup` are gone (the methods live on `ButtonExtensions` and so on).
    `Slider`, `Tabs`, `Expander`, `Scrollbar`, `Markdown`, `Menu`, `Select` and
    `TextField` keep their constants and other members.
  - Like any box the runtime builds, a component is linked under the parent
    in scope, so building it inside `using (runtime.PushParent(column))` puts
    it in the column. Layout (`Arrange`), the keyless leaves and the scopes
    (`Dialog`, `Popover`, `Menu.Begin`, `Select.Begin`) keep their spelling.

- **Text is UTF-16, not UTF-8 (since 0.9.1, breaking).** bibui now holds,
  measures and draws .NET's own chars, so a `string` goes straight in:
  `runtime.Text("Party 🎉")`, `Button.Build(runtime, "b", "Save")`.
  - `runtime.Text`/`TextFromId` and every component's label take a
    `ReadOnlySpan<char>` (was `ReadOnlyMemory<byte>` or `BibuiStr`). The text
    is copied into the node, so it may change as soon as the call returns.
    An optional label is empty, not null, for none.
  - `TabOption`, `ToggleOption` and `RadioOption` take a `string` label;
    `KeyboardShortcut.Build` takes its keys as strings.
  - `BibuiStr` is a `char` buffer for text the user edits (a `TextField`'s
    buffer, a Markdown source). `FromStrLit` takes chars, and `ToString()`
    gives the text.
  - `TextField`'s placeholder is a `ReadOnlySpan<char>`. Caret, selection and
    wrapped-line offsets count chars, and the caret, Backspace and a click
    move by text element, so 👍🏽 or 👨‍👩‍👧 is never split
    (`TextOp.ScanTextElement`, was `ScanCodePoint`).
  - `Fonts.Measure`/`OffsetAt`/`Wrap`, `MarkdownDocument` (`Parse`, `Text`,
    `TextOf`, `CodeOf`), `BibUiEvent.PasteData` (a `string`) and the raster
    backends' `MeasureText`/`ShapeText`/`DrawText` take or give chars. The
    backends' `string` and UTF-8 overloads are one `ReadOnlySpan<char>`
    overload each.
  - To migrate: drop `"…"u8`, `Encoding.UTF8.GetBytes(…)` and
    `BibuiStr.FromStrLit(…)` around labels and texts, and pass the string.
- **A frame is build, layout, paint.** Application code describes the frame
  through the runtime (`runtime.Box()`, `runtime.BoxFromId(id, claims)`,
  `runtime.Text`, `runtime.Icon`, `runtime.PushParent`); the layout solver
  sizes and places the whole tree; the painter draws it. Every node states
  its size on each axis (pixels, text, a share of the parent, or the sum of
  its children); there are no implicit sizes, margins or alignment. Spacing
  is a gap node and centring is springs.
- **Interactions are asked for, not called back.** A box claims what it takes
  (`InteractionKinds`) and the holder asks with
  `runtime.InteractionsFor(box)`. Asking consumes, so the order boxes ask in
  is their z-order. Overlays (menu, popover, select, dialog) open with
  `runtime.OpenDropDown(id, anchor)`, are built with
  `runtime.BeginDropDown(id)` and drawn last, and close on a press that a box
  outside them and their anchor takes, or that nothing takes. A dropdown's
  rect is its own over whatever is drawn under it, so a select in a dialog
  picks its rows.
- **Components are functions.** `Button.Build`, `Switch.Build`,
  `Slider.Build`, `Dialog.Begin` and the rest compose boxes and return what
  happened (`BibuiWidget`). They own no state, and colours are theme tokens
  resolved at paint time.
- **Text is UTF-8 throughout** (`BibuiStr`, `ReadOnlySpan<byte>`), from
  measuring and wrapping to drawing.
- **Font size and face come from a stack.** `runtime.PushFontSize(size)` and
  `runtime.PushFontFace(family, weight)` scope a size, family and weight for
  the texts built inside them. `runtime.Text` takes an optional size and
  family. `Text.Bold` is replaced by `Text.Weight` (`FontWeight`).
- **Clipping is a claim.** A box clips its content only when it claims
  `InteractionKinds.Clip` (`Arrange.Clipped()`), which also cuts presses to
  the box.
- **The package split.** bibui core references no SkiaSharp: drawing goes
  through `IRasterBackend`. The packages are `Bibui` (nodes, layout, painter,
  text, theming, and the runtime: the frame, interactions and components), `Bibui.Testing`, `Bibui.Raster.Skia`,
  `Bibui.Platform.Silk`, `Bibui.Platform.Wayland`, `Bibui.Raster.Managed`,
  `Bibui.Raster.Managed.Backend` and `Bibui.Text.FreeType`. Referencing `Bibui.Raster.Skia` makes Skia the default backend for
  `new BibuiRenderer()` and `new BibuiRuntime()`.
- **The window is a platform package's.** GLFW + OpenGL is
  `Bibui.Platform.Silk`, Wayland `Bibui.Platform.Wayland`. `Bibui.Key` and
  `Bibui.MouseButton` are bibui's own types.
- **Skia-specific API moved to the Skia packages.**
  `renderer.RenderTo(SKSurface, ...)` and `SkiaSurface` live in
  `Bibui.Raster.Skia`.
- **Drawing types are named for what they are:** `SkiaContext` is
  `DrawContext`, `SkiaDraw` is `DrawCallback`, `SkiaKey` is `CacheKey`.
- **The runtime is part of `Bibui`.** `BibuiRuntime`, the
  window input and the components moved from the `Bibui.Runtime`
  package into `Bibui`; reference `Bibui` alone.
- **Theme types are `Bibui.Theming`**, merged into `Bibui` from the former
  `Bibui.Contracts` package.
- **Theme colours come in two layers:** `ColorRamp`s (eleven steps, 50–950)
  and roles that name either a colour or a ramp step (`"accent-600"`).
  `ColorTokens` and `ThemeTokens` are records. A bare `new ColorTokens()`
  now gives the default ramps' roles instead of placeholder greys.
- **Managed raster modes are named in bibui's terms:**
  `Antialiasing.SkiaCompatible` is `Antialiasing.BaselineCompatible`,
  `ShapeOptions.SkiaCompatibleFeatures` is `BaselineCompatibleFeatures`, and
  `ShapeOptions.HarfBuzzSkiaCompatible` is `HarfBuzzBaselineCompatible`.
  `SkiaMaskBlur` is `GaussianMaskBlur`.
- `Switch.Build` takes its height in pixels (24 by default) and scales every
  part from it; its size constants are gone.
- `TextField.SingleLine`, `Ellipsis`, `Multiline` and `Rows` take the font
  size and face in scope instead of a `fontSize` argument.
- The unknown-theme error lists the themes under "Registered:".
- Every library is checked for Native AOT by the compiler; the test server
  serializes JSON through source-generated contexts.
- `BibuiTestServer` is opt-in: `BibuiTestServer.StartIfEnabled` starts one
  only with `BIBUI_TEST_SERVER=1`, and the samples start it no other way.
  Every request carries a per-server token, which the server writes to a
  user-only file (`TokenFilePath`) and `WorkflowClient` reads; `screenshot`
  and `start_recording` take paths relative to the server's output directory
  (`BIBUI_TEST_SERVER_OUT`). The constructor takes that directory.

### Added

- **`Ease`: the runtime's animation steps as pure functions.** A value and its
  target in, the value `dt` seconds on out, with nothing kept by the runtime:
  `Ease.Rate(speed, dt)`, the share of the gap closed in a frame, computed
  once for every value eased at that speed; `Ease.Exponential(current, target,
  rate)` and `Ease.Exponential(current, target, speed, dt)` (for `float`,
  `Vector2` and `Vector4`); and `Ease.Spring(ref current, ref velocity, target,
  spring, dt)`.
  `Animate`, hover and press, view scrolling, transitions and
  `DrawContext.Animate` all step through them, so a value eased by hand moves
  exactly as one the runtime eases (tested, frame by frame). Snapping to the
  target, reduced motion and `MotionEasing.None` stay with the caller.

- **Groups: blend modes and colour matrices.** `ctx.PushBlendMode(mode)`
  draws until `PopBlendMode` into a layer and composites it onto what is
  under it with the mode: `SrcOver`, `Multiply`, `Plus`, `Screen`, `Overlay`
  or `Src`, Skia's formulas on premultiplied colour. `ctx.PushColorMatrix(m)`
  composites its layer through a 4×5 matrix, row by row, on straight colour
  in 0..1 with the fifth column a translation in 0..1. `PushOpacity` is
  shorthand for a matrix that only scales alpha. A renderer makes no layer
  for `SrcOver` or the identity, and draws an alpha-only matrix as the
  opacity layer it drew before, so existing pixels do not move.
  - On the list, `DrawOpKind.PushOpacity` and `DrawOp.Alpha` are gone (breaking
    for a renderer of your own): the ops are `PushBlendMode` (`DrawOp.Blend`)
    and `PushColorMatrix` (20 floats in the payload). `ColorMatrices.IsIdentity`
    and `IsOpacity` say when a matrix needs no layer, or only an alpha.
  - With damage tracking, a `Src` layer, or a matrix that adds alpha, writes
    all of its clip: a partial repaint holds it to the repainted rect.
- **A texture can be drawn through a lookup table.**
  `ctx.DrawTexture(texture, dest, paint, new TextureLookup(table, TextureChannel.R))`:
  each pixel's raw channel (as stored: R8's R, A8's A, RG8's R and G,
  RGBA8's straight channels) picks the table's entry, a 1-D colormap from one
  channel or a 2-D table from two. A channel the format lacks reads 0. The
  source is filtered by the paint; the table is read exactly. A lookup drawn
  again allocates nothing, on every renderer.

- `runtime.Column()` and `runtime.Row()`: a pooled box along Y or X, linked
  under the parent in scope; push it to build into it:
  `var column = runtime.ColumnFit(); using (runtime.PushParent(column)) { ... }`.
  The bare forms are sized like `Box()`, by the sizes pushed; the sized forms
  set the box's own: `ColumnFit`/`RowFit` (children), `ColumnFillX`/`RowFillX`,
  `ColumnFillY`/`RowFillY`, `ColumnFill`/`RowFill` (parent), `ColumnWidth(px)`,
  `RowHeight(px)`, `RowFillX(height)` and `ColumnPx`/`RowPx`; an axis a form
  does not name fits its children. They return the box, so `Padded`, `Styled`
  and the other helpers chain on it.

- `runtime.Spring()`, and the `runtime.Centred()` scope: a spring before what
  is built inside and one after it, along the parent in scope, from the
  runtime's pool. Centred across a row:
  `using (runtime.PushParent(runtime.RowFillX())) using (runtime.Centred()) { ... }`.
- `Arrange.Column`, `Arrange.Row`, `Arrange.CentredX`, `Arrange.CentredY` and
  `Arrange.Centred` are obsolete: they allocate a box a frame and move nodes
  the runtime has already linked. Push a `runtime` row or column instead, and
  centre with `runtime.Centred()`. They still work; the repository's own call sites build with
  CS0618 warnings until they are moved over. `Arrange`'s size helpers
  (`FillX`, `Px`, `Padded`, ...) and `Space`/`Spring`/`Gap` are unchanged.

- `Icons.Register(name, svgPathData)`: an app adds icons of its own at
  start-up, drawn by `runtime.Icon` and known to `Icon.IsKnown` like the
  built-in Lucide set (24×24 viewBox, stroke 2, round caps and joins). The
  path is checked when it is registered; a built-in name, or the same name
  with other path data, throws. `Icons.Names` lists every icon, built-in and
  registered.
- Custom drawing on any box: `Box.Draw` is settable, so a retained box
  (`BoxFromId`) can draw as well as be asked about, and it is cleared with
  the rest of the build state when the box is acquired again.
  `DrawContext.Theme` and `DrawContext.Resolve(UiColor)` resolve theme tokens
  at paint, so a callback follows the theme the frame is painted in.
- Motion: `runtime.Animate` with a `Transition` — timed along an `Easing`
  (cubic-bezier, CSS and Material presets) or on a `Spring` that keeps its
  velocity across targets, with a delay — for floats and `Vector2`;
  `runtime.AnimateColor`, crossfading in Oklab (`ColorMix`); `Presence` and
  `PresenceList` for things that stay drawn while they leave, with
  `PresenceStyle` and `CollapseBox`; paint-time transforms on `Style`
  (`Translate`, `Scale`, `Rotate`, `TransformOrigin`) that the pointer test
  follows; `SetTheme(theme, transition)` crossfades; and
  `runtime.ReducedMotion` (`BIBUI_REDUCED_MOTION=1`). Hover and press now
  blend in Oklab.
- `Bibui.Platform.Wayland`: a window host on raw Wayland (xdg-shell,
  wl_shm buffers, frame-callback pacing, fractional scale, cursor shapes,
  clipboard, key repeat), with no GLFW or OpenGL.
- `Bibui.Raster.Managed` and `Bibui.Raster.Managed.Backend`: a deterministic
  managed CPU rasterizer behind `IRasterBackend`, with
  `Bibui.Text.FreeType` for text on the system FreeType and HarfBuzz. With
  the Wayland host, an app publishes with Native AOT as one file with no
  SkiaSharp.
- A managed PNG encoder and decoder.
- Damage tracking in the managed rasterizer
  (`ManagedRasterBackend.DamageTracking`), which rasterises only what changed
  and leaves every layer above it drawing whole frames.
- `ThemeRegistry`: one table of theme names, open to themes registered from
  outside bibui.
- Themes as JSON files with `extends` (`ThemeFile`,
  `ThemeRegistry.RegisterFile`). The built-in themes (`bibui-dark`,
  `bibui-light`, `neutral-dark`, `default-dark`, `default-light`) are files
  read through the same loader.
- bibui's own look: `bibui-dark` (the default) and `bibui-light`, warm
  graphite neutrals with a petrol-teal accent and status colours that meet
  WCAG AA in both modes.
- `Theme.FromSeed(accent, neutral, radius, density, mode)`: a whole theme
  from a few seeds, with ramps drawn in OKLCH and text roles checked for
  contrast (`Theme.Contrast`).
- Theme tokens for elevation, borders, the focus ring, density and motion.
- `TextField.Multiline(..., TextWrap wrap)`: wrap by words or by characters.
- A Markdown viewer component.

### Removed

- `SceneNode`, the hit tester, `Row`/`Column`/`Stack`/`Grid`, the `Child`
  slot, box callbacks, `Bibui.Button` and the per-component renderers.
- `Style.FontSize`, `Style.ClipContent`, `Style.Scale` and letter spacing.
- `GetDrawOps`, the YAML specs, the render manifest, and the browser parity
  pipeline.
- The `Bibui.Contracts` and `Bibui.Runtime` packages (merged into `Bibui`).
- `runtime.OnUnhandledKey`. It fired as a key arrived, before any field
  could take it, so it also reported text typed into a field. Read
  `runtime.UnconsumedEvents` while building instead: what a component took is
  marked eaten.

### Fixed

- A typed character above U+FFFF (an emoji, from Compose or an xkb layout)
  reaches a text field whole. On the Wayland window it was sent as two halves
  and the first one threw; on the Silk window it was cut to 16 bits and typed
  the wrong character. `WindowEvent.Text` takes a `Rune`, and a text field
  ignores a code point that is no character instead of throwing.

[Unreleased]: https://github.com/Navisurg/bibui/commits/main
