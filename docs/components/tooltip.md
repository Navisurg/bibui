# Tooltip

One line of small text on a dark pill, centred above a box you hold, or placed
by the mouse. It is built into the runtime's tooltip root, so it is drawn over
everything and takes nothing: the pointer goes through it.

![tooltip-above](images/tooltip-above.png)

Fixture: `tooltip-above`.

## Build

```cs
public static Box Build(BibuiRuntime runtime, BibuiStr text, Box? anchor = null)
```

| Parameter | Meaning |
|---|---|
| `text` | What it reads. |
| `anchor` | The box it sits above, 8 px clear and centred, where it was drawn last frame. With no anchor, it sits by the mouse at `runtime.DefaultToolTipOffset`. |

## Claims and reports

None. It returns the pill's `Box`.

## State the caller owns

When to show it. It decides nothing: build it in a frame you want it, usually
while the trigger reports `Hovering`.

## Theme tokens

- Pill: a `TextPrimary` fill and the `runtime.Theme.Elevation` tooltip shadow.
- Text: `Background`, at the small size. The pill inverts the page's colours.

## Example

```csharp
using Bibui;
using Bibui.Runtime;

sealed class CopyButton
{
    static readonly BibuiStr Copy = BibuiStr.FromStrLit("Copy"u8);
    static readonly BibuiStr Tip = BibuiStr.FromStrLit("Copy to clipboard"u8);

    public UiNode Build(BibuiRuntime runtime)
    {
        var copy = Button.Build(runtime, "copy", Copy, ghost: true);
        if (copy.Hovering) Tooltip.Build(runtime, Tip, (Box)copy.Node);
        if (copy.Clicked) runtime.ClipboardSet("copied text");
        return copy;
    }
}
```
