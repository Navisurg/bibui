# Popover

A raised panel centred under a box you hold, with whatever you build between
`Begin` and `End` inside it. It has no trigger of its own: build one, a button
or anything else you can ask about.

![popover-open](images/popover-open.png)

Fixture: `popover-open`.

## Begin and End

```cs
public static PopoverScope Begin(BibuiRuntime runtime, string id, Box anchor, float width = 280f)

// PopoverScope
public BibuiWidget End();
```

| Parameter | Meaning |
|---|---|
| `id` | The popover's dropdown root. |
| `anchor` | The box it opens under, 8 px below and centred on it, where it was drawn last frame. |
| `width` | The panel's width. It is as tall as its content. |

## Claims and reports

- The popover is one of the runtime's dropdowns. It is drawn over everything,
  and nothing beneath it gets a press where it was drawn.
- `End()` claims `InteractionKinds.Press` for the panel after its content, and
  returns its `BibuiWidget`. Use `Hovering` to keep a hover card open while the
  pointer is on it.

## State the caller owns

Whether it is open. Build it only in a frame it is open.

## Theme tokens

- Panel: a `SurfaceRaised` fill, a `Border` edge `runtime.Theme.Borders.Width`
  wide, and the `runtime.Theme.Elevation` overlay shadow.

## Example

```csharp
using Bibui;
using Bibui.Runtime;

sealed class SharePopover
{
    static readonly BibuiStr Share = BibuiStr.FromStrLit("Share"u8);
    static readonly byte[] Body = "Anyone with the link can view."u8.ToArray();

    bool _open;   // the caller's state

    public UiNode Build(BibuiRuntime runtime)
    {
        var trigger = Button.Build(runtime, "share", Share);
        if (trigger.Clicked) _open = !_open;

        if (_open)
        {
            var popover = Popover.Begin(runtime, "share-popover", (Box)trigger.Node);
            runtime.Text(Body, ComponentSize.Sm, TextColor.Secondary);
            popover.End();
        }
        return trigger;
    }
}
```
