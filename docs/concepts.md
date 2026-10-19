# Concepts

This page explains how bibui works: what happens in a frame, how boxes are
sized, how a box finds out it was clicked, and how components, text, hosts and
renderers fit around that. Read it after [Getting started](getting-started.md)
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
3. **Paint.** The painter walks the laid-out tree in order and records draw
   commands into a `DrawList`: fills, borders, shadows, text. It lays nothing
   out and writes nothing back to a node. A renderer draws the list afterwards.

There is no retained widget tree to patch. The build function describes the
whole frame every time. Most nodes come from pools and cost no allocation (see
[Retained and pooled boxes](#retained-and-pooled-boxes)).

A whole frame, drawn headless to a PNG with Skia (`Bibui.Raster.Skia`):

```csharp
var runtime = BibuiRuntime.Create();
runtime.Surface = new SurfaceOptions { Width = 320, Height = 160, ScaleFactor = 1f };
const string hello = "Hello, bibui";
const string ok = "OK";

runtime.BeginBuild();
UiNode.PushIntoParent(runtime.ParentInScope,
    Column(
        runtime.Text(hello),
        Space(12f),
        runtime.Button("ok", ok))
    .FillX().FillY()
    .Padded(EdgeInsets.All(24f)));
runtime.EndBuild();

using var skia = new SkiaCpuRenderer();
File.WriteAllBytes("hello.png", FramePixels.Png(runtime, skia.Draw));
```

This one also needs `using Bibui.Raster.Skia;`.

`BeginBuild()` opens a frame and `EndBuild()` closes it. What is built between
them is the frame: the runtime owns its root, what is built with nothing pushed
lands under it, and a tree made with `Arrange` is linked there with
`UiNode.PushIntoParent(runtime.ParentInScope, ...)`. `EndBuild()` lays the tree
out and records it into `runtime.DrawList`, and draws nothing. The app's state
is its own fields: the build reads them, a click writes them, and the next
frame sees the change. `FramePixels.Png` has the renderer you name draw the
list into a new buffer and encodes it (`FramePixels.Bgra` gives the raw
pixels). There is no screenshot method on the runtime. In a window, the app's
own loop runs the same frame each time the platform's `BeginFrame` returns
(input arrived, or something asked for a frame) and has its renderer draw it
(see [Hosts and renderers](#hosts-and-renderers)).
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
        runtime.Button("save", save),
        Space(8f),                                  // a fixed 8 px gap
        runtime.Button("close", close))
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
everything the build sets (style, children, size, claims, draw callback) so
that "not set this frame" can be expressed. A few things carry over from the frame before:

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

For the common case, a column or row, `runtime.Column()` and `runtime.Row()`
make a pooled box with its axis set, linked under the parent in scope like
any other; push it to build into it. Like `Box()`, they are sized by the sizes
pushed, and with none, not at all, so you usually call a sized form. Each
names the rule for the axes it names, and an axis it does not name is as large
as its children:

| Column | Row | Width | Height |
|---|---|---|---|
| `ColumnFit()` | `RowFit()` | children | children |
| `ColumnFillX()` | `RowFillX()` | parent | children |
| `ColumnFillY()` | `RowFillY()` | children | parent |
| `ColumnFill()` | `RowFill()` | parent | parent |
| `ColumnWidth(px)` | | pixels | children |
| | `RowHeight(px)` | children | pixels |
| | `RowFillX(height)` | parent | pixels |
| `ColumnPx(w, h)` | `RowPx(w, h)` | pixels | pixels |

They return the box, so the size helpers chain on it. Built with nothing
pushed, the page lands under the frame's root:

```csharp
var page = runtime.ColumnFillX().Padded(EdgeInsets.All(24f));
using (runtime.PushParent(page))
{
    runtime.Text(title);
    using (runtime.PushParent(runtime.RowFit()))
    {
        if (runtime.Button("ok", "OK").Clicked) Save();
        runtime.Gap(8f);
        if (runtime.Button("cancel", "Cancel", ghost: true).Clicked) Close();
    }
}
```

`runtime.Spring()` takes the room left along the parent in scope, and
`using (runtime.Centred()) { ... }` puts a spring before and after what is
built inside: centred across a row, that is
`using (runtime.PushParent(runtime.RowFillX())) using (runtime.Centred()) runtime.Text(empty);`.

The two forms mix. A node has one parent, and pushing it somewhere else
(`Row`, `Column`, `CentredY`, `UiNode.PushIntoParent`) **moves** it: it is
taken out of the parent it was linked under and appended to the new one. So
inside a `PushParent` scope, `Row(runtime.Text(title), Spring(), badge)` makes
the text under the parent in scope and then moves it into the row; push the
row where it belongs. Pushing a node into the parent it is already in moves it
to the end. Pushing a node into one of its own descendants throws.

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

A dropdown is open from `runtime.OpenDropDown(id, anchor)` until
`runtime.CloseDropDown(id)` or a press dismisses it. The anchor is the node it
belongs to, its trigger usually. While it is open, `runtime.BeginDropDown(id)`
returns a floating root to build it into, and null when it is not. You build
it inline, wherever its trigger is. When the build returns, the runtime links
it as the last child of the tree, so it is painted over everything. You place
it yourself, usually under the trigger's rectangle from last frame.

While a dropdown is open, **the rectangle it was drawn at last frame belongs to
it**. A box that is not inside the dropdown gets no pointer event and no hover
there, whenever it asks. That is why a dropdown wins even over content built
before it. Its rows ask as usual. The root should ask after them, to take what
they left, so that a press on its padding stops there:

```csharp
SelectScope select = Select.Begin(runtime, "fruit", current);
if (select.Open)
{
    foreach (var (id, label) in fruits)
        if (runtime.SelectItem(id, label, selected: id == chosen).Clicked) { chosen = id; select.Close(); }
}
select.End();
```

`Select.Begin` and `End` do all of this: the trigger's click opens the list or
closes it, and they begin the list under the trigger, push it as the parent
for the rows, and ask for the root last.

Whether a box is inside the dropdown is decided **when it asks**, by walking
its parents. So a control in a dropdown (or a `Dialog` or a `Menu`, which are
dropdowns) must already be linked under it at the moment it asks. Both forms
work there:

- **Statement form** is always right: build under the dropdown, or under a box
  pushed inside it, and every control is linked there as it is built.
- **Expression form** works too, because a component asks inside its `Build`,
  while it is still linked under the parent in scope; `Row`, `CentredY` and
  the rest move it afterwards. Keep the dropdown (or something in it) as the
  parent in scope while the arguments are built, and push the result inside
  the dropdown: `UiNode.PushIntoParent(panel, Row(label, Spring(), (...).Button()))`.

What that rules out is asking a box **after** it has been moved into a row
or column that is not yet linked under the dropdown, for example acquiring a
box with `BoxFromId`, putting it in a `Row`, then calling `InteractionsFor`
before the row is pushed in: at that moment the box is under a row with no
parent, outside the dropdown, and inside the dropdown's rectangle it gets
nothing. Ask before you rearrange, or push the row in first.
`runtime.OpenToolTip()` is linked last too, after the dropdowns, but it claims
nothing and covers nothing: the pointer goes through a tooltip.

A press dismisses an open dropdown in two ways, both in the runtime:

- **A box takes it** that is not under the dropdown's root or its anchor. The
  runtime closes the dropdown in `InteractionsFor`, as the box takes the press,
  so the press still reaches the box. Under is the same walk up the parents as
  above, so the trigger's own press leaves the dropdown to the trigger.
- **Nothing takes it.** After the build, a press no box took closes every open
  dropdown but those opened during the build, which came after it.

What is said last in a frame stands. The box that took the press closed the
dropdown before its caller read what it reported, so a caller that opens the
dropdown on that press opens it again: a right press on another row closes a
context menu and opens it there. A dropdown opened in every frame it is built
is never dismissed, which is how `Dialog` stays up. One that is not begun in a
frame, and not opened in it, closes.

Escape is a key, not a press, so the runtime does not close dropdowns on it.
`Menu` eats an Escape no one has eaten and closes itself; any other dropdown
leaves Escape to the app.

### Keys

Keys are not claimed or asked for. They are the frame's events, read from
`runtime.UnconsumedEvents`, and whoever handles one eats it so nothing after
it sees it. A focused text field eats the keys it takes; what is left is the
app's.

The window translates each key down into an intent for the text fields, so
its `Kind` depends on the key: `Navigate` for the arrows, Home and End (and
Ctrl+A), `Submit` for Enter, `Edit` for Backspace, Delete, Ctrl+X and Ctrl+V,
`Copy` for Ctrl+C, and `Press` for everything else. A shortcut handler
filters on **`BibUiEvent.IsKeyDown`**, true for all of them, and then reads
`Key`; filtering on `Press` alone never sees ↑, ↓ or Enter.

A printable key arrives twice: the key down, and a `Text` event with the
character it typed, which is not a key down. A key that gives a field the
keyboard must eat both, or the field, focused by the time the `Text` is read,
types it. The key down comes first in the same frame's events:

```csharp
bool focusedNow = false;
foreach (var ev in runtime.UnconsumedEvents)
{
    if (ev is { Kind: BibUiEventKind.Text, Char: '/' } && focusedNow) { ev.Eat(); continue; }
    if (!ev.IsKeyDown || searchFocused) continue;
    switch (ev.Key)
    {
        case Key.Slash: searchFocused = focusedNow = true; ev.Eat(); break;
        case Key.Down:  MoveSelection(+1); ev.Eat(); break;
        case Key.Enter: Open(); ev.Eat(); break;
    }
}
```

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
- While anything is still moving, the runtime keeps asking for frames, and
  the window's `BeginFrame` keeps returning. When everything has arrived, the loop goes idle.
- A value nobody asks for in a frame stops moving, and is forgotten soon after.

### Transitions, presence and transforms

Exponential approach suits a hover. For motion with a start and an end, give
`Animate` a `Transition`:

```csharp
// A fixed time along a named curve; a spring that keeps its speed when retargeted.
float slide = runtime.Animate("view", "x", target, Transition.Timed(0.22f, Easing.Emphasized));
Vector2 bar = runtime.Animate("selection", "at", new Vector2(0f, row * 32f), Transition.OnSpring(Spring.Snappy));

// A colour crossfaded in Oklab (a theme token follows a theme swap).
UiColor bg = runtime.AnimateColor(id, "bg", selected ? UiColor.Accent : UiColor.SurfaceRaised);
```

`Easing` has the CSS and Material curves (`EaseOut`, `Standard`,
`Emphasized`, …) or any `cubic-bezier`; `Spring.FromResponse(seconds,
dampingRatio)` makes a spring by feel. `Transition.Delay` holds a value still
before it moves, counted again from every new target.

Something that has to stay on screen while it leaves asks `Presence` every
frame, present or not, and builds while it is `Mounted`:

```csharp
var palette = runtime.Presence("palette", state.PaletteOpen);
if (palette.Mounted)
{
    Box panel = BuildPalette();
    PresenceStyle.FadeDown(16f).ApplyTo(panel.Style, palette.Progress);
}
```

`PresenceList` does the same for the rows of a list: removed rows stay, in
place and with their item, while they collapse, and added rows can stagger in.
`CollapseBox` gives a row the height its presence says:

```csharp
foreach (var row in runtime.PresenceList("inbox", messages, m => m.Id, stagger: 0.02f))
{
    Box holder = runtime.CollapseBox("row-" + row.Key, row.Progress).FillX();
    using (runtime.PushParent(holder)) BuildRow(row.Item, interactive: !row.Exiting);
}
```

`Style.Translate`, `Scale`, `Rotate` and `TransformOrigin` move a node and
its subtree when it is painted, not when it is laid out, so nothing around it
moves; the pointer finds it where it is drawn. `runtime.SetTheme(theme,
transition)` crossfades every colour from the old theme to the new one.

`runtime.ReducedMotion` (or `BIBUI_REDUCED_MOTION=1`) makes all of it, hover
and the renderer's own eased values included, arrive in one step.

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
    public static BibuiWidget Build(BibuiRuntime runtime, string id, ReadOnlySpan<char> label, bool selected)
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
            runtime.Text(label, ComponentSize.Sm, selected ? TextColor.Accent : TextColor.Primary);

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

## Text is UTF-16

bibui holds, measures and draws text as .NET's own chars (UTF-16). UTF-8 only
appears where bytes leave or enter the process: the clipboard, the Wayland
protocol and files.

- `runtime.Text(ReadOnlySpan<char> text, ...)` shows a text, and a component
  label is a `ReadOnlySpan<char>`. A `string` converts to it, so
  `runtime.Text("Save")` and `runtime.Button("save", "Save")` just work.
- The text is **copied** into the node, so it may change as soon as the call
  returns. A literal costs nothing, since literals are made once. To show a
  number without making a string each frame, format it into a stack buffer:
  `Span<char> buf = stackalloc char[16]; count.TryFormat(buf, out int n); runtime.Text(buf[..n]);`
- A `BibuiStr` is a growable char buffer for text the user edits: a text
  field edits one in place, with a `TextEditState` for the caret and
  selection. Both belong to the caller.
- Offsets into text (a caret, a selection, a wrapped line's range) count chars.
  A character such as 👍🏽 can be several chars; the caret steps over it whole.

```csharp
static class Labels
{
    public const string Save = "Save";
    public const string Cancel = "Cancel";
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

### Custom drawing

A box can paint itself: set `Box.Draw` to a callback, and the painter calls it
with the box and a `DrawContext` after the box's decoration and children. The
callback draws in the box's own space: (0,0) is the box's top-left, and
anything drawn outside the box is clipped. A clip, transform or opacity the
callback leaves pushed is popped after it, and so is a paint. `Draw` is build state like any other. Set it on the box the build
acquired, retained or pooled. The next `BoxFromId` or `Box()` clears it, so a
box draws only for as long as its build keeps setting the callback. A chart
that must know when it is hovered draws on its own retained box:

```csharp
Box chart = runtime.BoxFromId("chart", InteractionKinds.LeftPress);
Interaction i = runtime.InteractionsFor(chart);
chart.FillX().FillY();
chart.Draw = (box, ctx) =>
{
    var r = ctx.Logical;                          // the box's rect, logical px, at (0,0)
    Rgba line = ctx.Resolve(UiColor.Accent);      // a token, resolved at paint
    ctx.DrawPolyline(Points(r), line, 2f);
    float x = i.X - box.Rect.X;                   // the pointer is in surface px
    if (i.Hovering) ctx.DrawEdge(x, r.Top, x, r.Bottom, 1f, ctx.Resolve(UiColor.Border), dashed: true);
};
```

Resolve colours in the callback with `ctx.Resolve(UiColor)`, not while
building. `ctx.Theme` is the theme the frame is painted in, the same one the
painter resolves every built-in node against, so a callback follows a theme
that changed between build and paint, and `WithAlpha` applies as it does to a
`Style` colour. Everything a callback draws is recorded into the same
`DrawList` as the built-in nodes, so damage tracking sees it like any other
paint.

The draw context knows textures, not renderers. A callback that draws an image
uses a `TextureHandle` made on the renderer the app chose
(`skia.CreateTexture(format, kind, width, height, pixels)`, or the same call on
`ManagedRenderer`); the app keeps a reference to that renderer, and the
callback draws the handle with `ctx.DrawTexture`.

- **The format** says what the pixels are: `RGBA8` or `BGRA8` (straight
  alpha), `R8` (one grey channel, as a camera's NIR frame), `RG8` (two
  channels) or `A8` (coverage). Each draws with a default look, so any texture
  drawn plainly shows: R8 grey, A8 white coverage, RG8 as red and green, RGBA8
  and BGRA8 as they are.
- **The kind** says how often the pixels change. A `Static` texture is made
  once, and an update to it is ignored. A `Stream` texture keeps its size and
  format, and `UpdateTexture` overwrites its pixels in place: on the GPU, one
  texture that each video frame is uploaded into, with no new image per frame.
- Pixel rows may be padded, as a video decoder's are: pass their stride as
  `rowBytes`.
- Textures are made, updated and released on the thread that draws.

## Hosts and renderers

The runtime is independent of the window and of the rasterizer. It holds no
raster backend: each frame it records a `DrawList`, and a renderer that the
owner of the loop chose draws that list. Two pieces connect it to the outside:

- A **window** (`SilkWindow`, `WaylandWindow`, `BrowserWindow`) owns the native window, the
  event pump, the framebuffer, presenting, the clipboard and the cursor. It
  knows nothing of any renderer. The app owns the loop:
  `while (SilkWindow.BeginFrame(window, runtime)) { runtime.BeginBuild(window.Events); build; runtime.EndBuild(); SilkWindow.BeginDraw(window); draw; SilkWindow.EndDraw(window); SilkWindow.EndFrame(window, runtime); }`.
  `BeginFrame` sleeps while nothing is happening and returns when there is a
  frame to draw, with what the keyboard and mouse did in `window.Events`;
  `BeginBuild` turns that into the frame's events. `BeginDraw` readies the
  window to be drawn into (its GL context current; on Wayland, a free buffer
  in `window.Cpu`), `EndDraw` puts what was drawn on screen (a swap; a
  commit), and `EndFrame` paces the loop. A window offers two targets:
  `window.Gl`, a `GlTarget` (the GL framebuffer, Silk only; `Bibui.Gl`), and `window.Cpu`,
  a `CpuTarget` (memory the window presents: a `wl_shm` buffer on Wayland,
  the canvas's frame in the browser).
  CPU memory on a Silk window is `GlCpuFramebuffer`'s, below. bibui does no discovery and no
  reflection. The browser is the one host whose loop the app does not own,
  below.
- A **renderer** draws a `DrawList`: clipping, transforms, shapes, paths,
  textures and glyphs. Bound to a window's target, `renderer.Draw(runtime.DrawList)`
  draws there: `new SkiaGlRenderer(window.Gl)`, `new SkiaCpuRenderer(window.Cpu)`
  or `new ManagedRenderer(window.Cpu)`. Unbound, `SkiaCpuRenderer` draws into an
  `SKSurface` or into `RasterPixels` (BGRA8888 premultiplied memory the caller
  owns), and `ManagedRenderer` into `RasterPixels`. Each also makes the
  textures its lists draw. There is no default: you construct the one you
  want. Code that draws into memory it owns (`FramePixels`, `BibuiPage`, the
  test server) takes a `DrawToPixels`, a
  `void (DrawList list, RasterPixels target)` delegate, and a renderer's `Draw`
  method group converts to it.

There are three complete combinations.

[`samples/counter`](../samples/counter/Program.cs) is the first, in full.

**Cross-platform: Silk window and Skia raster.** `SilkWindow` (package
`Bibui.Platform.Silk`) opens a window through GLFW and presents with OpenGL. It
references no renderer; the app adds `Bibui.Raster.Skia` and binds a
`SkiaGlRenderer` to the window's GL framebuffer:

```csharp
using Bibui.Raster.Skia;
using Bibui.Runtime.Platform;

var runtime = BibuiRuntime.Create();
using var window = SilkWindow.Open(runtime, new PlatformWindowOptions("My app", 960, 640));
using var skia = new SkiaGlRenderer(window.Gl);
while (SilkWindow.BeginFrame(window, runtime))
{
    runtime.BeginBuild(window.Events);
    BuildUi(runtime);
    runtime.EndBuild();
    SilkWindow.BeginDraw(window);
    skia.Draw(runtime.DrawList);
    SilkWindow.EndDraw(window);
    SilkWindow.EndFrame(window, runtime);
}
```

A Silk window only ever shows what is drawn into its GL framebuffer. A renderer
that draws on the CPU draws into memory that `GlCpuFramebuffer` (package
`Bibui.Gl`) shows there: each frame it uploads the memory into a texture and
blits it onto the framebuffer. It takes a GPU renderer's place in the loop: a
frame is drawn into the framebuffer by one or the other, never both.
[`samples/counter-cpu`](../samples/counter-cpu/Program.cs) is the counter drawn
this way, with `SkiaCpuRenderer`.

```csharp
using var shown = new GlCpuFramebuffer(window.Gl);       // Bibui.Gl: memory shown on the GL framebuffer
using var managed = new ManagedRenderer(shown.Cpu);       // or new SkiaCpuRenderer(shown.Cpu)
while (SilkWindow.BeginFrame(window, runtime))
{
    runtime.BeginBuild(window.Events);
    BuildUi(runtime);
    runtime.EndBuild();
    SilkWindow.BeginDraw(window);                         // the window's GL context current
    shown.BeginDraw();                                    // the memory at the framebuffer's size now
    managed.Draw(runtime.DrawList);
    shown.EndDraw();                                      // upload, blit onto the framebuffer
    SilkWindow.EndDraw(window);                           // swap
    SilkWindow.EndFrame(window, runtime);                 // pace
}
```

`SkiaGlRenderer.ForGl` answers null where Skia cannot drive the GL context;
falling back to the CPU, as above, is the app's choice.

Every renderer has the same texture and draw calls (`CreateTexture`,
`UpdateTexture`, `ReleaseTexture`, `Draw`), and build code never sees the
renderer, so an app names its renderer in one place and swapping it is that
line and the setup:

```csharp
global using Renderer = Bibui.Raster.Skia.SkiaGlRenderer;   // or Bibui.Raster.Managed.Backend.ManagedRenderer
```

Textures belong to the renderer that made them: a renderer swapped while the
app runs makes its textures again.

**Linux, pure managed: Wayland window and managed raster.** `WaylandWindow`
(package `Bibui.Platform.Wayland`) talks to the compositor directly through
libwayland-client. It uses no GLFW and no GL, and presents shared-memory
buffers. `ManagedRenderer` (package `Bibui.Raster.Managed.Backend`) is a
rasterizer written in C#, with FreeType and HarfBuzz for text, so this
combination ships no Skia. Bind it to the window's `Cpu`:

```csharp
using Bibui.Platform.Wayland;
using Bibui.Raster.Managed.Backend;
using Bibui.Runtime.Platform;

var runtime = BibuiRuntime.Create();
if (!WaylandWindow.IsSupported) return;
using var window = WaylandWindow.Open(runtime, new PlatformWindowOptions("My app", 960, 640),
    new WaylandHostOptions { AppId = "my-app" });
using var managed = new ManagedRenderer(window.Cpu);
while (WaylandWindow.BeginFrame(window, runtime))
{
    runtime.BeginBuild(window.Events);
    BuildUi(runtime);
    runtime.EndBuild();
    WaylandWindow.BeginDraw(window);
    managed.Draw(runtime.DrawList);
    WaylandWindow.EndDraw(window);
    WaylandWindow.EndFrame(window, runtime);
}
```

**In the browser: a canvas and managed raster.** `BrowserWindow` (package
`Bibui.Platform.Browser`) is a `<canvas>` in a .NET WebAssembly app, drawn on
the CPU by `ManagedRenderer` and shown with `putImageData`. A page may not
block, so the browser owns the loop: the app hands the frame's body to
`BrowserWindow.Run`, which returns at once, and the browser calls it on
`requestAnimationFrame` whenever the window has input, owes frames or was
resized, and its canvas is in view. Several windows on one page share one
loop. [bibui in the browser](browser.md) has the details.

```csharp
using Bibui.Platform.Browser;
using Bibui.Raster.Managed.Backend;

var runtime = BibuiRuntime.Create();
var window = await BrowserWindow.OpenAsync(runtime, "app");   // <canvas id="app">
var managed = new ManagedRenderer(window.Cpu);
BrowserWindow.Run(window, runtime, () =>
{
    runtime.BeginBuild(window.Events);
    BuildUi(runtime);
    runtime.EndBuild();
    BrowserWindow.BeginDraw(window);
    managed.Draw(runtime.DrawList);
    BrowserWindow.EndDraw(window);
});
```

**Damage tracking.** The managed rasterizer can rasterise only what changed.
With `new ManagedRenderer { DamageTracking = true }` (or `BIBUI_DAMAGE=1`),
the renderer compares each `DrawList` draw by draw with the one before, and rasterises only the
rectangles that differ onto a surface that still holds an earlier frame, such
as the Wayland window's shared-memory buffers. Nothing above the rasterizer
changes: every frame is still built, laid out and recorded whole, and the window
still tells the compositor the whole surface changed. The result is
byte-identical to a full repaint, and past half the surface the whole frame is
drawn. Skia always draws whole frames. See [Performance](performance.md) for
measurements.

A Wayland window can also draw with Skia (`new SkiaCpuRenderer(window.Cpu)`), and
a headless program (tests, a command-line tool that writes PNGs) needs no window
at all. It builds between `runtime.BeginBuild()` and `runtime.EndBuild()` and
draws `runtime.DrawList` into its own buffer, as in [The frame](#the-frame-build-layout-paint).

**Native AOT.** Every bibui library is Native AOT compatible: there is no
reflection-based serialization, no runtime code generation and no discovery.
Theme files are read with `System.Text.Json`'s DOM, and renderers and windows are
constructed by the app, not looked up. An app on the raw Wayland window and the managed
rasterizer publishes as a single native executable:

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

The Silk + Skia route is JIT-only for now: Silk.NET itself warns under AOT,
so Native AOT is promised for the Wayland + managed route only.

An AOT app chooses its renderer like any other, for example
`new ManagedRenderer()`. Nothing is loaded by name.
