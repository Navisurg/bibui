# ScrollList

A vertical list of rows that all have the same height, of which only the rows in
sight are built. A list of a hundred thousand rows costs what its screenful does.

It scrolls by rows, not by pixels. The rows are `pitch` apart, top to top, so
which rows are in sight follows from the scroll position alone: the whole part of
the top row picks them, and the view is offset by the fraction. The view holds
only those rows. The bar beside it is sized from the row count, not from the
view's content.

The gallery's ScrollList page shows 100,000 rows.

## Build

```cs
public static ScrollListRows BeginScrollList(this BibuiRuntime runtime, string id, ref ScrollPt pt, float pitch, int count)
public static void EndScrollList(this BibuiRuntime runtime, ref ScrollPt pt, ScrollListRows rows)

// The bar on its own, for a list of your own built the same way:
public static ScrollPt Scrollbar(this BibuiRuntime runtime, string id, ScrollPt pt, int count, float viewRows)
```

| Parameter | Meaning |
|---|---|
| `id` | The view's retained box. The parts are `{id}-area` (the view and its bar side by side) and `{id}-bar`, whose parts are `{id}-bar-before`, `{id}-bar-thumb` and `{id}-bar-after`. |
| `pt` | Where the list is scrolled to, kept by the caller. See below. |
| `pitch` | The distance from the top of one row to the top of the next: a row and anything between rows. Every row must take exactly this much. |
| `count` | How many rows there are. |

`BeginScrollList` builds the area under the parent in scope and pushes the view
as the parent for the rows. Build rows `rows.Min` up to, not including,
`rows.Max`, each `pitch` tall, then call `EndScrollList`. Size the list by
`rows.Area`.

## State the caller owns

A `ScrollPt`, kept between frames beside the rows it scrolls:

| Field | Whose | Meaning |
|---|---|---|
| `Idx` | the caller's to move | The target: the row at the top once the list settles. |
| `Px` | the caller's to move | The target: how far into that row, in pixels. |
| `Off` | the list's | Rows still to travel. The top row shown is `Idx + Px / pitch + Off`. |
| `ViewRows` | the list's | How many whole rows the view held, as of last frame. |
| `Grab` | the bar's | The top row shown when its thumb was pressed. |
| `Stepped` | the list's | The frame `Off` was last eased in. |

Move the target with `pt.Target(idx, px, pitch)`: it keeps what is shown where it
is, and the list eases to the new target. To jump instead, set `pt.Off = 0`
after it. For example, to bring a row the keyboard moved to into sight:

```csharp
if (row < scroll.Idx || (row == scroll.Idx && scroll.Px > 0f))
    scroll.Target(row, 0f, pitch);                           // above: it becomes the top row
else if (row >= scroll.Idx + scroll.ViewRows)
    scroll.Target(row - scroll.ViewRows + 1, 0f, pitch);     // below: it becomes the bottom row
```

## Behaviour

- **The wheel** moves the target by its pixels, not by a row, and the list eases
  there at `runtime.Rates.Follow`.
- **The thumb**: dragging it moves the top row by as many rows as the thumb
  travels from where it was pressed, at once and on a whole row. Pressing it
  moves nothing.
- **The track**: pressing the space before or after the thumb pages by the rows
  in view, eased.
- **The ends**: the target stays between the first row at the top and the last
  row flush with the bottom. A list that shrinks shows its new end at once.
- **Easing** happens once a frame, however often the build runs, and stops within
  half a pixel. Under reduced motion or `MotionEasing.None` the list lands at
  once.
- **Allocation**: a frame that scrolls allocates nothing beyond the rows the
  caller builds.

## Claims and reports

- The view claims `InteractionKinds.Scroll | Clip`. It reports the wheel without
  moving on it, and `EndScrollList` asks after the rows, so a row that takes the
  wheel keeps it.
- The bar's thumb and spaces claim `InteractionKinds.LeftPress`, and ask before
  the rows.

## Theme tokens

- Thumb: `SurfaceRaised`, then `SurfaceRaisedHover` under the pointer and
  `SurfaceRaisedActive` while held, as [Scrollbar](scrollbar.md).
- Motion: `runtime.Rates.Follow`, from `Theme.Motion.Follow`.

## Example

```csharp
using Bibui;
using Bibui.Runtime;

sealed class LogView(List<string> lines)
{
    const float Pitch = 24f;
    ScrollPt _scroll;   // where the log is scrolled to, kept between frames

    public UiNode Build(BibuiRuntime runtime)
    {
        var rows = runtime.BeginScrollList("log", ref _scroll, Pitch, lines.Count);
        for (int i = rows.Min; i < rows.Max; i++)
        {
            Box row = runtime.Box();
            row.SizeX = SemanticSize.ParentPct(1f);
            row.SizeY = SemanticSize.Pixels(Pitch);   // exactly one pitch
            using (runtime.PushParent(row))
                runtime.Text(lines[i], ComponentSize.Sm);
        }
        runtime.EndScrollList(ref _scroll, rows);

        rows.Area.SizeX = SemanticSize.Pixels(480f);
        rows.Area.SizeY = SemanticSize.Pixels(320f);
        return rows.Area;
    }
}
```
