# Toggle and ToggleGroup

A **Toggle** is a button that stays pressed. It is outlined when off and filled
with the raised surface when on, with an optional icon before its label.

A **ToggleGroup** is a segmented control: items side by side in one outline,
with dividers between them. The active item is raised.

| Toggle off | Toggle on | Toggle disabled |
|---|---|---|
| ![toggle-default](images/toggle-default.png) | ![toggle-pressed](images/toggle-pressed.png) | ![toggle-disabled](images/toggle-disabled.png) |

![toggle-group-default](images/toggle-group-default.png)

Fixtures: `toggle-default`, `toggle-pressed`, `toggle-disabled` and
`toggle-group-default`.

## runtime.Toggle

```cs
public static BibuiWidget Toggle(
    this BibuiRuntime runtime,
    string id,
    bool on,
    ReadOnlySpan<char> label = default,
    string? icon = null,
    bool disabled = false)
```

| Parameter | Meaning |
|---|---|
| `id` | The toggle's retained box. |
| `on` | Draws it pressed. The toggle never writes it. |
| `label` | Text after the icon. Optional, so a toggle can be icon-only. |
| `icon` | The name of an icon drawn before the label, such as `"bold"`. |
| `disabled` | Draws the label and icon in the disabled colour and drops the hover fill. Swallows the press. |

The box claims `InteractionKinds.LeftPress`, or `Disabled`, and returns a
`BibuiWidget`. Flip your value on `Clicked`.

## runtime.ToggleGroup

```cs
public static ToggleGroupWidget ToggleGroup(
    this BibuiRuntime runtime,
    string id,
    IReadOnlyList<ToggleOption> items,
    string? value)

public readonly record struct ToggleOption(string Label, string Value);
public readonly record struct ToggleGroupWidget(Box Node, string? Picked);
```

| Parameter | Meaning |
|---|---|
| `id` | Names the items: `{id}-{value}`. |
| `items` | The items in order. |
| `value` | The active item's value, or null for none. |

Each item claims `InteractionKinds.LeftPress`. `Picked` is the value of the item
clicked this frame, which may be the active one. A group has no disabled state.

## State the caller owns

For Toggle, `on`. For ToggleGroup, the active `value`.

## Theme tokens

- Toggle: a `Border` edge when off, a `SurfaceRaised` fill when on. The hover
  fill is `SurfaceRaised` when off and `SurfaceRaisedHover` when on. The label
  and icon are `TextColor.Primary`, or `TextColor.Disabled`.
- ToggleGroup: a `Border` outline and dividers. The active item is
  `SurfaceRaised` with a `TextColor.Primary` label, and the rest are
  `TextColor.Secondary`.
- Both: lines are `runtime.Theme.Borders.Width` wide, and the 36 px height is
  scaled by `runtime.Theme.Density`.

## Example

```csharp
using Bibui.Core;
using Bibui.Core.Runtime;

sealed class FormatBar
{
    const string Bold = "Bold";
    static readonly ToggleOption[] Alignments =
    [
        new("Left", "left"),
        new("Centre", "centre"),
        new("Right", "right"),
    ];

    bool _bold;                 // the caller's state
    string _align = "left";

    public UiNode Build(BibuiRuntime runtime)
    {
        var row = runtime.BeginParent(runtime.Row());
        if (runtime.Toggle("format-bold", _bold, Bold, icon: "bold").Clicked) _bold = !_bold;
        runtime.Gap(12f);
        var align = runtime.ToggleGroup("format-align", Alignments, _align);
        if (align.Picked is { } picked) _align = picked;
        runtime.EndParent(row);
        return row;
    }
}
```
