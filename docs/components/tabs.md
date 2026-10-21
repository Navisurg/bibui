# Tabs

A strip of tab headers on a raised track, with the active tab cut out of it in
the background colour. The panel underneath is a separate call, so you decide
what goes in it.

| Account | Password |
|---|---|
| ![tabs-account](images/tabs-account.png) | ![tabs-password](images/tabs-password.png) |

Fixtures: `tabs-account` and `tabs-password`.

## Build and Panel

```cs
public static TabsWidget Tabs(
    this BibuiRuntime runtime,
    string id,
    IReadOnlyList<TabOption> items,
    string? value)

public static Box Panel(BibuiRuntime runtime);

public const float Height = 36f;     // the strip, at the default density
public const float PanelGap = 8f;    // the space to put between strip and panel

public readonly record struct TabOption(string Label, string Value);
public readonly record struct TabsWidget(Box Node, string? Picked);
```

| Parameter | Meaning |
|---|---|
| `id` | Names the tabs: `{id}-{value}`. |
| `items` | The tabs in order. |
| `value` | The active tab's value. |

The strip is as wide as its parent. `Panel` returns a bordered, padded column as
wide as its parent and as tall as its content. Begin it as the parent and
build the active tab's content into it.

## Claims and reports

- Each tab claims `InteractionKinds.LeftPress`.
- `Build` returns a `TabsWidget`. `Picked` is the value of the tab clicked this
  frame, or null.

## State the caller owns

The active tab's `value`, and the content of each tab.

## Theme tokens

- Strip: a `SurfaceRaised` fill. Its 36 px height is scaled by
  `runtime.Theme.Density`.
- Active tab: a `Background` fill with a `TextColor.Primary` label. The other
  tabs have `TextColor.Secondary` labels and a `SurfaceRaisedHover` hover fill.
- Panel: a `Surface` fill and a `Border` edge, `runtime.Theme.Borders.Width` wide.

## Example

```csharp
using Bibui.Core;
using Bibui.Core.Runtime;

sealed class SettingsTabs
{
    static readonly TabOption[] Items =
    [
        new("Account", "account"),
        new("Password", "password"),
    ];
    const string AccountText = "Manage your account settings.";
    const string PasswordText = "Change your password.";

    string _tab = "account";   // the caller's state

    public UiNode Build(BibuiRuntime runtime)
    {
        var column = runtime.BeginParent(runtime.Column().Sized(Size.Px(360f)));
        var tabs = runtime.Tabs("settings-tabs", Items, _tab);
        if (tabs.Picked is { } picked) _tab = picked;
        runtime.Gap(Tabs.PanelGap);
        var panel = runtime.BeginParent(Tabs.Panel(runtime));
        runtime.Text(_tab == "account" ? AccountText : PasswordText, textColor: TextColor.Secondary);
        runtime.EndParent(panel);
        runtime.EndParent(column);
        return column;
    }
}
```
