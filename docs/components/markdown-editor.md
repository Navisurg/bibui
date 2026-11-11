# MarkdownEditor

A Markdown document rendered as [Markdown](markdown.md) renders it, with the
block being edited shown as its source, syntax coloured, in a
[StyledTextField](styled-text-field.md) in its place. The
blocks before and after it stay rendered, spaced as the whole document. Edits
go straight into your `BibuiStr`, so there is one buffer and no copy to keep in
sync.

## Build

```cs
public static MarkdownEditorWidget MarkdownEditor(
    this BibuiRuntime runtime,
    string id,
    BibuiStr source,
    MarkdownEditState state,
    float? width = null,
    ComponentSize textSize = ComponentSize.Md)

public sealed class MarkdownEditState
{
    public int Active = -1;
    public TextEditState Edit = new();
}

public readonly record struct MarkdownEditorWidget(Box Node, string? ClickedLink, bool Edited);
```

| Parameter | Meaning |
|---|---|
| `id` | The editor's view. The field, the blocks and their links, task boxes and wide tables are retained boxes named under it. |
| `source` | The Markdown, in a buffer you own. The editor writes its edits into it, and parses it again when it changed. |
| `state` | What the editor keeps between frames. You own it, one per editor. |
| `width` | The view's width. With null, it is as wide as its parent. It is as tall as its parent and scrolls. |
| `textSize` | Body text size, and the field's. Headings use the steps above it. |

## Behaviour

- **Viewing.** With `Active` at -1, every block is rendered exactly as
  `runtime.Markdown(id, source)` renders it.
- **Editing.** A press on a block, not on a link or a task box, makes it active
  and focuses its field. The caret goes to the end of the source line the press
  stands for: the block's lines spread evenly over its rendered height.
- **A block's text** is its lines, then the blank lines and link reference
  definitions after it, up to the line break before the next block. The first
  block also owns anything before it. So every char of the source is in exactly
  one block's text, but the line breaks between them.
- **Edits** go into `source`, which is parsed again. A blank line typed into a
  block splits it, and the caret stays with whichever half it is in.
- **Between blocks.** Up from the field's first line, Down from its last, Left
  at its start and Right at its end move the caret into the block beside it,
  at the matching end. Backspace at the very start and Delete at the very end
  remove the line break between the two, joining them.
- **Leaving.** Escape, or a press anywhere else, ends focus and renders the
  block again. A press on another block edits that one instead.
- **Colouring.** The field's source is coloured as it is typed: its markers
  dimmed, code in the accent colour, strong text bold and emphasis italic (see
  *Theme tokens*). The ranges are found again each frame, left to right, a
  line at a time: quote and list markers, task boxes, a heading's `#`s and
  fences at a line's start, then code spans, links and emphasis. Emphasis is
  found within a line, not across the lines of a paragraph.
- **Task boxes** tick themselves in `source`. **Links** report `ClickedLink`
  and do not start editing.
- **An empty document** has no block to press, so it shows its field.

## Claims and reports

- The view claims `ViewScrollY | ClampY | Clip`. It asks before its content, so
  the wheel scrolls the document wherever the pointer is, the field included.
- The field is a `StyledTextField` named `{id}/field`, as tall as its lines,
  on the input look (`TextField.InputStyle`).
- Links and task boxes claim `InteractionKinds.LeftPress`, as in the viewer;
  so does each rendered block.
- It returns a `MarkdownEditorWidget`. `ClickedLink` is the destination of a
  link clicked this frame. `Edited` says the source changed this frame: typed,
  joined, or a task ticked.

## State the caller owns

The source and a `MarkdownEditState`:

- `Active` is the source offset of the block being edited, or -1 when every
  block is rendered. The editor keeps it at the start of the block that holds
  the caret, so it survives an edit that splits or joins blocks.
- `Edit` is the caret and selection, in offsets into the whole source.

It also holds, internally, what the editor keeps for you: the source as last
parsed and its document, parsed again only when the text changed, and the
field's state and ranges, in arrays that grow to the most needed and never
shrink. A steady frame allocates nothing.

To put the caret somewhere yourself, after a search or an outline jump, set
`Edit` and `Active`. The editor opens the block holding `Active`, focuses its
field and scrolls the caret into view. Set `Active` to -1 to render every
block.

Undo is yours: `Edited` once a frame is the moment to take a snapshot of
`source`.

## Theme tokens

- The rendered blocks: as [Markdown](markdown.md).
- The field: the input look, a `Surface` fill and `Border` edge, the theme's
  focus ring while focused, and body text in `TextPrimary`.
- The field's syntax:
  - `TextSecondary`: heading `#`s, the `*`, `_`, `**` and `__` around
    emphasis, code backticks and fences, list markers and task boxes, `>`, and
    a link's `[` and `](destination)`;
  - `Accent`: the text of code spans and fenced code blocks;
  - `FontWeight.Bold` for strong text, `FontWeight.Italic` for emphasis, and
    `FontWeight.BoldItalic` for the two together.

## Example

```csharp
using Bibui.Core;
using Bibui.Core.Runtime;

sealed class Notes
{
    // The caller's state: the source and where it is being edited.
    readonly BibuiStr _source = BibuiStr.FromStrLit("""
        # Notes

        - [ ] Draft the [release notes](https://example.com/notes)
        - [x] Tag the build
        """);
    readonly MarkdownEditState _state = new();

    public string? OpenedLink { get; private set; }
    public int Saves { get; private set; }

    public UiNode Build(BibuiRuntime runtime)
    {
        var notes = runtime.MarkdownEditor("notes", _source, _state, width: 640f);
        if (notes.ClickedLink is { } link) OpenedLink = link;
        if (notes.Edited) Saves++;
        return notes.Node;
    }
}
```
