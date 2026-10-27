# Contributing to bibui

Thank you for wanting to help. bibui lives at
<https://github.com/Navisurg/bibui>; issues and pull requests go there. This
guide takes you from a clone to a pull request: how to build, how changes
are tested, the few rules the code keeps, and what we look for in review.

bibui is licensed under the Apache License 2.0 (see `LICENSE`). By sending a
contribution you agree that it is licensed under the same terms, as section 5
of the licence describes. Third-party material bibui includes is listed in
`NOTICE`; if your change adds any (an icon, a font, a palette, code ported
from another project), add it there in the same pull request.

Everyone taking part is expected to follow the code of conduct in
`CODE_OF_CONDUCT.md`. If you have found a security vulnerability, do not
open an issue or pull request for it; report it privately as `SECURITY.md`
describes.

## Building

You need the .NET 10 SDK. On Linux, bibui's text and the Wayland host load
system libraries at run time:

- FreeType and HarfBuzz (`libfreetype6`, `libharfbuzz0b` on Debian/Ubuntu)
  for `Bibui.Core`, which measures and shapes all text with them;
- `libwayland-client`, `libwayland-cursor` and `libxkbcommon` for `Bibui.Platform.Wayland`.

Each library is its own project: `bibui-core` holds the core, the runtime
(`BibuiRuntime`) and the components, and the platform windows,
renderers and text engine are projects beside it. Build the ones you
touch, in Release, as CI does:

```bash
dotnet build bibui-core/bibui-core.csproj -c Release
dotnet build samples/component-gallery/component-gallery.csproj -c Release
```

Every project sets `TreatWarningsAsErrors`, so a build with a warning is a
failed build. CI runs on GitHub Actions, and the projects it builds are
listed in `.github/workflows/`; a pull request should build all of them with
**0 warnings**.

To see components running, start the gallery, a page per component:

```bash
dotnet run --project samples/component-gallery                       # GLFW + OpenGL window, Skia
dotnet run --project samples/component-gallery -p:App=WaylandManaged  # raw Wayland, managed raster
```

## Tests

Behaviour and layout are tested in ordinary C# test programs. Each is a
console app; it prints what failed and exits non-zero:

```bash
dotnet run --project tests/bibui-runtime-tests -c Release       # runtime, components, layout, themes
dotnet run --project tests/text-tests -c Release
dotnet run --project tests/png-tests -c Release
dotnet run --project tests/wayland-tests -c Release             # headless; skips what needs a compositor
dotnet run --project tests/browser-tests -c Release             # the browser host's logic, no browser
dotnet run --project bibui-raster-managed/tests -c Release
dotnet run --project bibui-raster-managed/damage-tests -c Release  # damage tracking, byte for byte
```

The browser route (`bibui-platform-browser`, `samples/counter-browser`)
needs the `wasm-tools` workload and FreeType and HarfBuzz built for the
browser, once; the smoke test drives the sample in headless Chromium:

```bash
dotnet workload install wasm-tools
bibui-platform-browser/native/build.sh
dotnet publish samples/counter-browser -c Release -o out/counter-browser
node tests/browser-smoke/smoke.mjs out/counter-browser/wwwroot
```

Layout is asserted over the laid-out nodes, not over pixels: build the tree,
then check bounds with `runtime.FindById(...)` or `BibuiPage.GetBounds(...)`
(`Bibui.Testing`). Add a test beside the change it covers.

### Pixel verification

bibui's rendering is also checked as pictures: every component state is
rendered on the CPU, deterministically, and compared with a picture a
maintainer has looked at and accepted. That suite and its reference pictures
are kept by the maintainers, outside this repository.

You do not need to run it. When your pull request changes what something
looks like, or could (a component, the painter, text, a theme, a raster
backend), a maintainer runs the pixel check on it and posts what it found:

- if your change was meant to leave pixels alone, a difference is a
  regression, and you will get the renders and diffs to fix it from;
- if it was meant to change pixels, say so in the pull request, with a
  screenshot from the gallery (`samples/component-gallery`); the maintainer
  reviews the new renders and updates the references.

Tests in this repository are where you show that a change works; the pixel
check is how the maintainers make sure nothing else moved.

## How bibui code is written

A frame is three passes that never mix: your code **builds** a tree of boxes,
the layout solver **lays it out**, and the painter **paints** it. The painter
lays nothing out, and the solver draws nothing. [Concepts](docs/concepts.md) explains each
part; a change keeps to these rules.

### Components

A component is a plain static extension method on the runtime
(`runtime.Switch(...)`; `bibui-core/src/Runtime/Switch.cs` is the pattern), not
a class with state ([Components are functions](docs/concepts.md#components-are-functions)):

- It composes `runtime.Box()`, `runtime.BoxFromId(id, claims)` (for anything it
  asks about), `runtime.Text`, `runtime.Icon`, `runtime.Gap` and
  `runtime.BeginParent`, building each node where it goes in the tree.
- Every node states its size on both axes ([Boxes and sizes](docs/concepts.md#boxes-and-sizes)).
- It owns no state and writes none of the caller's: it takes the value to draw
  and returns a `BibuiWidget`, and the caller updates its own state. What must
  survive a frame lives on a retained box or in `runtime.Animate`.
- Colours are theme tokens, and line widths, focus ring, shadows, density and
  motion are read from `runtime.Theme`: no literals, no theme parameter.
- Text is UTF-16: a label is a `ReadOnlySpan<char>`; `BibuiStr` only for text
  the user edits ([Text is UTF-16](docs/concepts.md#text-is-utf-16)).

### Interaction

There are no callbacks. A box says what it takes when it is acquired, and the
code that holds it asks what happened (`runtime.InteractionsFor(box)`); a
disabled control claims `InteractionKinds.Disabled`
([Claims and asks](docs/concepts.md#claims-and-asks)). Asking consumes, so ask order is
z-order: a container asks after the controls inside it, an overlay before what
is beneath it ([Ask order is z-order](docs/concepts.md#ask-order-is-z-order); dropdowns are
the [exception](docs/concepts.md#dropdowns-are-the-exception)).

### When a layout is wrong, fix the layout code

The solver is small and strict: a node without a size throws, and so does a
cycle such as a fill inside a parent sized by its children. Fix the code that
built the layout ([The usual mistakes](docs/concepts.md#the-usual-mistakes)). Changes to the
solver or the painter need a failing test that shows it mishandling correct
layout code, and an issue to discuss them before the pull request.

### Native AOT, always

bibui's own code must always be Native AOT compatible. Every library sets
`<IsAotCompatible>true</IsAotCompatible>` and warnings are errors, so the
compiler refuses what AOT cannot do. Do not add, or depend on:

- reflection-based serialization: `JsonSerializer` calls take a
  source-generated `JsonSerializerContext`;
- reflection discovery, `Activator.CreateInstance` on a computed type,
  `Type.GetType(string)`, `Assembly.Location`, `DependencyContext`, or
  runtime code generation (`Reflection.Emit`, compiled expressions);
- a package that needs any of these. Check a new dependency with an AOT
  publish before taking it.

Do not suppress an IL2xxx or IL3xxx warning to make a build pass; fix the
code, or ask in the pull request. CI publishes a test app on the raw Wayland
host and the managed rasterizer with Native AOT, so a regression shows up there.
That is the route that promises a Native AOT app; the Silk route runs on the
JIT ([why](docs/aot.md#which-route-publishes)).

### Performance

A performance change must not alter pixels. Measure before and after on the
same machine and put the numbers in the pull request.

## Pull requests

- **One change per pull request**, with a title that says what it does.
- **Describe** what changed and why, anything a reviewer must decide, and
  any change to the public API or to pixels. If pixels changed on purpose,
  list the gallery pages affected and add before/after screenshots.
- **Checks**, with numbers: every project in CI's build list builds in
  Release with 0 warnings, and the test programs your change affects pass.
  Say which you ran.
- **Tests** come with the change: a behaviour or layout test for a fix or a
  new component, and a gallery page for a new component.
- **Breaking changes** are called out, with what a caller has to change, and
  get an entry in `CHANGELOG.md` under *Unreleased*. User-visible additions
  and fixes get one too.
- Keep the code in bibui's own terms: comments explain what bibui does and
  why. Name another library only where bibui deliberately reproduces its
  output, and credit it in `NOTICE`.

A maintainer reviews, runs the pixel check where rendering changed, may ask
for changes, and merges.
