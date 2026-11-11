using System.Numerics;
using Bibui.Core;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

/// <summary>
/// What a Markdown editor keeps between frames, as <see cref="TextEditState"/>
/// does for a field: the caller owns it, one per editor.
/// </summary>
public sealed class MarkdownEditState
{
    /// <summary>
    /// The source offset of the block being edited, or -1 when every block is
    /// rendered. An offset, not a block index, so it survives the edit
    /// splitting or joining blocks: after a reparse the active block is the one
    /// whose span holds the caret. Set it, with <see cref="Edit"/>, to open the
    /// block holding it, focused and scrolled into view.
    /// </summary>
    public int Active = -1;

    /// <summary>The caret and selection in the active block's source, in offsets into the whole source.</summary>
    public TextEditState Edit = new();

    // The active block's text as its field edits it, and the field's caret in
    // it; its syntax as the field's ranges, Ranges[..RangeCount], found again
    // each frame into an array that grows to the most found and never shrinks.
    internal readonly BibuiStr Field = new(0);
    internal readonly StyledTextState FieldState = new();
    internal TextStyleRange[] Ranges = [];
    internal int RangeCount;

    // The source last parsed, and its document.
    internal readonly MarkdownParse Parse = new();
    internal Box? FieldBox;
    internal long FieldFrame = -1;   // the frame the field was last built in

    // Active and Edit as the editor left them, so what the app wrote is told apart.
    internal int LeftActive = -1, LeftCursor, LeftMark;
    internal bool FocusAsked;   // the field was given focus last frame, to have it this one
    internal int Reveal;        // frames left in which to scroll the caret into view

    // Names made once per id, so a frame allocates none.
    internal string? Id;
    internal string FieldId = "", BeforeId = "", AfterId = "", BlockScope = "";

    internal void Name(string id)
    {
        if (Id == id) return;
        Id = id;
        FieldId = $"{id}/field";
        BeforeId = $"{id}/before";
        AfterId = $"{id}/after";
        BlockScope = $"{id}/blocks";
    }
}

/// <summary>
/// What <see cref="MarkdownEditorExtensions.MarkdownEditor"/> built: its view,
/// the destination of a link clicked this frame, and whether the source changed
/// this frame.
/// </summary>
public readonly record struct MarkdownEditorWidget(Box Node, string? ClickedLink, bool Edited);

/// <summary>
/// A Markdown document rendered, with the block being edited shown as its
/// source, in a <see cref="StyledTextFieldExtensions.StyledTextField"/> in its
/// place, its syntax coloured by <see cref="MarkdownHighlight"/>. The blocks
/// before and after it are drawn by the viewer, spaced as the whole document.
///
/// <para>A block's text is its lines and the blank lines and link definitions
/// after it, up to the line break before the next block; the first block also
/// owns anything before it. So every char of the source but those line breaks
/// is in exactly one block's text.</para>
/// </summary>
internal static class MarkdownEditing
{
    // A block's text: [Start, End) of the source, End exclusive.
    internal readonly record struct Region(int Index, int Start, int End);

    internal static Region RegionAt(MarkdownDocument document, int length, int index)
    {
        var blocks = document.Root.Children;
        if (blocks.Count == 0) return new Region(0, 0, length);
        int start = index == 0 ? 0 : blocks[index].SourceStart;
        int end = index == blocks.Count - 1 ? length : blocks[index + 1].SourceStart - 1;
        return new Region(index, start, end);
    }

    // The region offset is in: the last that starts at or before it.
    internal static Region RegionOf(MarkdownDocument document, int length, int offset)
    {
        var blocks = document.Root.Children;
        int index = 0;
        for (int i = 1; i < blocks.Count && blocks[i].SourceStart <= offset; i++) index = i;
        return RegionAt(document, length, index);
    }

    // Focus to the field from the next frame, and the caret into view.
    internal static void Open(BibuiRuntime runtime, MarkdownEditState state)
    {
        runtime.Focus(state.FieldId);
        state.FocusAsked = true;
        state.Reveal = 2;
    }

    // The keys that would take the caret out of the field, eaten before the
    // field reads them: up from its first line, down from its last, left at its
    // start and right at its end move the caret into the block beside it;
    // Backspace at the start and Delete at the end remove the line break
    // between the two, joining them. Answers whether the source changed.
    internal static bool CrossEdges(BibuiRuntime runtime, MarkdownEditState state, BibuiStr source, Region region,
        ComponentSize textSize, float wrap, out bool moved)
    {
        moved = false;
        var edit = state.Edit;
        int length = (int)source.Length;
        bool collapsed = edit.Cursor == edit.Mark;
        bool atStart = collapsed && edit.Cursor == region.Start, atEnd = collapsed && edit.Cursor == region.End;
        const TextActionFlags Scans = TextActionFlags.WordScan | TextActionFlags.LineScan | TextActionFlags.KeepMark
            | TextActionFlags.SelectAll | TextActionFlags.PageScan;

        foreach (var ev in runtime.UnconsumedEvents)
        {
            int to = -1;
            if (ev.Kind == BibUiEventKind.Navigate && ev.DeltaY != 0 && (ev.Flags & Scans) == 0)
            {
                var text = source.Str;
                var block = text[region.Start..region.End];
                state.RangeCount = MarkdownHighlight.RangesFromSource(block, ref state.Ranges);
                var lines = Lines(runtime, state, block, textSize, wrap);
                int line = TextField.LineOf(lines, edit.Cursor - region.Start);
                int column = edit.Cursor - region.Start - lines[line].Start;
                if (ev.DeltaY < 0 && line == 0 && region.Start > 0)
                    to = LineAt(text, LineStart(text, region.Start - 1), column);
                else if (ev.DeltaY > 0 && line == lines.Length - 1 && region.End < length)
                    to = LineAt(text, region.End + 1, column);
            }
            else if (ev.Kind == BibUiEventKind.Navigate && ev.DeltaY == 0 && (ev.Flags & Scans) == 0)
            {
                if (ev.Delta < 0 && atStart && region.Start > 0) to = region.Start - 1;
                else if (ev.Delta > 0 && atEnd && region.End < length) to = region.End + 1;
            }
            else if (ev.Kind == BibUiEventKind.Edit && (ev.Flags & TextActionFlags.Delete) != 0
                && (ev.Flags & (TextActionFlags.Copy | TextActionFlags.WordScan)) == 0)
            {
                // The line break between two blocks is neither's, so removing it is the editor's.
                int at = ev.Delta < 0 && atStart && region.Start > 0 ? region.Start - 1
                    : ev.Delta > 0 && atEnd && region.End < length ? region.End
                    : -1;
                if (at < 0) continue;
                ev.Eat();
                source.ReplaceRange((at, at + 1), "");
                edit.Cursor = edit.Mark = at;
                moved = true;
                return true;
            }
            if (to < 0) continue;
            ev.Eat();
            edit.Cursor = edit.Mark = to;
            moved = true;
            return false;
        }
        return false;
    }

    // The end of the source line of the block at [start, end) of `text` that
    // `fraction` of the way down its rendered height stands for: its lines
    // spread evenly over its height, as near as a rendered block can say.
    internal static int LineEnd(ReadOnlySpan<char> text, int start, int end, float fraction)
    {
        var source = text[start..end];
        int lines = source.Count('\n') + 1;
        int line = Math.Clamp((int)(fraction * lines), 0, lines - 1);
        int lineStart = 0;
        for (int i = 0; i < line; i++) lineStart += source[lineStart..].IndexOf('\n') + 1;
        int lineEnd = source[lineStart..].IndexOf('\n');
        return start + (lineEnd < 0 ? source.Length : lineStart + lineEnd);
    }

    // Scrolls the view so the caret's line is in it with a line to spare.
    // Called after the build, by the rects the field and the view's first child
    // were last laid out at, so the caret's place in the content holds however
    // far the view has eased. Asked for a frame or two after the caret moves,
    // while the layout catches up with a block that changed.
    internal static void Reveal(BibuiRuntime runtime, MarkdownEditState state, Box view, Box field, Font font,
        ComponentSize textSize)
    {
        if (view.FirstChild is not { } top || view.Extent.Y <= 0f) return;
        state.Reveal--;
        float wrap = field.Extent.X > 0f ? field.Extent.X : float.PositiveInfinity;
        var lines = Lines(runtime, state, state.Field.Str, textSize, wrap);
        float advance = runtime.Fonts.LineAdvance(font);
        float caret = field.Rect.Y - top.Rect.Y + TextField.LineOf(lines, state.FieldState.Edit.Cursor) * advance;
        TextField.ScrollToCaret(view, new Vector2(0f, caret), new Vector2(0f, runtime.Fonts.LineHeight(font)),
            new Vector2(0f, view.ContentSize.Y), new Vector2(0f, advance));
    }

    // The lines `text`, a block's, breaks into in the field: in the faces of
    // its ranges, Ranges[..RangeCount].
    internal static ReadOnlySpan<TextLine> Lines(BibuiRuntime runtime, MarkdownEditState state, ReadOnlySpan<char> text,
        ComponentSize textSize, float wrap)
    {
        ReadOnlySpan<TextStyleRange> ranges = state.Ranges.AsSpan(0, state.RangeCount);
        int runs = state.FieldState.Room(ranges);
        using (runtime.FontFace(FontSlot.Body, FontWeight.Regular))
        using (runtime.FontSize(textSize))
            return new StyledText(runtime, text, runtime.FontInScope(), ranges, state.FieldState.Runs.AsSpan(0, runs),
                state.FieldState.Faces.AsSpan(0, runs)).Lines(wrap, TextWrap.Words);
    }

    internal static int LineStart(ReadOnlySpan<char> text, int offset) => text[..offset].LastIndexOf('\n') + 1;

    // `column` chars into the line starting at `start`, or its end if it is shorter.
    internal static int LineAt(ReadOnlySpan<char> text, int start, int column)
    {
        int end = text[start..].IndexOf('\n');
        return start + Math.Min(column, end < 0 ? text.Length - start : end);
    }
}

/// <summary>The MarkdownEditor component, called on the runtime that builds it.</summary>
public static class MarkdownEditorExtensions
{
    /// <summary>
    /// Builds the editor named <paramref name="id"/> under the parent in scope:
    /// <paramref name="source"/> rendered as <see cref="MarkdownExtensions.Markdown(BibuiRuntime, string, BibuiStr, float?, ComponentSize)"/>
    /// renders it, with the block being edited shown as its source and edited
    /// in place. A press on a block edits it; Escape, or a press outside it,
    /// renders it again. Edits, ticked tasks and joined blocks go straight
    /// into <paramref name="source"/>. Its view is <paramref name="width"/>
    /// wide, or as wide as its parent when that is null, as tall as its
    /// parent, and scrolls.
    /// </summary>
    public static MarkdownEditorWidget MarkdownEditor(this BibuiRuntime runtime, string id, BibuiStr source,
        MarkdownEditState state, float? width = null, ComponentSize textSize = ComponentSize.Md)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(state);
        state.Name(id);
        var edit = state.Edit;
        var document = Markdown.DocumentFor(state.Parse, source);
        int count = document.Root.Children.Count;
        bool edited = false;

        Font font;
        using (runtime.FontFace(FontSlot.Body, FontWeight.Regular))
            font = runtime.FontInScope(textSize);

        // What the app wrote opens its block; focus gone elsewhere, a press
        // outside or the app's, renders it again. An empty document has no
        // block to press, so its field is always there.
        bool focused = runtime.IsFocused(state.FieldId);
        bool asked = state.FocusAsked;
        state.FocusAsked = false;
        bool written = state.Active != state.LeftActive || edit.Cursor != state.LeftCursor || edit.Mark != state.LeftMark;
        if (state.Active >= 0 && written) MarkdownEditing.Open(runtime, state);
        else if (state.Active >= 0 && !focused && !asked) state.Active = -1;
        if (count == 0 && focused) state.Active = 0;
        bool editing = state.Active >= 0 || count == 0;

        MarkdownEditing.Region region = default;
        if (editing)
        {
            int length = (int)source.Length;
            int at = Math.Clamp(state.Active >= 0 && state.Active != state.LeftActive ? state.Active : edit.Cursor, 0, length);
            region = MarkdownEditing.RegionOf(document, length, at);
            if (edit.Cursor < region.Start || edit.Cursor > region.End) edit.Cursor = edit.Mark = at;
            edit.Mark = Math.Clamp(edit.Mark, region.Start, region.End);

            if (focused)
            {
                float wrap = state.FieldBox is { Extent.X: > 0f } field ? field.Extent.X : float.PositiveInfinity;
                if (MarkdownEditing.CrossEdges(runtime, state, source, region, textSize, wrap, out bool moved))
                {
                    edited = true;
                    document = Markdown.DocumentFor(state.Parse, source);
                    count = document.Root.Children.Count;
                }
                if (moved)
                {
                    region = MarkdownEditing.RegionOf(document, (int)source.Length, edit.Cursor);
                    state.Reveal = Math.Max(state.Reveal, 1);
                }
            }
        }
        else state.Reveal = 0;

        // The view asks first, so the wheel scrolls the document wherever the
        // pointer is, the field included: the field is as tall as its lines.
        Box view = runtime.BoxFromId(id, InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip);
        runtime.InteractionsFor(view);
        view.ChildLayoutAxis = LayoutAxis.Y;
        view.SizeX = width is { } w ? SemanticSize.Pixels(w) : SemanticSize.ParentPct(1f);
        view.SizeY = SemanticSize.ParentPct(1f);
        view.AllowOverflowY = true;

        bool laidOut = state.FieldFrame == runtime.FrameCount - 1;
        MarkdownWidget before, after = default;
        int pressedBefore, pressedAfter = -1;
        Vector2 atBefore, atAfter = default, sizeBefore, sizeAfter = default;
        int fieldStart = region.Start, fieldEnd = region.End;
        using (runtime.Parent(view))
        {
            if (!editing)
                before = Markdown.DrawRange(runtime, state.BeforeId, document, .., width, textSize, state.BlockScope, out pressedBefore, out atBefore, out sizeBefore);
            else
            {
                before = Markdown.DrawRange(runtime, state.BeforeId, document, ..region.Index, width, textSize, state.BlockScope, out pressedBefore, out atBefore, out sizeBefore);
                if (region.Index > 0) runtime.Gap(Markdown.GapAbove(runtime, document, region.Index, textSize));

                Box frame = runtime.Box();
                frame.ChildLayoutAxis = LayoutAxis.Y;
                frame.SizeX = width is { } fw ? SemanticSize.Pixels(fw) : SemanticSize.ParentPct(1f);
                frame.SizeY = SemanticSize.ChildrenSum();
                TextField.InputStyle(runtime, frame, focused);

                var slice = source.Str[region.Start..region.End];
                if (!state.Field.Str.SequenceEqual(slice)) state.Field.ReplaceRange((0, (int)state.Field.Length), slice);
                var fieldEdit = state.FieldState.Edit;
                fieldEdit.Cursor = edit.Cursor - region.Start;
                fieldEdit.Mark = edit.Mark - region.Start;
                state.RangeCount = MarkdownHighlight.RangesFromSource(state.Field.Str, ref state.Ranges);
                int cursorBefore = edit.Cursor;
                BibuiWidget field;
                using (runtime.Parent(frame))
                using (runtime.FontFace(FontSlot.Body, FontWeight.Regular))
                using (runtime.FontSize(textSize))
                using (runtime.SizeY(SemanticSize.ChildrenSum()))
                    field = runtime.StyledTextField(state.FieldId, state.Field, state.FieldState, state.Ranges.AsSpan(0, state.RangeCount));
                state.FieldBox = (Box)field.Node;
                state.FieldFrame = runtime.FrameCount;
                edit.Cursor = fieldEdit.Cursor + region.Start;
                edit.Mark = fieldEdit.Mark + region.Start;
                if (field.Interaction.Cancelled) state.Active = -1;
                if (edit.Cursor != cursorBefore) state.Reveal = Math.Max(state.Reveal, 1);
                if (state.Reveal > 0 && laidOut) MarkdownEditing.Reveal(runtime, state, view, state.FieldBox, font, textSize);

                if (count > 0)
                    after = Markdown.DrawRange(runtime, state.AfterId, document, (region.Index + 1).., width, textSize, state.BlockScope, out pressedAfter, out atAfter, out sizeAfter);
            }
        }

        // The field's edits go into the source after the blocks are drawn, so
        // the offsets they reported are moved past the edit by its length.
        int delta = 0;
        if (editing && !state.Field.Str.SequenceEqual(source.Str[fieldStart..fieldEnd]))
        {
            source.ReplaceRange((fieldStart, fieldEnd), state.Field.Str);
            delta = (int)state.Field.Length - (fieldEnd - fieldStart);
            edited = true;
            state.Reveal = Math.Max(state.Reveal, 1);
        }
        int Moved(int offset) => offset >= fieldEnd ? offset + delta : offset;

        bool drewAfter = editing && count > 0;
        string? link = before.ClickedLink ?? (drewAfter ? after.ClickedLink : null);
        int task = before.TaskClicked ? before.ToggledTask : drewAfter && after.TaskClicked ? after.ToggledTask : -1;
        if (task >= 0)
        {
            Markdown.ToggleTask(source, Moved(task));
            edited = true;
        }

        // A press on a rendered block edits it, the caret at the end of the
        // source line under the pointer.
        int pressed = pressedBefore >= 0 ? pressedBefore : pressedAfter;
        if (pressed >= 0)
        {
            var (at, size) = pressedBefore >= 0 ? (atBefore, sizeBefore) : (atAfter, sizeAfter);
            var block = document.Root.Children[pressed];
            int caret = MarkdownEditing.LineEnd(source.Str, Moved(block.SourceStart), Moved(block.SourceEnd),
                size.Y > 0f ? at.Y / size.Y : 0f);
            edit.Cursor = edit.Mark = caret;
            state.Active = caret;
            MarkdownEditing.Open(runtime, state);
        }

        if (edited)
        {
            document = Markdown.DocumentFor(state.Parse, source);
            runtime.RequestFrame();
        }
        if (state.Active >= 0)
        {
            int length = (int)source.Length;
            edit.Cursor = Math.Clamp(edit.Cursor, 0, length);
            edit.Mark = Math.Clamp(edit.Mark, 0, length);
            state.Active = MarkdownEditing.RegionOf(document, length, edit.Cursor).Start;
        }
        if (state.Reveal > 0) runtime.RequestFrame();
        state.LeftActive = state.Active;
        state.LeftCursor = edit.Cursor;
        state.LeftMark = edit.Mark;
        return new MarkdownEditorWidget(view, link, edited);
    }
}
