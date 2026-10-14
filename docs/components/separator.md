# Separator

A line the theme's border width thick, in the border colour. It takes nothing:
it only divides.

| Horizontal | Vertical |
|---|---|
| ![separator-horizontal](images/separator-horizontal.png) | ![separator-vertical](images/separator-vertical.png) |

Fixtures: `separator-horizontal` and `separator-vertical`.

## Build

```cs
public static Box Build(BibuiRuntime runtime, bool vertical = false)

public const float Thickness = 1f;   // under the default theme
```

A horizontal separator is as wide as its parent. A vertical one is as tall as
its parent, so in a row of centred items, the row must give itself a height.
Size the box it returns to make it shorter or longer.

## Claims and reports

None. It returns a keyless `Box`.

## State the caller owns

None.

## Theme tokens

- `Border`, `runtime.Theme.Borders.Width` thick.

## Example

```csharp
using Bibui;
using Bibui.Runtime;

sealed class Toolbar
{
    const string Cut = "Cut";
    const string Copy = "Copy";
    const string Help = "Help";

    public UiNode Build(BibuiRuntime runtime)
    {
        Box row = runtime.Box();
        row.ChildLayoutAxis = LayoutAxis.X;
        row.SizeX = SemanticSize.ChildrenSum();
        row.SizeY = SemanticSize.Pixels(36f);
        using (runtime.PushParent(row))
        {
            Button.Build(runtime, "toolbar-cut", Cut, ghost: true);
            Button.Build(runtime, "toolbar-copy", Copy, ghost: true);
            runtime.Gap(8f);
            Separator.Build(runtime, vertical: true);   // the row's 36 px
            runtime.Gap(8f);
            Button.Build(runtime, "toolbar-help", Help, ghost: true);
        }
        return row;
    }
}
```
