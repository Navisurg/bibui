# RowScrollList and ScrollList

Two vertical lists that build only the rows in sight, so a list of a hundred
thousand rows costs what its screenful does, and a bar for either. Both keep a
`ScrollPt` the caller owns, and both scroll by rows or by pixels.

- **RowScrollList**: rows that all have one height, `pitch` apart. Everything
  follows from the pitch, so nothing is walked or estimated, and it eases.
- **ScrollList**: rows of their own heights. It holds no heights: one loop hands
  them in as it reaches each row, so a height must be known before the row is
  built (from what the row holds: a line height per line, and padding). It does
  not ease.

The gallery's ScrollList page shows both, with a switch to scroll by pixels.

## Build

```cs
// Rows of one pitch
public static (int First, int Last) BeginRowScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, float pitch, int count, ScrollBy by = ScrollBy.Rows)
public static void EndRowScrollList(this BibuiRuntime runtime, ref ScrollPt pt, float pitch, int count, ScrollBy by = ScrollBy.Rows)

// Rows of their own heights
public static ScrollRows BeginScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count, ScrollBy by, float rowStep)
public bool ScrollRows.Next(ref ScrollPt pt, float height)
public static void EndScrollList(this BibuiRuntime runtime, ref ScrollPt pt, in ScrollRows rows)

// The bar, beside either
public static void Scrollbar(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count, int first, int last)
public const float ScrollList.BarLane = 12f;
```

| Parameter | Meaning |
|---|---|
| `id` | The view's retained box. The bar's are `{id}` and `{id}-thumb`. |
| `pt` | Where the list is: `Idx`, the row at the top, `Px`, how far past its top the view is, and `Off` (below). Kept by the caller between frames. |
| `pitch` | RowScrollList: the distance from one row's top to the next. Build each row exactly this tall. |
| `count` | How many rows there are. |
| `by` | `ScrollBy.Rows`: the wheel moves whole rows and the view rests on a row's top, the last row clipped at the end. `ScrollBy.Pixels`: the wheel moves pixels and the last row rests on the bottom. |
| `rowStep` | ScrollList by rows: the wheel's pixels that move the list a row. It depends on the rows and the device, so there is no default; a notch of a mouse wheel is 32. |
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
var rows = runtime.BeginScrollList("inbox", ref scroll, messages.Count, ScrollBy.Pixels, rowStep: 32f);
for (int i = rows.Row; rows.More; i = rows.Row)
    if (rows.Next(ref scroll, HeightOf(messages[i]))) Message(runtime, messages[i]);
int top = scroll.Idx;   // the row at the top this frame, before End moves it
runtime.EndScrollList(ref scroll, rows);
runtime.Scrollbar("inbox-bar", ref scroll, messages.Count, top, top + rows.Built);
```

The loop goes up while the top is above row `Idx`, past the rows scrolled past,
then down through the view; `Next` says which rows to build. By pixels the wheel
moves exactly through the heights of the rows it passes, however tall, so a row
taller than the view scrolls through like any other; a fast fling asks for the
height of every row it passes, in one frame. By rows no height is needed to
scroll, and a row of any height is one step.

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
- The bar counts rows, not heights: a row of any height is one row of the bar.

## State the caller owns

`ScrollPt`. Set `pt.Idx` (and `Px` and `Off` to 0) to show a row at once, or call
`pt.Target(idx)`: a RowScrollList eases there, a ScrollList shows it at once.
`Off` is the rows a RowScrollList is still easing; a ScrollList by rows holds
the wheel not yet a whole step in it.

## Theme tokens

- The bar's lane: `Surface`, then `SurfaceRaised` under the pointer, eased.
- The thumb: `SurfaceRaised`, `SurfaceRaisedHover` under the pointer, and
  `SurfaceRaisedActive` while held.

## Replaces

The list of rows of one pitch as it was, `BeginScrollList(id, ref pt, pitch,
count)`, `EndScrollList(ref pt)` and `Scrollbar(id, pt, count, viewRows)`, is
removed: it is RowScrollList, which also scrolls by pixels.
