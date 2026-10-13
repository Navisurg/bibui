# Select

A trigger showing the current choice and a chevron. When open, it shows a
raised list under the trigger. You build the list's rows between `Begin` and
`End`, either with `Select.Item` or with rows of your own.

| Closed | With a value | Disabled |
|---|---|---|
| ![select-default](images/select-default.png) | ![select-with-value](images/select-with-value.png) | ![select-disabled](images/select-disabled.png) |

| Open | A disabled option | Open with a selected row |
|---|---|---|
| ![select-open](images/select-open.png) | ![select-disabled-option](images/select-disabled-option.png) | ![select-colormap](images/select-colormap.png) |

Fixtures: `select-default`, `select-with-value`, `select-disabled`,
`select-open`, `select-disabled-option` and `select-colormap`.

## Begin, Item, End

```cs
public static SelectScope Begin(
    BibuiRuntime runtime,
    string id,
    BibuiStr text,
    bool open,
    float width = 220f,
    bool disabled = false)

public static BibuiWidget Item(
    BibuiRuntime runtime,
    string id,
    BibuiStr label,
    bool selected = false,
    bool disabled = false)

// SelectScope
public bool Open { get; }
public SelectResult End();

public readonly record struct SelectResult(Box Node, bool Toggled);
```

| Parameter | Meaning |
|---|---|
| `id` | The trigger's retained box. The list is the dropdown `{id}-list`. |
| `text` | What the trigger reads: the chosen option's label, or a placeholder. |
| `open` | Whether to open the list this frame. |
| `width` | The trigger's width. The list is as wide as the trigger was drawn. |
| `disabled` | A raised, quiet trigger that swallows the press. It never opens. |
| `Item.selected` | Draws that row raised. |
| `Item.disabled` | A quiet row that swallows the press. |

Build rows only while `scope.Open` is true. Then call `End()` exactly once, even
when the select is closed.

## Claims and reports

- The trigger claims `InteractionKinds.LeftPress`, or `Disabled`. Each `Item`
  row claims `LeftPress`, or `Disabled`, and returns its own `BibuiWidget`.
- The list is one of the runtime's dropdowns. It is drawn over everything, and
  nothing beneath it gets a press where it was drawn. After its rows, it claims
  `InteractionKinds.Press`, so a press on its padding stops there.
- `End()` returns a `SelectResult`. `Toggled` means the trigger was clicked this
  frame. `Node` is the trigger, to place.

## State the caller owns

Whether the list is open, and which value is chosen. A select never closes
itself: close it when the trigger toggles it or an item is clicked. A press
outside the open list does not close it yet.

## Theme tokens

- Trigger: a `Surface` fill (`SurfaceRaised` when disabled) and a `Border` edge.
  The chevron is `TextSecondary`. The text is `TextColor.Primary`, or
  `TextColor.Disabled`.
- List: a `SurfaceRaised` fill, a `Border` edge and the
  `runtime.Theme.Elevation` overlay shadow.
- Rows: `SurfaceRaisedHover` when selected and under the pointer.
  `TextColor.Primary`, or `TextColor.Disabled`.
- Edges are `runtime.Theme.Borders.Width` wide. Vertical padding is scaled by
  `runtime.Theme.Density`.

## Example

```csharp
using Bibui;
using Bibui.Runtime;

sealed class FruitPicker
{
    static readonly BibuiStr Placeholder = BibuiStr.FromStrLit("Select a fruit…"u8);
    static readonly (BibuiStr Label, string Value)[] Fruits =
    [
        (BibuiStr.FromStrLit("Apple"u8), "apple"),
        (BibuiStr.FromStrLit("Banana"u8), "banana"),
        (BibuiStr.FromStrLit("Cherry"u8), "cherry"),
    ];

    bool _open;            // the caller's state
    string? _fruit;

    public UiNode Build(BibuiRuntime runtime)
    {
        BibuiStr shown = Placeholder;
        foreach (var (label, value) in Fruits)
            if (value == _fruit) shown = label;

        var select = Select.Begin(runtime, "fruit", shown, _open);
        if (select.Open)
            foreach (var (label, value) in Fruits)
                if (Select.Item(runtime, "fruit-" + value, label, selected: value == _fruit).Clicked)
                {
                    _fruit = value;
                    _open = false;
                }
        var result = select.End();
        if (result.Toggled) _open = !_open;
        return result.Node;
    }
}
```
