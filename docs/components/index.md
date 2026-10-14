# Components

bibui's components are plain functions in the `Bibui.Runtime` namespace. Each one
builds a few boxes and texts under the parent in scope and returns what happened
to them this frame. None of them owns state, and none of them has a renderer of
its own: the ordinary painter draws every one.

New to bibui? Start with [Getting started](../getting-started.md). The ideas this
page takes for granted, such as boxes, sizes, asks and retained boxes, are in
[Concepts](../concepts.md). For colours and tokens, see [Theming](../theming.md).

## How every component works

All the components follow the same rules. Once you know them, a signature tells
you most of what a component does.

- **You call it from your build function, every frame.** `Checkbox.Build(runtime, …)`
  links the checkbox under the parent you pushed with `runtime.PushParent`, or
  you place the node it returns yourself.
- **The `id` names its retained boxes.** Any part that takes a press is a
  retained box (`runtime.BoxFromId`), so it keeps its hover and press state from
  frame to frame. Ids must be unique among the boxes built in a frame. Components
  that have rows name them `{id}-{value}`.
- **Your state goes in by value, and what happened comes back.** A checkbox is
  given `on` and reports `Clicked`. It never flips `on` itself; you do that.
  Nothing is a callback.
- **Labels are `ReadOnlySpan<char>`.** Pass a `string`, a slice, or text
  formatted into a stack buffer: the label is copied, so it may change as soon
  as the call returns. A `BibuiStr` is only for text the user edits.
- **Disabled is a claim.** A disabled control still takes the press but reports
  nothing, so the press does not fall through to whatever is beneath it.
- **Ask order is z-order.** A component asks about its boxes when you call it.
  The first box to ask for a point gets it. So build an overlay before the
  content under it, and a container after the controls inside it. Dropdown roots
  (Select, Menu, Popover, Dialog) are the exception: they win over everything,
  whenever they ask.
- **Colours are tokens.** A component never takes a `ThemeTokens`. It names
  tokens such as `UiColor.Accent`, which the painter resolves against the
  mounted theme. Each page lists the tokens its component reads.

### What comes back

Most controls return a `BibuiWidget`:

| Field | Meaning |
|---|---|
| `Node` | The node to place. A `BibuiWidget` converts to `UiNode` on its own. |
| `Clicked` | The press went down and came up on it. Act on this. |
| `Pressed` | The left button went down on it this frame. Use it for a press-down look, or to focus a field. |
| `Hovering` | The pointer is over it now. |
| `Interaction` | Everything else: drag, release, wheel, pointer position, modifiers, press count. |

Components that pick one of several values return their own record with a
`Picked` value, such as `RadioGroupWidget` and `TabsWidget`. Overlays that you
fill between `Begin` and `End` return a scope, and `End()` gives the result.

### The examples

Each example on these pages is a whole C# file that compiles against the
`Bibui` package. The caller's state is in the fields of a small class, and its
`Build(runtime)` method is meant to be called from your build function every
frame. Each screenshot is the reference picture of a fixture in bibui's pixel
verification suite, which checks every component against its pictures on each
change. Each page names the fixture it shows.

## The components

| Component | What it is | Returns |
|---|---|---|
| [Button](button.md) | A labelled button, solid or ghost | `BibuiWidget` |
| [Switch](switch.md) | An on/off switch whose knob travels | `BibuiWidget` |
| [Checkbox](checkbox.md) | A square with a check or a dash, and a label | `BibuiWidget` |
| [RadioGroup](radio-group.md) | A column of options, one chosen | `RadioGroupWidget` |
| [Toggle and ToggleGroup](toggle.md) | A button that stays pressed, and a segmented control | `BibuiWidget`, `ToggleGroupWidget` |
| [Slider](slider.md) | A value on a track | `SliderWidget` |
| [Select](select.md) | A trigger that opens a list | `SelectScope` → `Box` |
| [TextField](text-field.md) | Single-line, ellipsis and multi-line text, and the input look | `BibuiWidget` |
| [Tabs](tabs.md) | A strip of tabs and the panel under it | `TabsWidget`, `Box` |
| [Expander](expander.md) | An outlined frame whose header opens a body | `ExpanderWidget` |
| [Section](section.md) | A caption over a group | `Box` |
| [Dialog](dialog.md) | A modal panel on a scrim: centred, sheet or drawer | `DialogScope` → `DialogWidget` |
| [Menu](menu.md) | A dropdown or context menu of actions | `MenuScope` → `BibuiWidget` |
| [Popover](popover.md) | A raised panel under a trigger | `PopoverScope` → `BibuiWidget` |
| [Tooltip](tooltip.md) | One line of text over a trigger | `Box` |
| [Scrollbar](scrollbar.md) | A bar beside a view that scrolls | `BibuiWidget` |
| [Badge](badge.md) | A pill with a short label | `Box` |
| [Avatar](avatar.md) | Initials on a circle | `Box` |
| [KeyboardShortcut](keyboard-shortcut.md) | Keycaps for a keyboard shortcut | `Box` |
| [Progress](progress.md) | A filled share of a track | `Box` |
| [Separator](separator.md) | A line between things | `Box` |
| [Markdown](markdown.md) | A read-only Markdown viewer | `MarkdownWidget` |
| [Text, Icon and Spinner](text-icon-spinner.md) | The runtime's own leaves | `Text`, `Icon`, `Spinner` |

[Fixtures by component](fixtures.md) lists every fixture of every component and
is generated from the code, so a component without a fixture shows up there. Three
have none yet: Button, Dialog and Scrollbar.
