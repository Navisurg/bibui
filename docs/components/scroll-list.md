# RowScrollList and ScrollList

Lists that build only the rows in sight, so a hundred thousand rows cost what a
screenful does, and a bar for either. Both ease, and keep a `ScrollPt` the
caller owns.

- **RowScrollList**: rows of one height, `pitch` apart, by rows or by pixels.
  Everything follows from the pitch.
- **ScrollList**: rows of their own heights, by pixels. It holds no heights:
  the loop hands each in as the list reaches the row, so a row's height must be
  known before it is built, or, for a row measured by building it, no more than
  it is (a lower bound, such as one line of its text) until then.

A list scrolls from the first row on the top to the last row on the bottom. The
end is found from the view's height only once the view has been laid out: a
list not laid out yet (built again after being hidden) keeps the point it was
given, rather than one clamped to a height it has not been laid out at.

## Build

```cs
public static (int First, int Last) BeginRowScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, float pitch, int count, ScrollBy by = ScrollBy.Rows)
public static void EndRowScrollList(this BibuiRuntime runtime, ref ScrollPt pt, float pitch, int count, ScrollBy by = ScrollBy.Rows)

public static ScrollRows BeginScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count)
public bool ScrollRows.Next(ref ScrollPt pt, float height)
public static void EndScrollList(this BibuiRuntime runtime, ref ScrollPt pt, in ScrollRows rows)

public static void Scrollbar(this BibuiRuntime runtime, string id, ref ScrollPt pt, int count, int first, int last)
```

```csharp
var (first, last) = runtime.BeginRowScrollList("log", ref log, 24f, lines.Count, ScrollBy.Pixels);
for (int i = first; i < last; i++) Line(runtime, lines[i]);
runtime.EndRowScrollList(ref log, 24f, lines.Count, ScrollBy.Pixels);
runtime.Scrollbar("log-bar", ref log, lines.Count, first, last);

var rows = runtime.BeginScrollList("inbox", ref inbox, messages.Count);
for (int i = rows.Row; rows.More; i = rows.Row)
    if (rows.Next(ref inbox, HeightOf(messages[i]))) Message(runtime, messages[i]);
int top = inbox.Idx;   // the row at the top this frame, before End moves it
runtime.EndScrollList(ref inbox, rows);
runtime.Scrollbar("inbox-bar", ref inbox, messages.Count, top, top + rows.Built);
```

Build each row as tall as the list was told, or, while it is measured, at
least that tall: a height handed in above the row's own stops the list short of
the view's bottom. Lay a list and its bar out
side by side in a row. Something several rows tall in a RowScrollList is the
caller's to map onto whole rows.

## Claims and reports

- The view claims `InteractionKinds.Scroll | Clip`; the list spends the wheel in
  End, after the rows, so a row that scrolls takes it first.
- The bar's lane (`ScrollList.BarLane`, 12 px) claims `LeftPress`. A press on it,
  or a drag, shows the row that puts the thumb's centre under the pointer; a
  press on the thumb alone moves nothing. It counts rows, not heights.

## State the caller owns

`ScrollPt`: the target, `Px` pixels below the top of row `Idx`, and `Off`, the
pixels what is shown still is from it. `pt.Target(idx)` shows a row at once.

## Theme tokens

- The bar's lane: `Surface`, `SurfaceRaised` under the pointer.
- The thumb: `SurfaceRaised`, `SurfaceRaisedHover` under the pointer,
  `SurfaceRaisedActive` while held.
