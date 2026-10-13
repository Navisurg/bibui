# Concepts

This page explains how bibui works: what happens in a frame, how boxes are
sized, how a box finds out it was clicked, and how components, text, hosts and
raster backends fit around that. Read it after [Getting started](getting-started.md)
and before writing your first component. For each component's API, see
[Components](components/). For colours and themes, see [Theming](theming.md).

The samples on this page assume these usings:

```csharp
using Bibui;
using Bibui.Runtime;
using Bibui.Theming;
using static Bibui.Arrange;
```

## The frame: build, layout, paint

Every frame runs three passes, one after the other, and they never mix:

```
build (your code)  ──►  layout (the solver)  ──►  paint (the painter)
```

1. **Build.** Your build function runs and describes the whole UI as a tree of
   nodes: boxes, texts, icons. It also *asks* boxes what happened to them since
   the last frame (see [Claims and asks](#claims-and-asks)). It positions
   nothing. Each node only says how large it wants to be.
2. **Layout.** The solver turns those sizes into a rectangle for every node in
   the tree, all at once, before anything is drawn.
3. **Paint.** The painter walks the laid-out tree in order and issues raster
   calls: fills, borders, shadows, text. It lays nothing out and writes nothing
   back to a node.

There is no retained widget tree to patch. The build function describes the
whole frame every time. Most nodes come from pools and cost no allocation (see
[Retained and pooled boxes](#retained-and-pooled-boxes)).

A whole frame, rendered headless to a PNG:

```csharp
var runtime = new BibuiRuntime();          // Skia, when Bibui.Raster.Skia is referenced
byte[] hello = "Hello, bibui"u8.ToArray();
BibuiStr ok = BibuiStr.FromStrLit("OK"u8);

runtime.Mount(() =>
    Column(
        runtime.Text(hello),
        Space(12f),
        Button.Build(runtime, "ok", ok))
    .FillX().FillY()
    .Padded(EdgeInsets.All(24f)),
    new SurfaceOptions { Width = 320, Height = 160, ScaleFactor = 1f });

File.WriteAllBytes("hello.png", runtime.Render().PngBytes);
```

`Mount` takes the build function. `MountStateful` takes a build function plus
a state value and a `SetState<T>` handle that sets the value and asks for a
frame. `Render()` runs one frame and gives back its pixels. In a window,
`BibuiWindow` runs the same frame each time input arrives, or when something
asks for a frame (see [Hosts and raster backends](#hosts-and-raster-backends)).
Every frame starts cleared to the theme's `Background`, so the root needs no
background of its own.

Two timing rules follow from the three passes:

- **Asks test last frame's rectangles.** Layout runs after the build, so while
  the build runs, a box only knows where it was drawn last frame. A box's first
  frame is therefore not interactive. A dropdown placed under its trigger
  follows the trigger one frame late.
- **Eased values step at the end of the frame.** `HotT`, `ActiveT` and
  `runtime.Animate` read the value the previous frame settled on, so a build
  that runs twice for the same input gets the same answer both times.

## Boxes and sizes

A node says its size on each axis as a `SemanticSize`, that is, by what the
size depends on:

| Size | Helper | Depends on |
|---|---|---|
| `SemanticSize.Pixels(n)` | `.Px(width, height)` | nothing: exactly *n* logical pixels |
| `SemanticSize.Text()` | (what `runtime.Text` and `runtime.Icon` set) | what the node holds: a text's measured lines, an icon's side |
| `SemanticSize.ParentPct(f)` | `.FillX(f)`, `.FillY(f)` | the parent: a share of the space inside its padding |
| `SemanticSize.ChildrenSum()` | (what `Row` and `Column` set) | the children: summed along the layout axis, the largest across it, plus padding |

A box lays out its children along its `ChildLayoutAxis`: `LayoutAxis.X` for a
row, `LayoutAxis.Y` for a column. `Arrange` gives the common shapes:

```csharp
Box toolbar = Row(
        runtime.Text(title),
        Spring(),                                   // takes the room left, pushing what follows to the end
        Button.Build(runtime, "save", save),
        Space(8f),                                  // a fixed 8 px gap
        Button.Build(runtime, "close", close))
    .FillX()                                        // as wide as the parent
    .Px(height: 40f);                               // and exactly 40 px tall
```

How the solver uses those sizes:

- **Fills share the room.** Along the layout axis, a fill starts at its full
  share of the parent. If the children then overflow, each gives up space in
  proportion to its *strictness*: a fill gives up all it needs to, and pixels,
  text and children-sized nodes give up nothing. In a 400 px row, `.FillX()`
  next to `.Px(100f)` ends up 300 px wide. `.FillX(1f)` next to `.FillX(2f)`
  splits the row one third to two thirds. Every `SemanticSize` factory takes an
  optional `strictness` from 0 (gives up everything) to 1 (never shrinks).
- **Across the layout axis**, a child larger than its parent is cut to the
  parent.
- **Floating nodes** (`.At(x, y)`) are out of the flow. They sit at that offset
  from the parent's top-left corner, and the parent's size does not count them.
- **The root** is always the size of the surface, whatever it asks for. Its
  children can fill it.

### No default size, no answer for a ring

The solver is deliberately small. Per axis (X first, so a text's height is
measured at the width it will actually have), it works out:

1. the sizes that depend on nothing;
2. the sizes that depend on the parent, top-down;
3. the sizes that depend on the children, bottom-up;
4. the constraints: shrinking what overflows;
5. the positions.

It has **no default size**. A node that says no size on an axis throws a
`LayoutException` that names the node and its nearest ancestor with an id. A
default would turn a forgotten size into a layout that is almost right. The
exception tells you where the problem is.

It has **no answer for a ring**. Suppose a node takes a share of its parent,
and the parent is sized by its children. Then the child depends on the parent
and the parent depends on the child. Several rules have been tried for this
case (count the content, take a share of the nearest sized ancestor, a second
pass). Each one made one layout work and made the next surprising. So a ring
throws, and the layout code has to say what it meant.

### The usual mistakes

When a layout throws, collapses to zero or looks wrong, the cause is almost
always the code that built it, not the solver.

**A fill inside a parent sized by its children.** The row below is as wide as
its children, and the rule inside it wants a share of the row's width:

```csharp
Box row = Row(runtime.Text(title), runtime.Box().FillX().Px(height: 1f));
// LayoutException: Unsolvable layout ring along X: Box > Box > Box (beside "Title") takes a share
// of its parent, and the parent is sized by its children. Give the parent a size along X (pixels,
// or a share of a sized ancestor), or size this node by its content.
```

Fix it by giving the parent a size, here a share of a parent that has one, or
by giving the child a size of its own:

```csharp
Box row = Row(runtime.Text(title), runtime.Box().FillX().Px(height: 1f)).FillX();
```

**Centring in a row whose height comes from what it centres.** `CentredY`
builds a column that fills the row's height. If the row is sized by its
children, that is a ring on Y:

```csharp
Box item = Row(runtime.Icon("check"), Space(8f), CentredY(runtime.Text(title)));   // ring on Y
```

Give the row a height, and the springs have something to share:

```csharp
Box item = Row(runtime.Icon("check"), Space(8f), CentredY(runtime.Text(title))).Px(height: 32f);
```

**A box with no size, or a size on the wrong axis.** `runtime.Box()` has no
size of its own. Setting `SizeX` twice where one of them should have been
`SizeY` leaves Y unsaid:

```csharp
Box swatch = runtime.Box();
swatch.SizeX = SemanticSize.Pixels(16f);
swatch.SizeX = SemanticSize.Pixels(16f);     // meant SizeY: "No size along Y for Box > Box."
```

`swatch.Px(16f, 16f)` says both at once. To give every box in a block the same
size, `runtime.PushSizeX(...)` / `runtime.PushSizeY(...)` set the size of the
boxes acquired inside a `using` scope.

**A scroll area that does not scroll.** Children that run past a box are cut
to it unless the box allows overflow along that axis (`AllowOverflowY = true`).
A box that should scroll allows overflow and claims the wheel
(`InteractionKinds.ViewScrollY | InteractionKinds.ClampY`), and the solver
offsets its children by its `ViewOffset`.

## Retained and pooled boxes

There are two ways to get a box, and the difference is identity.

`runtime.Box()` gives a **pooled** box. It has no name. The *n*-th call in a
frame gets the *n*-th box from a pool, and the pool rewinds when the frame
ends. It is cleared each time, claims nothing and allocates nothing. Use it for
a group, a pill or a spacer: anything nothing will ask about.

`runtime.BoxFromId(id, claims)` gives a **retained** box: the same instance
every frame, for as long as something keeps asking for it. Each call clears
everything the build sets (style, children, size, claims) so that "not set this
frame" can be expressed. A few things carry over from the frame before:

- `Rect` and `Extent`, where it was drawn last frame, which is what an ask tests;
- `HotT` and `ActiveT`, how hovered and how pressed it is, eased over frames;
- `ViewOffset` and `TargetViewOffset`, for a box that scrolls.

A retained box nobody asks for is dropped after 60 frames. There is nothing to
register or release. Two `BoxFromId` calls with the same id in one frame return
the *same* box, so give siblings distinct ids (`"row-" + item.Id`, not
`"row"`).

`runtime.Text`, `runtime.Icon` and `runtime.Gap` are pooled like `Box()`.
`runtime.TextFromId(id, ...)` is the retained form of a text.

Pooled or retained, a node built through the runtime is linked under the
**parent in scope**: the box most recently pushed with `PushParent`. When
nothing is pushed it is linked nowhere, and you place it yourself, for example
as an argument to `Row` or `Column`:

```csharp
Box card = runtime.BoxFromId("card", InteractionKinds.LeftPress);
card.ChildLayoutAxis = LayoutAxis.Y;
card.Px(240f, 120f);
card.Style.Padding = EdgeInsets.All(12f);
card.Style.BackgroundColor = UiColor.SurfaceRaised;

using (runtime.PushParent(card))
{
    runtime.Text(title);                                   // linked under card
    runtime.Gap(4f);                                       // 4 px along card's axis
    runtime.Text(subtitle, ComponentSize.Sm, TextColor.Secondary);
}
```

Every `Push…` returns a scope to dispose. If a scope is still open when the
build returns, the runtime throws rather than linking next frame's boxes under
this frame's parent.

## Claims and asks

bibui has no event callbacks and no hit-test pass. A box says what it takes
when it is acquired (its *claims*), and the build function asks the box it
holds what happened:

```csharp
Box track = runtime.BoxFromId("volume", InteractionKinds.LeftPress);
Interaction i = runtime.InteractionsFor(track);

if (i.Clicked) muted = !muted;                    // pressed on it and released on it
if (i.Dragging) volume = Math.Clamp((i.X - track.Rect.X) / track.Extent.X, 0f, 1f);
```

The claims are `InteractionKinds` flags:

| Claim | Takes |
|---|---|
| `LeftPress`, `RightPress`, `MiddlePress` (`Press` for all three) | that button's press, its release, and the drag in between. A box that takes a press can be hovered, so `Hovering` and `HotT` come with it. There is no separate hover claim. |
| `ViewScrollX`, `ViewScrollY` (`ViewScroll`) | the wheel, which moves the box's `TargetViewOffset` |
| `Scroll` | the wheel, reported in `ScrollX`/`ScrollY` and moving nothing (for a zoom, say) |
| `ClampX`, `ClampY` (`Clamp`) | nothing; keeps the view offset inside the content |
| `Clip` | nothing; children are drawn, and pressed, only inside the box |
| `Disabled` | nothing, and swallows presses (below) |
| `None` | nothing: the box is not asking to be interacted with |

`Interaction` reports `Hovering`, `Pressed`, `Released`, `Clicked`,
`Dragging` with `DragStartX/Y` and `DragDeltaX/Y`, the right- and
middle-button equivalents, `ScrollX/Y`, the pointer's `X`/`Y` and the
`Modifiers` held. Act on `Clicked` rather than `Pressed`: someone who presses,
slides off and lets go has changed their mind.

**`Disabled` is a claim, not the absence of one.** A control that must be there
but takes nothing claims `InteractionKinds.Disabled`. It reports nothing *and*
swallows the press, so the press does not fall through to the panel beneath. A
box that claims `None` takes nothing and blocks nothing.

### Ask order is z-order

An ask tests the pointer against the rectangle the box was drawn at last
frame, and **asking consumes**. The first box to ask about a point gets the
event, and boxes that ask after it get nothing there. Nothing else decides
which box is on top: not tree depth, not paint order. Two rules follow:

- **A container that claims an area asks after the controls inside it.**
  Otherwise the container takes their presses. Being their parent does not
  give way to them by itself.
- **An overlay asks before the content beneath it**, for the same reason.

This works because *acquiring*, *asking* and *describing* are three separate
steps, and only asking has an order that matters:

```csharp
var panel = runtime.BoxFromId("panel", InteractionKinds.LeftPress);   // acquire
var knob  = runtime.BoxFromId("knob",  InteractionKinds.LeftPress);

Interaction onKnob  = runtime.InteractionsFor(knob);    // ask, innermost first
Interaction onPanel = runtime.InteractionsFor(panel);   // then the container

panel.Px(200f, 200f);                                  // describe, in any order, afterwards
knob.Px(20f, 20f).At(onKnob.Dragging ? onKnob.X - panel.Rect.X : 90f, 90f);
UiNode.PushIntoParent(panel, knob);
```

An ask needs no geometry, because it tests last frame's rectangle. So you can
acquire a whole subtree with its claims, ask in whatever order the z-order
needs, and only then lay it out using the state the asks produced. A canvas of
draggable cards works this way: the card contents ask, then the cards, then the
background, and every card is positioned afterwards from where the drags left
it.

`InteractionsFor` takes the box rather than its name so this cannot go wrong
by accident. The only way to hold a box is `BoxFromId`, which clears it and
sets its claims in the same call, so holding a box proves it was described
this frame.

### Dropdowns are the exception

`runtime.OpenDropDown(id)` returns a floating root to build a dropdown into.
You build it inline, wherever its trigger is. When the build returns, the
runtime links it as the last child of the tree, so it is painted over
everything. You place it yourself, usually under the trigger's rectangle from
last frame.

While a dropdown is open, **the rectangle it was drawn at last frame belongs to
it**. A box that is not inside the dropdown gets no pointer event and no hover
there, whenever it asks. That is why a dropdown wins even over content built
before it. Its rows ask as usual. The root should ask after them, to take what
they left, so that a press on its padding stops there:

```csharp
SelectScope select = Select.Begin(runtime, "fruit", current, open: fruitOpen);
if (select.Open)
{
    foreach (var (id, label) in fruits)
        if (Select.Item(runtime, id, label, selected: id == chosen).Clicked) { chosen = id; fruitOpen = false; }
}
if (select.End().Toggled) fruitOpen = !fruitOpen;
```

`Select.Begin` and `End` do all of this: they open the dropdown under the
trigger, push it as the parent for the rows, and ask for the root last.
`runtime.OpenToolTip()` is linked last too, after the dropdowns, but it claims
nothing and covers nothing: the pointer goes through a tooltip.

## Hot, active and animation

A retained box that claims a press carries two eased values:

- **`HotT`** goes towards 1 while the pointer is over the box and towards 0
  when it leaves. It moves at `theme.Motion.Hover`.
- **`ActiveT`** jumps to 1 on a press, because a press that fades in under the
  finger feels slow, and eases back to 0 on release.

Both are written only by an ask. A box that claims nothing, or never calls
`InteractionsFor`, stays cold however long the pointer rests on it. Reading
`HotT` is how a component opts in to a hover look:

```csharp
Box chip = runtime.BoxFromId("chip", InteractionKinds.LeftPress);
runtime.InteractionsFor(chip);
chip.Px(80f, 28f);
chip.Style.CornerRadius = 14f;
chip.Style.BackgroundColor = UiColor.Accent.WithAlpha(0.15f + 0.25f * chip.HotT - 0.1f * chip.ActiveT);
```

The `Style` treatments do the same without arithmetic. `HoverBackgroundColor`
and `DrawFlags.HotEffects` / `ActiveEffects` are blended by `HotT` and
`ActiveT` when the box is painted.

For anything else that should glide, `runtime.Animate(id, property, target)`
returns where the value has got to on its way to `target`:

```csharp
float open = runtime.Animate("sidebar", "width", sidebarOpen ? 1f : 0f);
Box sidebar = runtime.Box().Px(width: 48f + 192f * open).FillY();
```

- The first time a value is asked for, it starts at its target, so a screen
  does not slide into place on the frame it first appears.
- Speed is in halvings per second: the value closes half of the remaining gap
  every `1/speed` seconds, at any frame rate. It defaults to
  `theme.Motion.Default`, and a theme with `Motion.Easing = MotionEasing.None`
  (reduced motion) makes every value arrive in one step.
- While anything is still moving, the runtime keeps asking the host for
  frames. When everything has arrived, the loop goes idle.
- A value nobody asks for in a frame stops moving, and is forgotten soon after.

If your own state changes outside a build (a timer, a network reply), call
`runtime.RequestFrame()`. It is safe from any thread, and it wakes a window
that is waiting for input.

## Components are functions

A component is a plain static function. It composes boxes and returns a
`BibuiWidget`: the node to place, plus the `Interaction` the caller acts on.
There is no component base class, no renderer per component, and no callback:

```csharp
public static class Chip
{
    public static BibuiWidget Build(BibuiRuntime runtime, string id, BibuiStr label, bool selected)
    {
        Box chip = runtime.BoxFromId(id, InteractionKinds.LeftPress);
        Interaction interaction = runtime.InteractionsFor(chip);

        chip.ChildLayoutAxis = LayoutAxis.X;
        chip.SizeX = chip.SizeY = SemanticSize.ChildrenSum();
        chip.Style.Padding = EdgeInsets.Symmetric(horizontal: 12f, vertical: runtime.Theme.Density.Of(4f));
        chip.Style.CornerRadius = runtime.Theme.Radii.Full;
        chip.Style.BorderWidth = runtime.Theme.Borders.Width;
        chip.Style.BorderColor = selected ? UiColor.Accent : UiColor.Border;
        chip.Style.BackgroundColor = selected ? UiColor.Accent.WithAlpha(0.15f) : (UiColor?)null;
        chip.Style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;

        using (runtime.PushParent(chip))
            runtime.Text(label.AsMemory(), ComponentSize.Sm, selected ? TextColor.Accent : TextColor.Primary);

        return new BibuiWidget(chip, interaction);
    }
}
```

The rules that make components compose:

- **The component owns no state and does not write the caller's.** It takes
  the value to draw and reports what happened. The caller applies it to its own
  state:

  ```csharp
  var chip = Chip.Build(runtime, "only-unread", onlyUnreadLabel, onlyUnread);
  if (chip.Clicked) onlyUnread = !onlyUnread;
  ```

  Anything that has to survive between frames lives on a retained box (`HotT`,
  `ActiveT`, `Rect`) or in `runtime.Animate`, not in the component.
- **Take the value to draw, not the state to interpret.** A `bool` can only
  put a switch's knob at one end. Where a component animates, it eases the
  value itself (as `Switch` does with `runtime.Animate`) or takes an eased
  0..1 from the caller.
- **No theme parameter.** Colours are tokens such as `UiColor.Accent`, resolved
  against the live theme when the frame is painted. Numbers the build needs
  (border widths, density, motion speeds) are read from `runtime.Theme`. See
  [Theming](theming.md).
- **Claim at the door.** Pass the claims to `BoxFromId`, and pass
  `InteractionKinds.Disabled` (not `None`) when the component is disabled.

`BibuiWidget` converts to `UiNode` wherever a node is expected, and goes
straight into the argument list of `Row` or `Column`, as the samples above
show.

`Button`, `Switch`, `Checkbox`, `Slider`, `Select` and the rest of the built-in
components are written exactly this way. Their source is the best reference
for writing your own.

## Text is UTF-8

bibui holds, measures and draws text as UTF-8 bytes. No `string` sits between
your text and the glyphs.

- `runtime.Text(ReadOnlyMemory<byte> text, ...)` shows a text. A `byte[]`
  converts to it.
- A component label is a `BibuiStr`, a growable UTF-8 buffer. Make fixed labels
  once from a UTF-8 literal: `BibuiStr.FromStrLit("Save"u8)`. Pass `label.AsMemory()`
  to `runtime.Text`.
- The text is **held, not copied**. Keep the memory unchanged until the frame
  has been painted. Formatting a new value each frame is fine:
  `runtime.Text(Encoding.UTF8.GetBytes($"{count} items"))`.
- A text field edits a `BibuiStr` in place, with a `TextEditState` for the
  caret and selection. Both belong to the caller.

```csharp
static class Labels
{
    public static readonly BibuiStr Save = BibuiStr.FromStrLit("Save"u8);
    public static readonly BibuiStr Cancel = BibuiStr.FromStrLit("Cancel"u8);
}
```

Use `runtime.PushFontSize(...)` and `runtime.PushFontFace(...)` to set the size,
family and weight for every text built inside a scope.

## Painting

The painter draws the laid-out tree in order, depth first, so a child is drawn
over its parent and a later sibling over an earlier one. Dropdowns and the
tooltip are linked last, so they are drawn last. For each box it draws the
shadow, background, border and children, clipped where the box claims `Clip`.
It resolves colour tokens against the current theme as it goes.

The painter only reads nodes. It never sizes or moves one, and it writes
nothing back for the next frame. So the picture is a pure function of the tree
and the theme. Given the same tree, theme, surface size and scale factor, a
frame is the same image, byte for byte, every time.

**Damage tracking.** With `runtime.DamageTracking = true` (or
`BIBUI_DAMAGE=1`), `runtime.RenderTo(surface)` records the frame instead of
drawing it straight away. It compares the record with the frame the surface
already holds and repaints only the rectangles that differ.
`runtime.LastDamage` tells the host what to present. The result is
byte-identical to a full repaint, and the runtime falls back to a full repaint
when more than half the surface changed. See [Performance](performance.md) for
measurements.

## Hosts and raster backends

The runtime is independent of the window and of the rasterizer. Two interfaces
connect it to the outside:

- An **`IPlatformHost`** owns the native window, the event pump, the
  framebuffer, presenting, the clipboard and the cursor. `BibuiWindow` runs the
  frame loop on top of a host: it paces frames, translates input and sleeps
  while nothing is happening. You always pass the host explicitly. bibui does
  no discovery and no reflection.
- An **`IRasterBackend`** draws: surfaces, clipping, transforms, shapes, paths,
  images, text and PNG encoding. The parameterless `new BibuiRuntime()` uses
  the default backend. `new BibuiRuntime(backend)` uses the one you pass.

There are two complete combinations.

**Cross-platform: Silk host and Skia raster.** `SilkPlatformHost` (package
`Bibui.Platform.Silk`) opens a window through GLFW and renders with OpenGL when
it can, falling back to the CPU otherwise (`BIBUI_GPU=0` forces the CPU). It
brings in `Bibui.Raster.Skia`, which registers Skia as the default backend:

```csharp
using Bibui.Runtime.Platform;

var runtime = new BibuiRuntime();
runtime.Mount(() => BuildUi(runtime));
new BibuiWindow(runtime, new SilkPlatformHost()).Run("My app");
```

**Linux, pure managed: Wayland host and managed raster.** `WaylandPlatformHost`
(package `Bibui.Platform.Wayland`) talks to the compositor directly through
libwayland-client. It uses no GLFW and no GL, and draws on the CPU straight into
shared-memory buffers. `ManagedRasterBackend` (package
`Bibui.Raster.Managed.Backend`) is a rasterizer written in C#, with FreeType and
HarfBuzz for text, so this combination ships no Skia. Pass the backend
explicitly:

```csharp
using Bibui.Platform.Wayland;
using Bibui.Raster.Managed.Backend;

var runtime = new BibuiRuntime(new ManagedRasterBackend());
runtime.Mount(() => BuildUi(runtime));
if (WaylandPlatformHost.IsSupported)
    new BibuiWindow(runtime, new WaylandPlatformHost(new WaylandHostOptions { AppId = "my-app" })).Run("My app");
```

A Wayland host can also draw with Skia, and a headless program (tests, a
command-line tool that renders PNGs) needs no host at all. It calls
`runtime.Render()`.

**Native AOT.** Every bibui library is Native AOT compatible: there is no
reflection-based serialization, no runtime code generation and no discovery.
Theme files are read with `System.Text.Json`'s DOM, and backends and hosts are
passed in, not looked up. An app on the raw Wayland host and the managed
rasterizer publishes as a single native executable:

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

The Silk + Skia route is JIT-only for now: Silk.NET itself warns under AOT,
so Native AOT is promised for the Wayland + managed route only.

An AOT app passes its backend, for example
`new BibuiRuntime(new ManagedRasterBackend())`. The parameterless
`new BibuiRuntime()` loads the default (Skia) backend by name, which an AOT
build may have dropped, so an AOT publish that calls it warns (IL2026).
