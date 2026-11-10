# StyledTextField

A multi-line field whose text is drawn in more than one style: ranges of it in
a colour or a weight of their own, for syntax in an editor. It is a
[`MultilineTextField`](text-field.md) in every other way: it wraps at its
width, scrolls, pages, takes focus and edits your buffer in place.

You give the ranges each frame, as your highlighter finds them in the text,
in a buffer of your own. The field draws each range in its colour and weight,
and breaks lines, places the caret and finds the pointer with each range in
its own face.

The gallery's StyledTextField page shows it on Markdown source, its markers
dimmed, strong text bold and code in the accent colour. It has no fixture yet.

## StyledTextField

```cs
public static BibuiWidget StyledTextField(
    this BibuiRuntime runtime, string id, BibuiStr text, StyledTextState state,
    Span<TextStyleRange> ranges,
    uint events = MultiLineEvents,
    TextColor textColor = TextColor.Primary,
    ReadOnlySpan<char> placeholder = default,
    TextWrap wrap = TextWrap.Words)

public readonly record struct TextStyleRange(
    int Start, int Length, UiColor? Color = null, FontWeight? Weight = null)
{
    public int End { get; }
}

public sealed class StyledTextState
{
    public TextEditState Edit = new();
}
```

| Parameter | Meaning |
|---|---|
| `state` | What the field keeps between frames: `Edit`, the caret and selection, and room for the runs it measures by. One per field, yours to keep. |
| `ranges` | Ranges of `text` in a style of their own, as char offsets (UTF-16 code units) into the text as it is before this frame's edits; sorted by `Start`, none overlapping, all inside the text. The field writes to them. |
| `TextStyleRange.Color` | The range's colour, or the field's `textColor` when null. |
| `TextStyleRange.Weight` | The range's `FontWeight`, or the face in scope when null. |

Every other parameter is `MultilineTextField`'s; see [TextField](text-field.md).

- **Order.** The ranges are sorted by `Start`, and none starts before the one
  before it ends; all lie inside the text. A highlighter that scans the text
  from the start finds them that way. The field does not sort them or drop
  any; a debug build asserts the order. Overlapping styles are not merged:
  give them already split. An empty range draws nothing.
- **Clusters.** A range end inside a cluster (an emoji with its skin tone, a
  letter and its combining mark) moves to the cluster's end, in your span, so
  a range never splits what is drawn as one character.
- **Edits.** As the field applies each edit, it shifts your ranges in place,
  so the frame is drawn with ranges that match the edited text; you find them
  again next frame. Text typed where a range starts or ends goes outside it,
  and so does a replacement's; text typed inside it grows it.
- **Widths.** A range with a `Weight` is measured in its face. A range that only
  colours changes no width, so a field with only those, or none, is measured
  and broken as a plain `MultilineTextField` is.
- **Double click.** A word is selected within the stretch of its line in one
  face: a word half bold, half regular is selected up to the change of face.
  Between ranges that only colour, the whole word is.

## Claims and reports

The same as `MultilineTextField`'s: the view box claims `Clip`,
`ViewScrollY | ClampY`, and `LeftPress` and `Keyboard` when `events` is not 0;
it returns a `BibuiWidget` whose `Node` is the view. Up, Down, Page Up and
Page Down move by the field's lines as its faces break them. Escape ends the
edit; Enter is a line break.

## State the caller owns

The text, the `StyledTextState`, and the ranges' buffer. The state's `Edit` is
the caret and selection; the rest of it is the field's room for its runs,
which grows to the most the field has needed and is not given back, so a
steady frame allocates nothing. The field moves the ranges in your buffer, in
place: an end into a cluster, and with each edit. Focus is the runtime's. Without focus, the field keeps its selection in sight, dimmer,
and shows no caret.

## Markdown source

`MarkdownHighlight.RangesFromSource` writes a Markdown source's syntax as
ranges, the colouring the `MarkdownEditor` gives the block it edits: markers
dimmed, code in the accent colour, strong bold, emphasis italic, sorted and not
overlapping. The array grows to the most ranges found and never shrinks.

```csharp
int count = MarkdownHighlight.RangesFromSource(_source.Str, ref _ranges);
runtime.StyledTextField("notes", _source, _state, _ranges.AsSpan(0, count));
```

It is a grammar of its own, as small as it can be, so it can differ from how
the viewer renders: emphasis is found within a line, not across a paragraph's
lines, and strikethrough, autolinks, HTML, setext underlines and thematic
breaks are left plain.

## Theme tokens

As `MultilineTextField`'s, and each range's `Color`, a token resolved at paint
time like any other.

## Example

```csharp
using Bibui.Core;
using Bibui.Core.Runtime;

sealed class NoteEditor
{
    readonly BibuiStr _source = BibuiStr.FromStrLit("# Notes\nSome **bold** words.");
    readonly StyledTextState _state = new();
    readonly TextStyleRange[] _ranges = new TextStyleRange[2];

    public UiNode Build(BibuiRuntime runtime)
    {
        // Found again from the source each frame: a heading bold, its hash dimmed.
        int count = 0;
        ReadOnlySpan<char> text = _source.Str;
        int end = text.IndexOf('\n');
        if (text.StartsWith("# ") && end > 0)
        {
            _ranges[count++] = new TextStyleRange(0, 2, UiColor.TextSecondary);
            _ranges[count++] = new TextStyleRange(2, end - 2, Weight: FontWeight.Bold);
        }

        Box frame = runtime.Box();
        TextField.InputStyle(runtime, frame, runtime.IsFocused("notes"));
        frame.SizeX = SemanticSize.Pixels(400f);
        frame.SizeY = SemanticSize.ChildrenSum();

        runtime.BeginParent(frame);
        runtime.BeginSizeY(TextField.Rows(runtime, 6));
        runtime.StyledTextField("notes", _source, _state, _ranges.AsSpan(0, count));
        runtime.EndSizeY();
        runtime.EndParent(frame);
        return frame;
    }
}
```
