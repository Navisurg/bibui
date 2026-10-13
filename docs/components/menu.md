# Menu

A raised list of actions. It serves as a dropdown menu, opened under a trigger,
and as a context menu, opened where the right button went down. You build rows
between `Begin` and `End` from `Item`, `Separator` and `Label`, or from rows of
your own.

| Dropdown | Context |
|---|---|
| ![menu-dropdown](images/menu-dropdown.png) | ![menu-context](images/menu-context.png) |

Fixtures: `menu-dropdown` and `menu-context`.

## Begin, rows, End

```cs
public static MenuScope Begin(BibuiRuntime runtime, string id, float x, float y, float width = 220f)
public static MenuScope BeginBelow(BibuiRuntime runtime, string id, Box anchor, float width = 220f, float gap = 4f)

public static BibuiWidget Item(
    BibuiRuntime runtime,
    string id,
    BibuiStr label,
    string? icon = null,
    BibuiStr? shortcut = null,
    bool disabled = false)
public static Box Separator(BibuiRuntime runtime)
public static Box Label(BibuiRuntime runtime, BibuiStr text)

// MenuScope
public BibuiWidget End();
```

| Parameter | Meaning |
|---|---|
| `id` (Begin) | The menu's dropdown root. |
| `x`, `y` | Where its top left goes, in window coordinates: for a context menu, the pointer's position at the right press. |
| `anchor` | `BeginBelow` opens under this box where it was drawn last frame, with the left edges aligned. |
| `width` | The menu's width. It is as tall as its rows. |
| `id` (Item) | The row's retained box. |
| `icon` | The name of an icon before the label. |
| `shortcut` | Quiet text at the row's far end, such as `⌘S`. |
| `disabled` | A quiet row with no hover. It swallows the press. |

## Claims and reports

- Each `Item` claims `InteractionKinds.LeftPress`, or `Disabled`, and returns its
  own `BibuiWidget`: act on `Clicked`.
- The menu is one of the runtime's dropdowns. It is drawn over everything, and
  nothing beneath it gets a press where it was drawn. `End()` claims
  `InteractionKinds.Press` for the list after its rows, so a press on its padding
  or on a rule stops there. `End()` returns the list's `BibuiWidget`.
- `Separator` and `Label` take nothing.

## State the caller owns

Whether the menu is open, and for a context menu, where it opens. An item clicked
is usually your cue to act and close the menu. A press outside the menu does not
close it: watch for one yourself if you want that.

## Theme tokens

- List: a `SurfaceRaised` fill, a `Border` edge `runtime.Theme.Borders.Width`
  wide, and the `runtime.Theme.Elevation` overlay shadow.
- Item: `TextPrimary` label and icon (`TextDisabled` when disabled), a
  `SurfaceRaisedHover` hover fill, and a `TextDisabled` shortcut. Vertical
  padding is scaled by `runtime.Theme.Density`.
- Separator: `Border`. Label: `TextSecondary`, bold, at the small size.

## Example

```csharp
using Bibui;
using Bibui.Runtime;

sealed class AccountMenu
{
    static readonly BibuiStr Trigger = BibuiStr.FromStrLit("My Account"u8);
    static readonly BibuiStr Account = BibuiStr.FromStrLit("Account"u8);
    static readonly BibuiStr Profile = BibuiStr.FromStrLit("Profile"u8);
    static readonly BibuiStr Settings = BibuiStr.FromStrLit("Settings"u8);
    static readonly BibuiStr SettingsKey = BibuiStr.FromStrLit("⌘S"u8);
    static readonly BibuiStr LogOut = BibuiStr.FromStrLit("Log out"u8);

    bool _open;   // the caller's state

    public UiNode Build(BibuiRuntime runtime)
    {
        var trigger = Button.Build(runtime, "account-trigger", Trigger);
        if (trigger.Clicked) _open = !_open;

        if (_open)
        {
            var menu = Menu.BeginBelow(runtime, "account-menu", (Box)trigger.Node);
            Menu.Label(runtime, Account);
            bool chosen = Menu.Item(runtime, "account-profile", Profile, "user").Clicked;
            chosen |= Menu.Item(runtime, "account-settings", Settings, "settings", SettingsKey).Clicked;
            Menu.Separator(runtime);
            chosen |= Menu.Item(runtime, "account-logout", LogOut, "log-out").Clicked;
            menu.End();
            if (chosen) _open = false;
        }
        return trigger;
    }
}
```

For a context menu, ask about the area that opens it, and open the menu at the
pointer on its right press:

```csharp
using Bibui;
using Bibui.Runtime;

sealed class FileContextMenu
{
    static readonly BibuiStr Rename = BibuiStr.FromStrLit("Rename"u8);
    static readonly BibuiStr Delete = BibuiStr.FromStrLit("Delete"u8);

    (float X, float Y)? _openAt;   // the caller's state

    public UiNode Build(BibuiRuntime runtime)
    {
        if (_openAt is { } at)
        {
            var menu = Menu.Begin(runtime, "file-menu", at.X, at.Y);
            bool chosen = Menu.Item(runtime, "file-rename", Rename, "pencil").Clicked;
            chosen |= Menu.Item(runtime, "file-delete", Delete, "trash").Clicked;
            menu.End();
            if (chosen) _openAt = null;
        }

        Box area = runtime.BoxFromId("file-area", InteractionKinds.LeftPress | InteractionKinds.RightPress);
        var press = runtime.InteractionsFor(area);
        if (press.RightPressed) _openAt = (press.X, press.Y);
        else if (press.Pressed) _openAt = null;   // a left press beside the menu closes it
        area.SizeX = SemanticSize.Pixels(320f);
        area.SizeY = SemanticSize.Pixels(200f);
        return area;
    }
}
```
