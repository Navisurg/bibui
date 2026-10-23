# Changelog

All notable changes to bibui are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and bibui follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **RowScrollList** (`runtime.BeginRowScrollList`, `EndRowScrollList`): the
  list of rows of one pitch, building only those in sight, by rows or by
  pixels (`ScrollBy`). It eases.
- **ScrollList** (`runtime.BeginScrollList(id, ref scroll, count)`, a loop of
  `ScrollRows.Next`, `EndScrollList`): rows of their own heights, by pixels.
  It holds no heights; the loop hands each in as the list reaches the row, so
  the wheel moves exactly through rows of any height. It eases.
- `runtime.Scrollbar(id, ref scroll, count, first, last)`, a bar for either.

### Changed

- The gallery's ScrollList page shows both lists; the showcase's target queue
  is a RowScrollList.
- Text finds its face by the theme's slot and its size by the face's id, so
  measuring and drawing a line no longer hash a family's name: a frame of 256
  lines records about a third faster.
- **Breaking:** `Font` holds the `Face` it is set in instead of `Family` and
  `Weight`, and has no public constructor. Get one from
  `runtime.FontInScope()`: a font is always one the theme provides.
- **Breaking:** `DrawContext.FontMetrics` takes a `Font` instead of a size,
  family and weight.

### Removed

- **Breaking:** the pitch overloads `BeginScrollList(id, ref scroll, pitch,
  count)`, `EndScrollList(ref scroll)` and `Scrollbar(id, scroll, count,
  viewRows)`, `ScrollPt.First`, `Last`, `ViewRows` and `Target(idx, px,
  pitch)`. Use RowScrollList, the same list. `ScrollPt.Off` is now pixels.
  Removed without a deprecated release first, so the next release is a major
  one.
- **Breaking:** `Fonts.Face(family, weight)`, and `DrawContext.LineBox` and
  `BlockHeight`, which took a family's name. Use `Fonts.LineHeight(font)` with
  a font from `runtime.FontInScope()`.

## [1.0.0] - 2026-10-21

The first stable release, and the start of the full semantic-versioning
promise (docs/versioning.md). The core package is `Bibui.Core`; `Bibui` is
all of bibui in one reference; layout is said with a few fast paths over the
primitives, with a Begin and End for every scope; and bibui runs in the
browser.

### Added

- **`Bibui`: all of bibui in one reference.** The package `Bibui` is now a
  package of dependencies only: `Bibui.App.SilkSkiaGl` (so `BibuiApp` opens a
  Silk window drawn by Skia on the GPU), `Bibui.Core`, both windows
  (`Bibui.Platform.Silk`, `Bibui.Platform.Wayland`), the browser host, both
  renderers (`Bibui.Raster.Skia`, `Bibui.Raster.Managed.Backend`), `Bibui.Gl`
  and `Bibui.Testing`, each at exactly its own version. An app that wants the
  fewest dependencies, or another window or renderer, references the one
  `Bibui.App` package it runs on instead; an app that referenced `Bibui` for the
  core alone references `Bibui.Core`.
- **bibui in the browser: `Bibui.Platform.Browser`.** `BrowserWindow` is a
  window on a `<canvas>` in a .NET WebAssembly app
  (`Microsoft.NET.Sdk.WebAssembly`, no Blazor), with `window.Events` and a
  `Cpu` target that `ManagedRenderer` (or `SkiaCpuRenderer`) draws into.
  `BrowserWindow.OpenAsync(runtime, "canvas-id")` opens one, by element id or
  on a canvas element; a page may not block, so `BrowserWindow.Run(window,
  runtime, frame)` hands the frame's body to the browser, which calls it on
  `requestAnimationFrame` when the window has input, owes frames or was
  resized, and its canvas is in view. Several windows share one loop. Frames
  are drawn at the canvas's size in device pixels, with devicePixelRatio as
  the scale. Pointer, wheel, keys (by physical key, with the typed
  character), copy and paste, and the cursor. The package carries FreeType
  and HarfBuzz built for the browser and links them into the app; it needs
  the `wasm-tools` workload. Dead keys, IME and on-screen keyboards do not
  type yet. See [bibui in the browser](docs/browser.md) and
  `samples/counter-browser`.
- **Key repeat for every key.** The Wayland, Silk and browser windows report a held
  key's down again with `BibUiEvent.Repeat` set (`WindowEvent.KeyRepeat` for
  hosts and tests), not only its text, so arrows, Backspace and Page Down
  repeat in an app's own handling too.

### Changed

- **Layout is said with a few fast paths over the primitives (breaking).**
  `Arrange` and the sized rows and columns (`RowFit`, `RowFillX`, `ColumnFill`,
  `ColumnPx`, …), `Spring` and `Centred` are gone. In their place:
  - `Row()` and `Column()` are as large as their children on any axis no size
    is pushed for, and `.Sized(x, y)` sets the axes it is given:
    `Row().Sized(Size.Fill, runtime.Em(2.5f))` is a toolbar.
  - `Size.Fit`, `Size.Fill`, `Size.Pct`, `Size.Px` and `Size.Text` name the
    sizes, and `runtime.Em(n)` is `n` times the font size in scope.
  - `Gap(px)` and `Spacer(size)` space along the parent's axis;
    `Spacer(Size.Fill)` is a spring.
  - `BeginCenter` centres along the parent's axis, `BeginCenterAcross` across
    it (handing back the cell it makes), `BeginCenterX`/`BeginCenterY` choose
    between them by the parent's axis, and `BeginCenterXY` centres both ways.
  - Every scope has a `Begin`/`End` pair (`BeginParent(box)`/`EndParent(box)`,
    `BeginAxis`, `BeginSizeX`, `BeginFontSize`, `BeginFontFace`, the centres),
    and the scope form, named without `Begin` (`Parent(box)`, `Center()`,
    `FontFace(...)`, …), is the `Begin` handing back a scope whose `Dispose`
    is the `End`. `PushParent`, `PushFontSize` and the other `Push…` names
    are gone.
  - `.At(x, y)` floats a node; `Styled`, `Padded` and `Clipped` are the
    node's fields, set as such.
  - `Text` and `TextFromId` take a `weight`, as they take a `family`: one bold
    text is `Text(title, weight: FontWeight.Bold)`, with no face begun.
- **`DrawFlags.Unclipped`.** A box's `Draw` callback is clipped to the box;
  with this flag on its style, its ink may reach past it (a glow, a highlight
  around a word), cut only by a box that claims `Clip`. Layout and
  hit-testing still take the box's rect.
- **Only the frame's ground is in the theme's background.** A tooltip's text
  and the active tab are drawn in `Surface`, not `Background`, so a theme
  whose background is transparent (`#00000000`) gives a transparent frame for
  a host to compose over its own content, and both still show. With bibui's
  own themes, where the two are the same colour, nothing changes.
- **The core package is `Bibui.Core` (breaking).** The package `Bibui` is
  now `Bibui.Core`, built from `bibui-core/` into the assembly
  `Bibui.Core`, and its namespaces moved under it: `Bibui` is
  `Bibui.Core`, and `Bibui.Runtime`, `Bibui.Draw`, `Bibui.Theming`,
  `Bibui.FreeType`, `Bibui.Imaging` and `Bibui.TextEdit` are
  `Bibui.Core.Runtime`, `Bibui.Core.Draw` and so on. Replace a
  `PackageReference` to `Bibui` with `Bibui.Core`, and `using Bibui…;`
  with `using Bibui.Core…;`. The embedded fonts and themes are named
  `Bibui.Core.Assets.*`. The other packages keep their names.

### Fixed

- **FreeType on 32-bit processes.** The FreeType struct offsets bibui reads
  were laid out for 64-bit (LP64) only, so on a 32-bit process, wasm32 among
  them, glyph metrics and the face's glyph count were read from the wrong
  fields. They are now chosen by pointer size. Nothing changes on 64-bit.

## [0.10.1] - 2026-10-19

Frame pacing: windows draw at the display's refresh rate, and animation
keeps its length in real time.

### Changed

- **Windows set `TargetHz` to the display's refresh rate.** The Wayland
  window takes it from the current mode of the outputs its surface is on;
  the Silk window from the monitor GLFW reports, when it opens and when it
  moves, and paces its loop to it. It was always 60.
- **The window measures `Dt` (breaking only if you relied on
  `Dt == 1 / TargetHz`).** `Dt` is now a property the window sets in its
  `BeginFrame`, before the build: the time since the last frame started. A
  frame that follows a wait for input is one frame at `TargetHz`, so a
  window woken after a long idle does not jump. Without a window (headless,
  tests) it stays 1/60 unless set, so runs are deterministic. Setting
  `TargetHz` no longer changes the animation step; set `Dt` instead.

### Fixed

- **A hidden Silk window no longer hangs.** `VSync = false` never reached
  the GL context, so on Wayland `SwapBuffers` waited for frame callbacks a
  hidden window never gets, and Hyprland showed its not-responding dialog.
  The window now sets the swap interval to 0.
- **Animations in the Wayland packages ran twice as fast on a 120 Hz
  display**, because the window drew 120 frames a second while `Dt` was
  1/60.

## [0.10.0] - 2026-10-19

bibui 0.10: the app owns its loop and its renderer. A frame is built, laid
out and recorded into a `DrawList`, and the renderer the app chose draws it;
the `Bibui.App` packages put a window and a renderer behind two calls. Most
entries below break code written for 0.9: each says what to use instead,
and `samples/counter` is the whole of a loop.

### Changed

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

- **A `Box.Draw` callback is handed its box, and draws in the box's space,
  clipped to it (breaking).** `DrawCallback` is `(Box box, DrawContext ctx)`:
  the box is there for its `HotT`, `ActiveT` and key. (0,0) is the box's
  top-left, `ctx.Rect` and `ctx.Logical` start at (0,0), and nothing a callback
  draws leaves its box. A clip, transform or opacity it leaves pushed is popped
  after it, and a pop past its own does nothing. Code that offset its draws by
  `ctx.Rect`/`ctx.Logical` is unchanged; a callback that drew at surface
  coordinates subtracts the box's position, and one that drew across its own
  edges (a stroke centred on the edge, a ring around an end point) insets.

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

### Added

- **`Bibui.App` packages: a bibui app in one package.** Five packages, one per
  window and renderer: `Bibui.App.SilkSkiaGl` (Silk, Skia on the GPU),
  `Bibui.App.SilkSkiaCpu`, `Bibui.App.SilkManaged` (Silk, drawn on the CPU and
  shown through the GL framebuffer), `Bibui.App.WaylandManaged` and
  `Bibui.App.WaylandSkiaCpu`. Each brings bibui, its window and its renderer,
  and has the same `BibuiApp`, so changing the package changes how an app is
  drawn and nothing in its code:
  ```csharp
  using var app = BibuiApp.Open(runtime, new("App", 800, 600));
  while (app.BeginFrame())
  {
      runtime.BeginBuild(app.Events);
      Build();
      runtime.EndBuild();
      app.EndFrame();      // draw, present, pace
  }
  ```
  - `app.Renderer` is the renderer, for textures; `app.Window` the window.
  - The test server starts with `new BibuiAppOptions { TestServer = true }` or
    `BIBUI_TEST_SERVER=1`; otherwise it is off. On the CPU packages its
    screenshots are drawn by the app's own renderer, textures included.
  - The window and renderer packages stay usable on their own, for a loop an
    app writes itself.
  - Each depends on exactly the `Bibui`, window and renderer versions it was
    built with.

- **ScrollList: rows of one height, of which only those in sight are built.**
  `runtime.BeginScrollList(id, ref scroll, pitch, count)` fills the parent in
  scope with a view and a bar and pushes the view; build rows `scroll.First` up
  to `scroll.Last`, then `runtime.EndScrollList(ref scroll)` pops it. The list
  scrolls by rows: the `ScrollPt` the caller keeps is all it needs between
  frames, the target row and the pixels into it, how far the list is still
  shown from there (eased to 0 at `Rates.Follow`), and the rows in sight. The
  wheel moves it by pixels, the thumb drags by rows from where it was pressed,
  and the track pages by the rows in view. `scroll.Target(idx, px, pitch)`
  moves it from the caller's side, keeping what is shown. The bar is also
  `runtime.Scrollbar(id, scroll, count, viewRows)`, for a list built the same
  way. A gallery page shows 100,000 rows.

- **One rate per kind of motion, and everything eases by it (breaking).**
  `runtime.Rates` holds this frame's rates for exponential approach, computed
  once as the build begins from the theme's motion speeds: `Hover` (hot and
  pressed boxes), `Follow` (a view's scroll offset, the caret), `Toggle` (a
  switch's knob, a panel opening) and `Default` (anything else). Every rate is
  1 under reduced motion or `MotionEasing.None`, so no caller checks for it.
  `runtime.Rate(speed)` gives one for a speed of your own, the same way.
  - `Ease.Exponential(current, target, rate)` (`float`, `Vector2`, `Vector4`),
    `Ease.Rate(speed, dt)` and `Ease.Spring(ref current, ref velocity, target,
    spring, dt)` are the steps as pure functions; the runtime's hover, press,
    scrolling, `Animate`, transitions and `DrawContext.Animate` all step
    through them.
  - `runtime.Animate(id, property, target, rate)` and
    `DrawContext.Animate(key, target, rate)` take a rate where they took a
    speed: `runtime.Rates.Toggle`, `runtime.Rate(18f)`, or 1 for a value driven
    directly (it was `float.PositiveInfinity`). With none, `Rates.Default`;
    `DrawContext.Animate`'s default was a speed of 12, and is now the theme's
    default, 20.
  - `MotionTokens.Caret` is `MotionTokens.Follow`, and view scrolling eases at
    it rather than at `Hover`. A theme file's `"caret"` is still read as
    `"follow"`.

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

### Removed

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

## [0.9.3] - 2026-10-15

bibui 0.9, the first public release: a frame model of build, layout and
paint, the library split into packages by what they depend on, and a second
route to the screen with no native drawing dependency. The entries below are
the changes since the private, callback-based versions bibui was used as
before it was published.

### Changed

- **Runtimes and windows are made with `Create`, and the backend and host are
  always chosen (breaking).**
  `BibuiWindow.Create(BibuiRuntime.Create().WithSkia()).WithSilk().Run(title: "App")`.
  - `BibuiRuntime` and `BibuiWindow` have no public constructors. Instead of
    `new BibuiRuntime(backend)`, write `BibuiRuntime.Create()` followed by
    `.WithSkia()` (Bibui.Raster.Skia), `.WithManagedRasterizer()`
    (Bibui.Raster.Managed.Backend) or `.WithRaster(backend)`, before the
    runtime first draws or measures. Instead of `new BibuiWindow(runtime, host)`,
    write `BibuiWindow.Create(runtime)` followed by `.WithSilk()`
    (Bibui.Platform.Silk), `.WithWayland(options)` (Bibui.Platform.Wayland) or
    `.WithHost(host)`.
  - There is no default backend: `new BibuiRuntime()`, `new BibuiRenderer()`
    and `RasterBackends` are gone, and Bibui.Raster.Skia no longer registers
    itself. Nothing is loaded by name, so nothing warns under trimming or AOT.
    A runtime with no backend throws when it first draws, and a window with no
    host throws when it runs, each saying what to call.
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
  text, theming, and the runtime: the frame loop, interactions, components
  and `BibuiWindow`), `Bibui.Testing`, `Bibui.Raster.Skia`,
  `Bibui.Platform.Silk`, `Bibui.Platform.Wayland`, `Bibui.Raster.Managed`,
  `Bibui.Raster.Managed.Backend` and `Bibui.Text.FreeType`. Referencing `Bibui.Raster.Skia` makes Skia the default backend for
  `new BibuiRenderer()` and `new BibuiRuntime()`.
- **`BibuiWindow` takes its platform host.** `new BibuiWindow(runtime)` is
  now `new BibuiWindow(runtime, new SilkPlatformHost())` (GLFW + OpenGL, in
  `Bibui.Platform.Silk`) or `new WaylandPlatformHost()`. `Bibui.Key` and
  `Bibui.MouseButton` are bibui's own types.
- **Skia-specific API moved to the Skia packages.** `Box.CustomDraw` is now
  `Draw = SkiaCustomDraw.Of(...)` in `Bibui.Raster.Skia`;
  `renderer.RenderTo(SKSurface, ...)` and `SkiaSurface` live there too.
- **Drawing types are named for what they are:** `SkiaContext` is
  `DrawContext`, `SkiaDraw` is `DrawCallback`, `SkiaKey` is `CacheKey`.
- **The runtime is part of `Bibui`.** `BibuiRuntime`, `BibuiWindow`, the
  platform-host seam and the components moved from the `Bibui.Runtime`
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

[Unreleased]: https://github.com/Navisurg/bibui/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/Navisurg/bibui/compare/v0.10.1...v1.0.0
[0.10.1]: https://github.com/Navisurg/bibui/compare/v0.10.0...v0.10.1
[0.10.0]: https://github.com/Navisurg/bibui/compare/v0.9.3...v0.10.0
[0.9.3]: https://github.com/Navisurg/bibui/releases/tag/v0.9.3
