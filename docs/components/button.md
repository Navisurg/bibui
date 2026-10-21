# Button

A labelled button. Solid by default: a raised fill and an edge, with the theme's
hover and press treatments. A ghost button has no fill or edge at rest and only
lights up under the pointer. It is as large as its label plus padding.

| Solid | Ghost | Disabled |
|---|---|---|
| ![button-default](images/button-default.png) | ![button-ghost](images/button-ghost.png) | ![button-disabled](images/button-disabled.png) |

Fixtures: `button-default`, `button-ghost` and `button-disabled`. The ghost
button draws only its label at rest. `tooltip-above`, `popover-open` and
`menu-dropdown` show a solid button as the trigger of an overlay.

## Build

```cs
public static BibuiWidget Button(
    this BibuiRuntime runtime,
    string id,
    ReadOnlySpan<char> label,
    bool ghost = false,
    bool disabled = false,
    ComponentSize size = ComponentSize.Md)
```

| Parameter | Meaning |
|---|---|
| `id` | The button's retained box. |
| `label` | What it reads. |
| `ghost` | No resting fill or edge, only a hover fill. For toolbars and quiet actions. |
| `disabled` | The label is drawn in the disabled colour. The button swallows the press and reports nothing. |
| `size` | The label's type size. The padding stays the same, so the button grows with its text. |

## Claims and reports

- The box claims `InteractionKinds.LeftPress`, or `InteractionKinds.Disabled`
  when disabled.
- It returns a `BibuiWidget`: act on `Clicked`. `Pressed` and `Hovering` are
  there if you need them.

## State the caller owns

None. A button has no value. What a click means is up to you.

## Theme tokens

- Solid: the fill is `SurfaceRaised`, then `SurfaceRaisedHover` under the pointer
  and `SurfaceRaisedActive` while held. The edge is `Border`, then `BorderHover`,
  then `Accent` while held. These come from `DrawFlags.Border | Background |
  HotEffects | ActiveEffects`.
- Ghost: the hover fill is `SurfaceRaised`.
- The label is `TextColor.Primary`, or `TextColor.Disabled`.
- The edge is `runtime.Theme.Borders.Width` wide. The vertical padding is scaled
  by `runtime.Theme.Density`.

## Example

```csharp
using Bibui.Core;
using Bibui.Core.Runtime;

sealed class Counter
{
    const string Minus = "−";
    const string Plus = "+";
    const string Reset = "Reset";

    int _count;   // the caller's state

    public UiNode Build(BibuiRuntime runtime)
    {
        Box row = runtime.Box();
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = row.SizeY = SemanticSize.ChildrenSum();
        using (runtime.PushParent(row))
        {
            if (runtime.Button("counter-minus", Minus).Clicked) _count--;
            runtime.Gap(8f);
            if (runtime.Button("counter-plus", Plus).Clicked) _count++;
            runtime.Gap(8f);
            // Disabled at zero: still there, and a press on it reaches nothing beneath.
            if (runtime.Button("counter-reset", Reset, ghost: true, disabled: _count == 0,
                    size: ComponentSize.Sm).Clicked)
                _count = 0;
        }
        return row;
    }
}
```
