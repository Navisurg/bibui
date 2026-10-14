# Checkbox

An 18 px square with a check when on, or a dash when indeterminate, and an
optional label beside it. The whole row takes the press, so the label is as much
a target as the square.

| Checked | Disabled | Indeterminate |
|---|---|---|
| ![checkbox-default](images/checkbox-default.png) | ![checkbox-disabled](images/checkbox-disabled.png) | ![checkbox-indeterminate](images/checkbox-indeterminate.png) |

Fixtures: `checkbox-default`, `checkbox-disabled` and `checkbox-indeterminate`.

## Build

```cs
public static BibuiWidget Build(
    BibuiRuntime runtime,
    string id,
    bool on,
    ReadOnlySpan<char> label = default,
    bool disabled = false,
    bool indeterminate = false)
```

| Parameter | Meaning |
|---|---|
| `id` | The row's retained box. |
| `on` | Draws the check. The checkbox never writes it. |
| `label` | Text to the right of the square. |
| `disabled` | A disabled edge and label. Swallows the press and reports nothing. |
| `indeterminate` | Draws a dash instead of the check, and takes precedence over `on`. Use it for a "select all" over a partly selected list. |

## Claims and reports

- The row claims `InteractionKinds.LeftPress`, or `Disabled`.
- It returns a `BibuiWidget` whose `Node` is the row. Flip your value on
  `Clicked`.

## State the caller owns

`on`, and whether the box is `indeterminate`.

## Theme tokens

- Square: `Accent` fill and edge when on or indeterminate. Otherwise a `Surface`
  fill and a `Border` edge. The edge is `TextDisabled` when disabled.
- Check and dash: `AccentForeground`.
- Label: `TextColor.Primary`, or `TextColor.Disabled`.
- The edge is `runtime.Theme.Borders.Width` wide.

## Example

```csharp
using Bibui;
using Bibui.Runtime;

sealed class NotificationSettings
{
    const string All = "All notifications";
    const string Email = "Email";
    const string Push = "Push";

    bool _email = true, _push;   // the caller's state

    public UiNode Build(BibuiRuntime runtime)
    {
        Box column = runtime.Box();
        column.ChildLayoutAxis = LayoutAxis.Y;
        column.SizeX = column.SizeY = SemanticSize.ChildrenSum();
        using (runtime.PushParent(column))
        {
            // "All" is indeterminate while only some are on, and turns them all on or off.
            bool some = _email != _push;
            if (Checkbox.Build(runtime, "notify-all", _email && _push, All, indeterminate: some).Clicked)
                _email = _push = !(_email && _push);
            runtime.Gap(12f);
            if (Checkbox.Build(runtime, "notify-email", _email, Email).Clicked) _email = !_email;
            runtime.Gap(12f);
            if (Checkbox.Build(runtime, "notify-push", _push, Push).Clicked) _push = !_push;
        }
        return column;
    }
}
```
