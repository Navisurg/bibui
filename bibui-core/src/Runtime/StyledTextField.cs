using System.Diagnostics;
using System.Numerics;
using Bibui.Core;
using Bibui.Core.TextEdit;
using static Bibui.Core.Runtime.TextField;

namespace Bibui.Core.Runtime;

/// <summary>
/// A range of a <see cref="StyledTextFieldExtensions.StyledTextField"/>'s text
/// in a colour or a weight of its own: <see cref="Length"/> chars from
/// <see cref="Start"/>. What is not said is the field's: a null
/// <see cref="Color"/> is its text colour, a null <see cref="Weight"/> the face
/// in scope. For syntax in an editor: a Markdown source's markers dimmed, its
/// strong text bold.
/// </summary>
public readonly record struct TextStyleRange(int Start, int Length, UiColor? Color = null, FontWeight? Weight = null)
{
    /// <summary>Where the range ends: the offset after its last char.</summary>
    public int End => Start + Length;
}

/// <summary>
/// What a styled field keeps between frames, as <see cref="TextEditState"/> does
/// for a plain one: the caller owns it, one per field.
/// </summary>
public sealed class StyledTextState
{
    /// <summary>The caret and selection.</summary>
    public TextEditState Edit = new();

    // The field's own: where each face-setting range's weight starts and ends
    // (StyleRun), and the font of each run, rebuilt from the ranges every frame
    // into arrays that grow to the most runs seen and never shrink.
    internal StyleRun[] Runs = [];
    internal Font[] Faces = [];

    // How many runs `ranges` make, two for each that sets a face and the
    // field's, or none; Runs and Faces grown to hold them.
    internal int Room(ReadOnlySpan<TextStyleRange> ranges)
    {
        int faced = 0;
        foreach (ref readonly var range in ranges) if (range.Weight is not null) faced++;
        int count = faced == 0 ? 0 : 2 * faced + 1;
        if (count > Runs.Length)
        {
            int size = (int)BitOperations.RoundUpToPowerOf2((uint)count);
            Array.Resize(ref Runs, size);
            Array.Resize(ref Faces, size);
        }
        return count;
    }
}

/// <summary>The StyledTextField component, called on the runtime that builds it.</summary>
public static class StyledTextFieldExtensions
{
    /// <summary>
    /// <see cref="TextFieldExtensions.MultilineTextField"/> with
    /// <paramref name="ranges"/> of its text in colours and weights of their
    /// own, in offsets into <paramref name="text"/> as it is before this
    /// frame's edits: sorted by their starts, none overlapping the one before
    /// it, all inside the text. The field moves an end inside a cluster to the
    /// cluster's end, and shifts them with each edit it makes, in place, so
    /// this frame is drawn with ranges that match the edited text; the caller
    /// gives them again next frame. Text typed where a range starts or ends
    /// goes outside it. Lines break, and the caret and the pointer are placed,
    /// with each range in its own face; a range that only colours changes no
    /// width, so it is measured as plain text. Sized as MultilineTextField is,
    /// <paramref name="fitRows"/> too.
    /// </summary>
    public static BibuiWidget StyledTextField(this BibuiRuntime runtime, string id, BibuiStr text,
        StyledTextState state, Span<TextStyleRange> ranges,
        uint events = global::Bibui.Core.Runtime.TextField.MultiLineEvents,
        TextColor textColor = TextColor.Primary, ReadOnlySpan<char> placeholder = default, TextWrap wrap = TextWrap.Words,
        (int Min, int Max)? fitRows = null)
    {
        Debug.Assert(StyledText.InOrder(ranges, text.Str.Length),
            "A styled field's ranges are sorted by their starts, none overlapping the one before it, all inside its text.");
        Font font = runtime.FontInScope();
        StyledText.Snap(runtime.Fonts, font, text.Str, ranges);
        int runCount = state.Room(ranges);
        return Field(runtime, id, text, state.Edit, font, ranges, state.Runs.AsSpan(0, runCount), state.Faces.AsSpan(0, runCount),
            events, textColor, placeholder, wrap, fitRows);
    }

    private static BibuiWidget Field(BibuiRuntime runtime, string id, BibuiStr text, TextEditState edit, Font font,
        Span<TextStyleRange> ranges, Span<StyleRun> runs, Span<Font> faces, uint events, TextColor textColor,
        ReadOnlySpan<char> placeholder, TextWrap wrap, (int Min, int Max)? fitRows)
    {
        // The field's own view: as tall as the size pushed, scrolling, cut at its edge.
        var claims = InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip
            | (events != 0 ? InteractionKinds.LeftPress | InteractionKinds.Keyboard : InteractionKinds.None);
        Box view = runtime.BoxFromId(id, claims);
        Interaction interaction = runtime.InteractionsFor(view);
        bool focused = interaction.Focused;
        view.ChildLayoutAxis = LayoutAxis.Y;
        view.SizeX = SemanticSize.ParentPct(1f);
        view.AllowOverflowY = true;
        int caretBefore = edit.Cursor;
        float lineHeight = runtime.Fonts.LineHeight(font);
        float lineAdvance = runtime.Fonts.LineAdvance(font);
        // Wrapped at last frame's width; unwrapped before the first layout.
        float room = view.Extent.X > 0f ? view.Extent.X : float.PositiveInfinity;

        var styled = new StyledText(runtime, text.Str, font, ranges, runs, faces);
        if (interaction.Pressed || interaction.Dragging)
        {
            // The pointer on the stretch of its line in one face, as on a single
            // line in that face: the edit state moved into the stretch's offsets, and back.
            float pointerX = interaction.X - view.Rect.X;
            float pointerY = interaction.Y - view.Rect.Y + view.ViewOffset.Y;
            var pointerLines = styled.Lines(room, wrap);
            var line = pointerLines[Math.Clamp((int)MathF.Floor(pointerY / lineAdvance), 0, pointerLines.Length - 1)];
            var piece = styled.PieceAt(line.Start, line.End, pointerX);
            edit.Cursor -= piece.Start; edit.Mark -= piece.Start;
            HandlePointer(runtime, piece.Font, interaction, text.Str[piece.Start..piece.End], edit, piece.X, focused);
            edit.Cursor += piece.Start; edit.Mark += piece.Start;
        }

        // A page as MultilineTextField counts one: the lines shown last frame less one.
        int pagedLines = 0;
        bool edited = focused && ApplyEdits(runtime, text, edit, events, font, ranges, runs, faces, room, wrap,
            TextField.PageRows(view.Extent.Y, lineHeight, lineAdvance), out pagedLines);
        if (pagedLines != 0)
            view.TargetViewOffset = view.TargetViewOffset with { Y = view.TargetViewOffset.Y + pagedLines * lineAdvance };
        if (focused) EndingKeys(runtime, ref interaction, submits: false);

        // An empty buffer shows the placeholder, in a quieter colour, unstyled.
        bool empty = text.Length == 0;
        ReadOnlySpan<char> shown = empty ? placeholder : text.Str;
        TextColor color = empty ? TextColor.Secondary : textColor;
        if (edited || empty) styled = new StyledText(runtime, shown, font, empty ? [] : ranges, runs, faces);
        var lines = styled.Lines(room, wrap);
        // Fitted to the lines as edited this frame, so it grows with the line typed.
        if (fitRows is { } fit) view.SizeY = FitRows(runtime, lines.Length, fit);

        // Not focused: no caret, and the selection drawn dimmer. Empty: the
        // caret is before the placeholder, which is not text to select.
        int caret = !empty ? Math.Clamp(edit.Cursor, 0, shown.Length) : 0;
        int mark = !empty ? Math.Clamp(edit.Mark, 0, shown.Length) : 0;
        int lo = Math.Min(caret, mark), hi = Math.Max(caret, mark);
        UiColor highlight = focused ? Highlight : UnfocusedHighlight;

        using var inView = runtime.Parent(view);

        // A row per line, LineAdvance apart.
        int caretLine = -1;
        for (int index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (index > 0) runtime.Gap(lineAdvance - lineHeight);
            // The caret's line: the first that holds it, or the space after it does.
            if (caretLine < 0 && caret <= (index + 1 < lines.Length ? line.Next - 1 : int.MaxValue)) caretLine = index;

            Box row = runtime.Box();
            row.ChildLayoutAxis = LayoutAxis.X;
            row.SizeX = SemanticSize.ParentPct(1f);
            row.SizeY = SemanticSize.Pixels(lineHeight);
            int selectionStart = Math.Clamp(lo, line.Start, line.End);
            int selectionEnd = Math.Clamp(hi, line.Start, line.End);
            using (runtime.Parent(row))
            {
                if (styled.Ranges.IsEmpty)
                    DrawLine(runtime, shown[line.Start..line.End], selectionStart - line.Start, selectionEnd - line.Start, color, highlight);
                else
                    DrawStyledLine(runtime, shown, styled.Ranges, line.Start, line.End, selectionStart, selectionEnd, color, highlight);
            }
        }

        // The caret last, over the text, at its x in its line's faces.
        if (focused)
        {
            int caretLineIndex = Math.Max(0, caretLine);
            float lineTop = caretLineIndex * lineAdvance;
            DrawCaretAt(runtime, view, styled.Measure(lines[caretLineIndex].Start, caret), lineTop,
                SemanticSize.Pixels(lineHeight), edited);

            // Its line is kept in sight when it moves, so a wheel scroll is left alone.
            if (!empty && (edited || edit.Cursor != caretBefore))
                ScrollToCaret(view, new Vector2(0f, lineTop), new Vector2(0f, lineHeight),
                    new Vector2(0f, lineHeight + Math.Max(0, lines.Length - 1) * lineAdvance), new Vector2(0f, lineHeight));
        }
        return new BibuiWidget(view, interaction);
    }

    // Applies and eats the frame's events of the given kinds, in order, as
    // TextField.ApplyEdits does, the ranges moving with each edit and a move
    // between lines made in the text's faces. A page is pageRows lines, and
    // with pageRows 0 is left to the app; pagedLines is how many lines the
    // frame's pages moved the caret. Answers whether the text changed.
    private static bool ApplyEdits(BibuiRuntime runtime, BibuiStr text, TextEditState edit, uint kinds, Font font,
        Span<TextStyleRange> ranges, Span<StyleRun> runs, Span<Font> faces, float wrapWidth, TextWrap wrap, int pageRows,
        out int pagedLines)
    {
        pagedLines = 0;
        bool edited = false;
        Span<char> charBuf = stackalloc char[TextOp.MaxCharsPerCodePoint];
        foreach (var ev in runtime.UnconsumedEvents)
        {
            if ((kinds & 1u << (int)ev.Kind) == 0) continue;
            if (ev.Kind == BibUiEventKind.Navigate && ev.DeltaY != 0)
            {
                if ((ev.Flags & TextActionFlags.PageScan) != 0 && pageRows == 0) continue;
                pagedLines += MoveVertically(new StyledText(runtime, text.Str, font, ranges, runs, faces), edit, wrapWidth, wrap, ev, pageRows);
                ev.Eat();
                continue;
            }
            var op = TextOp.FromStateAndEvent(runtime.Fonts, font, text, edit, ev, charBuf);
            if (op.Range.Item1 != op.Range.Item2 || op.ReplaceString.Length > 0)
            {
                edited = true;
                text.ReplaceRange(op.Range, op.ReplaceString);
                StyledText.Shift(ranges, op.Range.Item1, op.Range.Item2, op.ReplaceString.Length);
                StyledText.Snap(runtime.Fonts, font, text.Str, ranges);
            }
            edit.Cursor = op.NewCursor;
            edit.Mark = op.NewMark;
            if (op.CopyString.Length > 0) runtime.ClipboardSet(new string(op.CopyString));
            ev.Eat();
        }
        return edited;
    }

    // TextField.MoveVertically in a styled text's lines: up or down a line, or
    // pageRows with PageScan, to the char nearest the caret's x on that line,
    // or to the start or end of the text with no line there. Answers how many
    // lines a page moved the caret.
    private static int MoveVertically(StyledText text, TextEditState edit, float wrapWidth, TextWrap wrap, BibUiEvent ev, int pageRows)
    {
        var lines = text.Lines(wrapWidth, wrap);
        bool page = (ev.Flags & TextActionFlags.PageScan) != 0;
        int on = LineOf(lines, edit.Cursor);
        int to = Math.Clamp(on + Math.Sign(ev.DeltaY) * (page ? pageRows : 1), 0, lines.Length - 1);
        if (to == on)
            edit.Cursor = ev.DeltaY > 0 ? text.Length : 0;
        else
            edit.Cursor = lines[to].Start + text.OffsetAt(lines[to].Start, lines[to].End, text.Measure(lines[on].Start, edit.Cursor));
        if ((ev.Flags & TextActionFlags.KeepMark) == 0) edit.Mark = edit.Cursor;
        return page && to != on ? to - on : 0;
    }

    // A line of a styled text, chars lineStart..lineEnd, as DrawLine draws one:
    // texts side by side, cut at the selection [lo, hi) and where a range
    // starts or ends, each in its range's colour and weight.
    private static void DrawStyledLine(BibuiRuntime runtime, ReadOnlySpan<char> text, ReadOnlySpan<TextStyleRange> ranges,
        int lineStart, int lineEnd, int lo, int hi, TextColor color, UiColor highlight)
    {
        if (lo > lineStart) Pieces(runtime, text, ranges, lineStart, lo, color);
        if (hi > lo)
        {
            Box selection = runtime.Box();
            selection.ChildLayoutAxis = LayoutAxis.X;
            selection.SizeX = SemanticSize.ChildrenSum();
            selection.SizeY = SemanticSize.ParentPct(1f);
            selection.Style.BackgroundColor = highlight;
            using (runtime.Parent(selection)) Pieces(runtime, text, ranges, lo, hi, color);
        }
        if (lineEnd > hi) Pieces(runtime, text, ranges, hi, lineEnd, color);
    }

    private static void Pieces(BibuiRuntime runtime, ReadOnlySpan<char> text, ReadOnlySpan<TextStyleRange> ranges,
        int from, int to, TextColor color)
    {
        int at = from;
        foreach (ref readonly var range in ranges[StyledText.FirstEndingAfter(ranges, from)..])
        {
            if (range.Start >= to) break;
            if (range.Length == 0) continue;
            if (range.Start > at)
            {
                Part(runtime, text[at..range.Start], color);
                at = range.Start;
            }
            int end = Math.Min(range.End, to);
            using (runtime.SizeX(SemanticSize.Text()))
            using (runtime.SizeY(SemanticSize.Text()))
                runtime.Text(text[at..end], textColor: range.Color ?? (UiColor)color, weight: range.Weight);
            at = end;
        }
        if (at < to) Part(runtime, text[at..to], color);
    }
}

/// <summary>
/// A field's text with its styled ranges, as the text layer measures and
/// breaks it: the ranges that set a face become the text's
/// <see cref="StyleRun"/>s, and every width, line and offset is the text
/// layer's over them. A text with no range that sets a face is measured and
/// broken as a plain field's.
/// </summary>
internal readonly ref struct StyledText
{
    private readonly Fonts _fonts;
    private readonly ReadOnlySpan<char> _text;
    private readonly Font _font;
    private readonly ReadOnlySpan<TextStyleRange> _ranges;
    private readonly ReadOnlySpan<StyleRun> _runs;
    private readonly ReadOnlySpan<Font> _faces;

    /// <param name="runs">Room for the text's runs, two for each range that sets a face and one more; written here.</param>
    /// <param name="faces">Room for the font of each run, as many.</param>
    public StyledText(BibuiRuntime runtime, ReadOnlySpan<char> text, Font font, ReadOnlySpan<TextStyleRange> ranges,
        Span<StyleRun> runs, Span<Font> faces)
    {
        _fonts = runtime.Fonts;
        _text = text;
        _font = font;
        _ranges = ranges;
        int count = Runs(runtime, font, ranges, runs, faces);
        _runs = runs[..count];
        _faces = faces[..count];
    }

    /// <summary>One style throughout: every call is the text layer's for one style.</summary>
    public bool Plain => _runs.IsEmpty;

    public int Length => _text.Length;

    public ReadOnlySpan<TextStyleRange> Ranges => _ranges;

    private FontStyle Style => TextLayer.StyleFromFont(_fonts, _font, _font.Scale);

    /// <summary>The width of chars <paramref name="from"/>..<paramref name="to"/>, on one line.</summary>
    public float Measure(int from, int to) => Plain
        ? TextLayer.WidthFromChars(_fonts, _text[from..to], Style, _font.Scale)
        : TextLayer.WidthFromChars(_fonts, _text, _runs, from, to, _font.Scale);

    /// <summary>The lines the text breaks into at <paramref name="width"/>, kept by the text layer for the frame.</summary>
    public ReadOnlySpan<TextLine> Lines(float width, TextWrap wrap) => Plain
        ? TextLayer.LinesFromText(_fonts, _text, Style, _font.Scale, width, wrap)
        : TextLayer.LinesFromText(_fonts, _text, _runs, _font.Scale, width, wrap);

    /// <summary>The offset, from <paramref name="lineStart"/>, of the cluster boundary nearest <paramref name="x"/> on that line.</summary>
    public int OffsetAt(int lineStart, int lineEnd, float x)
    {
        var piece = PieceAt(lineStart, lineEnd, x);
        return piece.Start - lineStart + CharAtX(_fonts, piece.Font, _text[piece.Start..piece.End], piece.X);
    }

    /// <summary>
    /// The stretch of line lineStart..lineEnd in one face that <paramref name="x"/>,
    /// from the line's start, falls in: its chars, its font, and x from where it
    /// starts. The last stretch past the line's end.
    /// </summary>
    public (int Start, int End, Font Font, float X) PieceAt(int lineStart, int lineEnd, float x)
    {
        if (Plain) return (lineStart, lineEnd, _font, x);
        int run = RunAt(_runs, lineStart);
        float pen = 0f;
        for (int at = lineStart; ; run++)
        {
            int end = run + 1 < _runs.Length ? Math.Min(lineEnd, _runs[run + 1].Start) : lineEnd;
            if (end == lineEnd) return (at, end, _faces[run], x - pen);
            float width = TextLayer.WidthFromChars(_fonts, _text[at..end], _runs[run].Style, _faces[run].Scale);
            if (x < pen + width) return (at, end, _faces[run], x - pen);
            pen += width;
            at = end;
        }
    }

    // The run at an offset: the last that starts at or before it, found by halving.
    private static int RunAt(ReadOnlySpan<StyleRun> runs, int at)
    {
        int lo = 0, hi = runs.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (runs[mid].Start <= at) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    // The runs, and the font of each: the field's from 0, and a run at each
    // range that sets a face, back to the field's at its end. A run in the
    // style of the one before it is not one, and an empty run gives way to the
    // one after it. None when every range is in the field's style. A face is
    // resolved once for ranges of one weight in a row.
    private static int Runs(BibuiRuntime runtime, Font font, ReadOnlySpan<TextStyleRange> ranges, Span<StyleRun> runs, Span<Font> faces)
    {
        if (runs.IsEmpty) return 0;
        var plain = TextLayer.StyleFromFont(runtime.Fonts, font, font.Scale);
        runs[0] = new StyleRun(0, plain);
        faces[0] = font;
        int count = 1;
        FontWeight? weight = null;
        Font face = font;
        FontStyle style = plain;
        foreach (ref readonly var range in ranges)
        {
            if (range.Weight is not { } w) continue;
            if (w != weight)
            {
                using (runtime.FontFace(weight: w)) face = runtime.FontInScope();
                style = TextLayer.StyleFromFont(runtime.Fonts, face, face.Scale);
                weight = w;
            }
            count = Add(runs, faces, count, new StyleRun(range.Start, style), face);
            count = Add(runs, faces, count, new StyleRun(range.End, plain), font);
        }
        return count == 1 ? 0 : count;
    }

    private static int Add(Span<StyleRun> runs, Span<Font> faces, int count, StyleRun run, Font face)
    {
        if (runs[count - 1].Start == run.Start) count--;
        if (count > 0 && runs[count - 1].Style == run.Style) return count;
        runs[count] = run;
        faces[count] = face;
        return count + 1;
    }

    /// <summary>
    /// The first of <paramref name="ranges"/>, in order and not overlapping, that
    /// ends after <paramref name="offset"/>, found by halving; their count when none does.
    /// </summary>
    public static int FirstEndingAfter(ReadOnlySpan<TextStyleRange> ranges, int offset)
    {
        int lo = 0, hi = ranges.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (ranges[mid].End <= offset) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Whether <paramref name="ranges"/> are as the field takes them: sorted by
    /// their starts, none overlapping the one before it, all inside a text of
    /// <paramref name="length"/> chars.
    /// </summary>
    public static bool InOrder(ReadOnlySpan<TextStyleRange> ranges, int length)
    {
        int end = 0;
        foreach (ref readonly var range in ranges)
        {
            if (range.Start < end || range.Length < 0 || range.End > length) return false;
            end = range.End;
        }
        return true;
    }

    /// <summary>
    /// Moves each range's ends that fall inside a cluster of <paramref name="text"/>
    /// to the cluster's end, in place: as given (an emoji without its skin
    /// tone), and after an edit made one there (a combining mark typed at a
    /// range's end), so a range never splits what is drawn as one character.
    /// Keeps them in order: a range left empty stays.
    /// </summary>
    public static void Snap(Fonts fonts, Font font, ReadOnlySpan<char> text, Span<TextStyleRange> ranges)
    {
        var style = TextLayer.StyleFromFont(fonts, font, font.Scale);
        int end = 0;
        for (int k = 0; k < ranges.Length; k++)
        {
            var r = ranges[k];
            int start = Math.Max(end, Boundary(fonts, style, text, Math.Min(r.Start, text.Length)));
            int stop = Math.Max(start, Boundary(fonts, style, text, Math.Min(r.End, text.Length)));
            ranges[k] = r with { Start = start, Length = stop - start };
            end = stop;
        }
    }

    // The cluster boundary at `at`, or the end of the cluster `at` falls inside.
    // A char below 256 always starts a cluster (TextLayer.ScanCluster), so only
    // a char past it needs the scan.
    private static int Boundary(Fonts fonts, FontStyle style, ReadOnlySpan<char> text, int at)
    {
        if (at <= 0 || at >= text.Length || text[at] < 256) return at;
        int start = TextLayer.ScanCluster(fonts, style, text, at, -1, out _);
        int next = TextLayer.ScanCluster(fonts, style, text, start, +1, out _);
        return next > at ? next : at;
    }

    /// <summary>
    /// The ranges after chars <paramref name="start"/>..<paramref name="end"/>
    /// were replaced by <paramref name="inserted"/> chars: those after the edit
    /// move with it, those it falls inside grow or shrink by it. Text typed
    /// where a range starts or ends goes outside it, a replacement's too.
    /// </summary>
    public static void Shift(Span<TextStyleRange> ranges, int start, int end, int inserted)
    {
        int delta = inserted - (end - start);
        if (delta == 0 && start == end) return;
        for (int k = 0; k < ranges.Length; k++)
        {
            var r = ranges[k];
            int newStart = r.Start < start ? r.Start
                : r.Start >= end ? r.Start + delta
                : start + inserted;
            int newEnd = r.End <= start ? r.End
                : r.End >= end ? r.End + delta
                : start + inserted;
            ranges[k] = r with { Start = newStart, Length = Math.Max(0, newEnd - newStart) };
        }
    }
}
