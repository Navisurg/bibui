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
  outside them and their anchor takes, or that nothing takes.
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

[Unreleased]: https://github.com/Navisurg/bibui/commits/main
