# RowScrollList and ScrollList

Two vertical lists that build only the rows in sight, so a list of a hundred
thousand rows costs what its screenful does, and a bar for either. Both keep a
`ScrollPt` the caller owns, and both ease: the wheel moves the target, and
what is shown glides there.

- **RowScrollList**: rows that all have one height, `pitch` apart, scrolled by
  rows or by pixels. Everything follows from the pitch, so nothing is walked or
  estimated.
- **ScrollList**: rows of their own heights, scrolled by pixels. It holds no
  heights: one loop hands them in as it reaches each row, so a height must be
  known before the row is built (from what the row holds: a line height per
  line, and padding).

The gallery's ScrollList page shows both, with a switch to scroll the first by
pixels.

## Build

```cs
// Rows of one pitch
public static (int First, int Last) BeginRowScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, float pitch, int count, ScrollBy by = ScrollBy.Rows)
public static void EndRowScrollList(this BibuiRuntime runtime, ref ScrollPt pt, float pitch, int count, ScrollBy by = ScrollBy.Rows)

// Rows of their own heights
public static ScrollRows BeginScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count)
public bool ScrollRows.Next(ref ScrollPt pt, float height)
public static void EndScrollList(this BibuiRuntime runtime, ref ScrollPt pt, in ScrollRows rows)

// The bar, beside either
public static void Scrollbar(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count, int first, int last)
public const float ScrollList.BarLane = 12f;
```

| Parameter | Meaning |
|---|---|
| `id` | The view's retained box. The bar's are `{id}` and `{id}-thumb`. |
| `pt` | Where the list is: the target, `Px` pixels below the top of row `Idx`, and `Off`, the pixels what is shown still is from it, eased to 0. Kept by the caller between frames. |
| `pitch` | RowScrollList: the distance from one row's top to the next. Build each row exactly this tall. |
| `count` | How many rows there are. |
| `by` | RowScrollList: `ScrollBy.Rows`, the wheel moves whole rows and the view rests on a row's top, the last row clipped at the end; `ScrollBy.Pixels`, the wheel moves pixels and the last row rests on the bottom. |
| `height` | ScrollList: the height of row `rows.Row`. Build the row exactly this tall when `Next` says so. |
| `first`, `last` | The bar: the rows the list showed this frame, `[first, last)`. |

### RowScrollList

```csharp
var (first, last) = runtime.BeginRowScrollList("log", ref scroll, 24f, lines.Count, ScrollBy.Pixels);
for (int i = first; i < last; i++) Line(runtime, lines[i]);
runtime.EndRowScrollList(ref scroll, 24f, lines.Count, ScrollBy.Pixels);
runtime.Scrollbar("log-bar", ref scroll, lines.Count, first, last);
```

Something several rows tall is the caller's to map onto whole rows: it covers
rows `i` to `i + n`, and is built at the first of them in sight, shifted up by
the rows of it above.

### ScrollList

```csharp
var rows = runtime.BeginScrollList("inbox", ref scroll, messages.Count);
for (int i = rows.Row; rows.More; i = rows.Row)
    if (rows.Next(ref scroll, HeightOf(messages[i]))) Message(runtime, messages[i]);
int top = scroll.Idx;   // the row at the top this frame, before End moves it
runtime.EndScrollList(ref scroll, rows);
runtime.Scrollbar("inbox-bar", ref scroll, messages.Count, top, top + rows.Built);
```

The loop goes up while the top shown is above row `Idx`, past the rows scrolled
past, then down through the view; `Next` says which rows to build. The wheel
moves the target exactly through the heights of the rows it passes, however
tall, so a row taller than the view scrolls through like any other; the loop
asks for the heights of the rows the top shown passes as it eases there. To
step through rows of their own heights, map them onto whole rows of a
RowScrollList.

Lay the list and the bar out side by side: begin a row, build the list and then
the bar in it, and end the row.

## Claims and reports

- The view claims `InteractionKinds.Scroll | Clip`: the wheel is reported to it,
  and the list spends it in End, after the rows, so a row that scrolls takes the
  wheel first.
- The bar's lane claims `InteractionKinds.LeftPress`, and is hot over the thumb
  too. A press on the lane, or a drag from the lane or the thumb, sets
  `pt.Idx` to the row that puts the thumb's centre under the pointer, at once;
  a press on the thumb alone moves nothing.
- The bar counts rows, not heights: placed by the rows shown, a row of any
  height is one row of the bar.

## State the caller owns

`ScrollPt`. `pt.Target(idx)` shows row `idx` at the top at once, in either list.
To glide there with a RowScrollList, add the pixels between to `Off`.
RowScrollList by rows holds the wheel not yet a row in `Px`, unseen.

## Theme tokens

- The bar's lane: `Surface`, then `SurfaceRaised` under the pointer, eased.
- The thumb: `SurfaceRaised`, `SurfaceRaisedHover` under the pointer, and
  `SurfaceRaisedActive` while held.

## Replaces

The list of rows of one pitch as it was, `BeginScrollList(id, ref pt, pitch,
count)`, `EndScrollList(ref pt)` and `Scrollbar(id, pt, count, viewRows)`, is
removed: it is RowScrollList, which also scrolls by pixels.
