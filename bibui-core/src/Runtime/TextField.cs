using System.Numerics;
using Bibui.Core;
using Bibui.Core.TextEdit;
using Bibui.Core.Theming;
using static Bibui.Core.Runtime.TextField;

namespace Bibui.Core.Runtime;

/// <summary>
/// Text fields drawn from the caller's buffer and edit state. Each is a box of
/// its own, retained under its id inside the box the caller pushed; it claims
/// and asks for what it takes, and returns what happened. It owns no state:
/// while focused it applies the frame's text events, as <see cref="TextOp"/> says.
/// </summary>
public static class TextField
{
    internal static readonly UiColor Highlight = UiColor.Rgba(59, 130, 246, 110);
    internal const float CaretWidth = 2f;
    internal const string Dots = "\u2026";
    internal const float DotsGap = 2f;   // between a cut line and its "…"

    // The events a field takes while focused, as bits of their kinds. Read-only
    // fields take what never changes the text.
    public const uint SingleLineReadOnlyEvents = 1u << (int)BibUiEventKind.Navigate
                                               | 1u << (int)BibUiEventKind.Copy;
    public const uint MultiLineReadOnlyEvents = SingleLineReadOnlyEvents;
    public const uint SingleLineEvents = SingleLineReadOnlyEvents
                                       | 1u << (int)BibUiEventKind.Text
                                       | 1u << (int)BibUiEventKind.Edit;
    public const uint MultiLineEvents = SingleLineEvents | 1u << (int)BibUiEventKind.Submit;




    /// <summary>The height of <paramref name="rows"/> lines in the font in scope: what to push for a multi-line field of a set height.</summary>
    public static SemanticSize Rows(BibuiRuntime runtime, int rows)
    {
        Font font = runtime.FontInScope();
        return SemanticSize.Pixels(runtime.Fonts.LineHeight(font) + (Math.Max(1, rows) - 1) * runtime.Fonts.LineAdvance(font));
    }

    /// <summary>
    /// The look of an input on a field's box: padded and outlined, the outline
    /// the theme's focus ring while edited, the destructive colour on an
    /// <paramref name="error"/>. Widths, the ring and the padding above and
    /// below the text are <paramref name="runtime"/>'s theme's.
    /// </summary>
    public static void InputStyle(BibuiRuntime runtime, Box box, bool focused, bool error = false, bool disabled = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        InputStyle(runtime.Theme, box, focused, error, disabled);
    }

    /// <summary>
    /// <see cref="InputStyle(BibuiRuntime, Box, bool, bool, bool)"/> under the
    /// default theme's widths, focus ring and density, whatever theme is mounted.
    /// </summary>
    [Obsolete("Pass the runtime, so the theme's border width, focus ring and density reach the field: InputStyle(runtime, box, ...).")]
    public static void InputStyle(Box box, bool focused, bool error = false, bool disabled = false)
        => InputStyle(DefaultTheme, box, focused, error, disabled);

    internal static readonly ThemeTokens DefaultTheme = new();

    internal static void InputStyle(ThemeTokens theme, Box box, bool focused, bool error, bool disabled)
    {
        ArgumentNullException.ThrowIfNull(box);
        // The ring is drawn as the outline whatever its offset: one standing
        // clear of the edge is not drawn yet.
        bool ring = focused && !disabled && !error;
        box.Style.Padding = EdgeInsets.Symmetric(horizontal: 12f, vertical: theme.Density.Of(10f));
        box.Style.CornerRadius = 6f;
        box.Style.BorderWidth = ring ? theme.FocusRing.Width : theme.Borders.Width;
        box.Style.BorderColor = disabled ? UiColor.Border
            : error ? UiColor.Destructive
            : focused ? theme.FocusRing.Color
            : UiColor.Border;
        box.Style.BackgroundColor = disabled ? UiColor.SurfaceRaised : UiColor.Surface;
    }

    // The char boundary nearest x on one line, x logical px from the line's
    // start: the cluster under x, and the boundary before it when x is in its
    // left half, after it when in its right half; 0 at or before the start, the
    // line's length past its end. The clusters are the text layer's, as the walk
    // sets them (ScanCluster).
    internal static int CharAtX(Fonts fonts, Font font, ReadOnlySpan<char> line, float x)
    {
        if (x <= 0f || line.IsEmpty) return 0;
        var style = TextLayer.StyleFromFont(fonts, font, font.Scale);
        float target = x * font.Scale;   // the text layer measures in physical px
        float pen = 0f;                  // where the cluster at `at` starts
        int at = 0;
        while (at < line.Length)
        {
            int end = TextLayer.ScanCluster(fonts, style, line, at, +1, out float width);
            if (pen + width / 2f > target) return at;
            pen += width;
            at = end;
        }
        return line.Length;
    }

    // How wide chars are on one line, in logical px, as layout's walk sets them
    // (TextLayer.WidthFromChars): what the field's Text parts are laid out and drawn at.
    internal static float WidthOfChars(Fonts fonts, Font font, ReadOnlySpan<char> chars)
        => TextLayer.WidthFromChars(fonts, chars, TextLayer.StyleFromFont(fonts, font, font.Scale), font.Scale);

    // The pointer on one line of text: line holds its chars, x is from where it
    // starts, and the edit state is in the line's offsets. A press puts the
    // cursor at the text element under it and sets the mark, unless shift extends a
    // field already being edited; held, the cursor follows the pointer.
    internal static void HandlePointer(BibuiRuntime runtime, Font font, Interaction interaction,
        ReadOnlySpan<char> line, TextEditState edit, float x, bool focused)
    {
        int at = CharAtX(runtime.Fonts, font, line, x);
        bool extend = focused && (interaction.Modifiers & KeyModifiers.Shift) != 0;

        // Double click highlights word, and dragged selects by words: the
        // cursor goes to the edge of the word under the pointer that is
        // away from the mark.
        if (interaction.PressCount > 1)
        {
            var style = TextLayer.StyleFromFont(runtime.Fonts, font, font.Scale);
            int start = TextLayer.ScanWord(runtime.Fonts, style, line, at, -1);
            int end = TextLayer.ScanWord(runtime.Fonts, style, line, start, +1);
            if (interaction.Pressed) { edit.Mark = start; edit.Cursor = end; }
            else edit.Cursor = at < edit.Mark ? start : end;
        }
        else
        {
            edit.Cursor = at;
            if (interaction.Pressed && !extend) edit.Mark = at;
        }
    }

    // One line of text, drawn as texts side by side split at the selection —
    // [lo, hi) in the line's offsets — the selected one in a box with the
    // highlight behind it, so layout puts the highlight under its glyphs.
    internal static void DrawLine(BibuiRuntime runtime, ReadOnlySpan<char> line, int lo, int hi,
        TextColor color)
    {
        if (lo > 0) Part(runtime, line[..lo], color);
        if (hi > lo)
        {
            Box selection = runtime.Box();
            selection.ChildLayoutAxis = LayoutAxis.X;
            selection.SizeX = SemanticSize.ChildrenSum();
            selection.SizeY = SemanticSize.ParentPct(1f);
            selection.Style.BackgroundColor = Highlight;
            using (runtime.Parent(selection)) Part(runtime, line[lo..hi], color);
        }
        if (hi < line.Length) Part(runtime, line[hi..], color);
    }

    // A slice of the line, sized by its text whatever size is pushed.
    internal static void Part(BibuiRuntime runtime, ReadOnlySpan<char> text, TextColor color)
    {
        using (runtime.SizeX(SemanticSize.Text()))
        using (runtime.SizeY(SemanticSize.Text()))
            runtime.Text(text, textColor: color);
    }

    // Moves the view's target so the caret, the box [at, at + size), is in
    // sight with step beyond it, without passing end. The view's clamp keeps
    // it from going before the start.
    internal static void ScrollToCaret(Box view, Vector2 at, Vector2 size, Vector2 end, Vector2 step)
    {
        Vector2 room = view.Extent;
        Vector2 far = at + size;
        Vector2 target = view.TargetViewOffset;

        if (room.X > 0f && far.X - target.X > room.X) target.X = far.X - room.X + MathF.Min(step.X, end.X - far.X);
        else if (at.X < target.X) target.X = at.X - step.X;

        if (room.Y > 0f && far.Y - target.Y > room.Y) target.Y = far.Y - room.Y + MathF.Min(step.Y, end.Y - far.Y);
        else if (at.Y < target.Y) target.Y = at.Y - step.Y;

        view.TargetViewOffset = target;
    }

    // The caret on a line top below the first, after the text before it. It
    // glides there, but jumps right after an edit so it never trails a typed character.
    internal static void DrawCaret(BibuiRuntime runtime, Box view, Font font, ReadOnlySpan<char> line,
        int lo, int hi, int caret, float top, SemanticSize height, bool edited)
    {
        float before = WidthOfChars(runtime.Fonts, font, line[..lo]);
        if (caret == hi && hi > lo) before += WidthOfChars(runtime.Fonts, font, line[lo..hi]);

        // The caret follows the cursor: 60 halvings a second by default, half the way each frame at 60 Hz.
        float rate = edited ? 1f : runtime.Rates.Follow;
        float easedX = runtime.Animate(view.Id!, "caret-x", before, rate);
        float easedY = runtime.Animate(view.Id!, "caret-y", top, rate);
        float x = edited ? before : easedX, y = edited ? top : easedY;
        Box bar = runtime.Box();
        bar.SizeX = SemanticSize.Pixels(CaretWidth);
        bar.SizeY = height;
        bar.At(x - CaretWidth / 2f, y);
        bar.Style.BackgroundColor = UiColor.TextPrimary;
    }

    // Applies and eats the frame's events of the given kinds, in order. A move
    // between lines is the field's; the rest is TextOp's. Answers whether the
    // text changed.
    internal static bool ApplyEdits(BibuiRuntime runtime, BibuiStr text, TextEditState edit, uint kinds,
        Font font, float wrapWidth, TextWrap wrap)
    {
        bool edited = false;
        Span<char> charBuf = stackalloc char[TextOp.MaxCharsPerCodePoint];
        foreach (var ev in runtime.UnconsumedEvents)
        {
            if ((kinds & 1u << (int)ev.Kind) == 0) continue;
            if (ev.Kind == BibUiEventKind.Navigate && ev.DeltaY != 0)
            {
                MoveVertically(runtime, font, text, edit, wrapWidth, wrap, ev);
                ev.Eat();
                continue;
            }
            var op = TextOp.FromStateAndEvent(runtime.Fonts, font, text, edit, ev, charBuf);
            if (op.Range.Item1 != op.Range.Item2 || op.ReplaceString.Length > 0) edited = true;
            text.ReplaceRange(op.Range, op.ReplaceString);
            edit.Cursor = op.NewCursor;
            edit.Mark = op.NewMark;
            if (op.CopyString.Length > 0) runtime.ClipboardSet(new string(op.CopyString));
            ev.Eat();
        }
        return edited;
    }

    // Up or down a line, by the sign of ev.DeltaY, to the char nearest the
    // caret's x on that line. With no line there — above the first, below the
    // last, or anywhere in a single line — the start or end of the text.
    // Shift keeps the mark.
    internal static void MoveVertically(BibuiRuntime runtime, Font font, BibuiStr text, TextEditState edit,
        float wrapWidth, TextWrap wrap, BibUiEvent ev)
    {
        var fonts = runtime.Fonts;
        var style = TextLayer.StyleFromFont(fonts, font, font.Scale);
        var lines = TextLayer.LinesFromText(fonts, text.Str, style, font.Scale, wrapWidth, wrap);
        int on = LineOf(lines, edit.Cursor);
        int to = on + Math.Sign(ev.DeltaY);
        if (to < 0 || to >= lines.Length)
        {
            edit.Cursor = ev.DeltaY > 0 ? (int)text.Length : 0;
        }
        else
        {
            float caretX = WidthOfChars(fonts, font, text.Str[lines[on].Start..edit.Cursor]);
            edit.Cursor = lines[to].Start + CharAtX(fonts, font, text.Str[lines[to].Start..lines[to].End], caretX);
        }
        if ((ev.Flags & TextActionFlags.KeepMark) == 0) edit.Mark = edit.Cursor;
    }

    // The line an offset is on: the last that starts at or before it.
    internal static int LineOf(ReadOnlySpan<TextLine> lines, int at)
    {
        int k = 0;
        while (k + 1 < lines.Length && at >= lines[k + 1].Start) k++;
        return k;
    }
}

/// <summary>The TextField component, called on the runtime that builds it.</summary>
public static class TextFieldExtensions
{
    /// <summary>
    /// One line of <paramref name="text"/>, the field named <paramref name="id"/>
    /// in the box in scope, edited while <paramref name="focused"/>. A line tall,
    /// cut at the box's edge, and scrolled a quarter of its width at a time to
    /// keep the caret in sight.
    /// <paramref name="events"/>: <see cref="SingleLineEvents"/> to edit,
    /// <see cref="SingleLineReadOnlyEvents"/> to select, none to only show it.
    /// </summary>
    public static BibuiWidget TextField(this BibuiRuntime runtime, string id, BibuiStr text,
        TextEditState edit, bool focused, uint events = global::Bibui.Core.Runtime.TextField.SingleLineEvents,
        TextColor textColor = TextColor.Primary, ReadOnlySpan<char> placeholder = default)
    {
        Font font = runtime.FontInScope();
        float lineHeight = runtime.Fonts.LineHeight(font);

        // The field's own view: a line tall, cut at its edge, scrolling along the line.
        var claims = InteractionKinds.Clip | InteractionKinds.ClampX
            | (events != 0 ? InteractionKinds.LeftPress : InteractionKinds.None);
        Box view = runtime.BoxFromId(id, claims);
        Interaction interaction = runtime.InteractionsFor(view);
        view.ChildLayoutAxis = LayoutAxis.X;
        view.SizeX = SemanticSize.ParentPct(1f);
        view.SizeY = SemanticSize.Pixels(lineHeight);
        view.AllowOverflowX = true;

        if (interaction.Pressed || interaction.Dragging)
        {
            float pointerX = interaction.X - view.Rect.X + view.ViewOffset.X;
            HandlePointer(runtime, font, interaction, text.Str, edit, pointerX, focused);
        }

        bool edited = focused && ApplyEdits(runtime, text, edit, events, font, float.PositiveInfinity, TextWrap.Words);

        // An empty buffer shows the placeholder, in a quieter colour, the same way.
        bool empty = text.Length == 0;
        ReadOnlySpan<char> shown = empty ? placeholder : text.Str;
        TextColor color = empty ? TextColor.Secondary : textColor;

        // Not focused: no caret and nothing highlighted. Empty: the caret is
        // before the placeholder, which is not text to select.
        int caret = focused && !empty ? Math.Clamp(edit.Cursor, 0, shown.Length) : 0;
        int mark = focused && !empty ? Math.Clamp(edit.Mark, 0, shown.Length) : 0;
        int lo = Math.Min(caret, mark), hi = Math.Max(caret, mark);

        // Edited, the caret is kept in sight; otherwise the line shows from its start.
        if (!focused || empty)
            view.TargetViewOffset = view.TargetViewOffset with { X = 0f };
        else
            ScrollToCaret(view, new Vector2(WidthOfChars(runtime.Fonts, font, shown[..caret]), 0f), Vector2.Zero,
                new Vector2(WidthOfChars(runtime.Fonts, font, shown), 0f), view.Extent / 4f);

        using (runtime.Parent(view))
        {
            DrawLine(runtime, shown, lo, hi, color);
            // Draw carret last so it appears over text
            if (focused) DrawCaret(runtime, view, font, shown, lo, hi, caret, top: 0f, SemanticSize.Pixels(lineHeight), edited);
        }
        return new BibuiWidget(view, interaction);
    }

    /// <summary>
    /// <paramref name="text"/> as lines, the field named <paramref name="id"/> in
    /// the box in scope, wrapped at its width and edited while <paramref name="focused"/>.
    /// Its height is the size pushed on the stack — <c>ChildrenSum()</c>,
    /// <see cref="Rows"/> or a share — and the wheel scrolls past it. Lines
    /// break as <paramref name="wrap"/> says: between words, or anywhere.
    /// </summary>
    public static BibuiWidget MultilineTextField(this BibuiRuntime runtime, string id, BibuiStr text,
        TextEditState edit, bool focused, uint events = global::Bibui.Core.Runtime.TextField.MultiLineEvents,
        TextColor textColor = TextColor.Primary, ReadOnlySpan<char> placeholder = default, TextWrap wrap = TextWrap.Words)
    {
        Font font = runtime.FontInScope();
        var style = TextLayer.StyleFromFont(runtime.Fonts, font, font.Scale);

        // The field's own view: as tall as the size pushed, scrolling, cut at its edge.
        var claims = InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip
            | (events != 0 ? InteractionKinds.LeftPress : InteractionKinds.None);
        Box view = runtime.BoxFromId(id, claims);
        Interaction interaction = runtime.InteractionsFor(view);
        view.ChildLayoutAxis = LayoutAxis.Y;
        view.SizeX = SemanticSize.ParentPct(1f);
        view.AllowOverflowY = true;
        int caretBefore = edit.Cursor;
        float lineHeight = runtime.Fonts.LineHeight(font);
        float lineAdvance = runtime.Fonts.LineAdvance(font);
        // Wrapped at last frame's width; unwrapped before the first layout.
        float room = view.Extent.X > 0f ? view.Extent.X : float.PositiveInfinity;

        if (interaction.Pressed || interaction.Dragging)
        {
            // The line under the pointer, and the pointer on it as on a single
            // line: the edit state moved into the line's offsets, and back.
            float pointerX = interaction.X - view.Rect.X;   // from where the text starts
            float pointerY = interaction.Y - view.Rect.Y + view.ViewOffset.Y;   // in the lines, scrolled
            int lineUnderPointer = (int)MathF.Floor(pointerY / lineAdvance);
            var pointerLines = TextLayer.LinesFromText(runtime.Fonts, text.Str, style, font.Scale, room, wrap);
            var line = pointerLines[Math.Clamp(lineUnderPointer, 0, pointerLines.Length - 1)];
            int lineStart = line.Start;
            ReadOnlySpan<char> lineText = text.Str[line.Start..line.End];

            edit.Cursor -= lineStart; edit.Mark -= lineStart;
            HandlePointer(runtime, font, interaction, lineText, edit, pointerX, focused);
            edit.Cursor += lineStart; edit.Mark += lineStart;
        }

        bool edited = focused && ApplyEdits(runtime, text, edit, events, font, room, wrap);

        // An empty buffer shows the placeholder, in a quieter colour, the same way.
        bool empty = text.Length == 0;
        ReadOnlySpan<char> shown = empty ? placeholder : text.Str;
        TextColor color = empty ? TextColor.Secondary : textColor;
        var lines = TextLayer.LinesFromText(runtime.Fonts, shown, style, font.Scale, room, wrap);

        // Not focused: no caret and nothing highlighted. Empty: the caret is
        // before the placeholder, which is not text to select.
        int caret = focused && !empty ? Math.Clamp(edit.Cursor, 0, shown.Length) : 0;
        int mark = focused && !empty ? Math.Clamp(edit.Mark, 0, shown.Length) : 0;
        int lo = Math.Min(caret, mark), hi = Math.Max(caret, mark);

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
            // The selection's part on this line, in the line's offsets.
            int selectionStart = Math.Clamp(lo, line.Start, line.End) - line.Start;
            int selectionEnd = Math.Clamp(hi, line.Start, line.End) - line.Start;
            using (runtime.Parent(row))
                DrawLine(runtime, shown[line.Start..line.End], selectionStart, selectionEnd, color);
        }

        // Draw carret last so it appears over text: on its line as on a single line, at that line's place.
        if (focused)
        {
            int caretLineIndex = Math.Max(0, caretLine);
            int lineStart = lines[caretLineIndex].Start, lineEnd = lines[caretLineIndex].End;
            // The caret and the selection in the caret's line's offsets.
            int selectionStart = Math.Clamp(lo, lineStart, lineEnd) - lineStart;
            int selectionEnd = Math.Clamp(hi, lineStart, lineEnd) - lineStart;
            int caretOnLine = caret - lineStart;
            float lineTop = caretLineIndex * lineAdvance;
            var caretHeight = SemanticSize.Pixels(lineHeight);
            DrawCaret(runtime, view, font, shown[lineStart..lineEnd], selectionStart, selectionEnd,
                caretOnLine, lineTop, caretHeight, edited);

            // Its line is kept in sight when it moves, so a wheel scroll is left alone.
            if (!empty && (edited || edit.Cursor != caretBefore))
                ScrollToCaret(view, new Vector2(0f, lineTop), new Vector2(0f, lineHeight),
                    new Vector2(0f, lineHeight + Math.Max(0, lines.Length - 1) * lineAdvance), new Vector2(0f, lineHeight));
        }
        return new BibuiWidget(view, interaction);
    }

    /// <summary>
    /// A <see cref="SingleLine"/> named <paramref name="id"/> in the box in scope,
    /// ending in "…" when it is wider than the box: the line gives up room for
    /// the "…". Edited, it scrolls instead. The box must not be sized by its
    /// children, and whether the line fits is told by its width last frame.
    /// </summary>
    public static BibuiWidget EllipsisTextField(this BibuiRuntime runtime, string id, BibuiStr text,
        TextEditState edit, bool focused, uint events = 0,
        TextColor textColor = TextColor.Primary, ReadOnlySpan<char> placeholder = default)
    {
        UiNode box = runtime.ParentInScope;   // holds the line, and the "…" when it is cut
        float room = box.Extent.X > 0f
            ? box.Extent.X - box.Style.Padding.Left - box.Style.Padding.Right
            : float.PositiveInfinity;
        box.ChildLayoutAxis = LayoutAxis.X;

        var line = runtime.TextField(id, text, edit, focused, events, textColor, placeholder);
        bool empty = text.Length == 0;
        ReadOnlySpan<char> shown = empty ? placeholder : text.Str;
        if (focused || WidthOfChars(runtime.Fonts, runtime.FontInScope(), shown) <= room)
            return line;   // fits, or being edited: it scrolls instead

        runtime.Gap(DotsGap);
        using (runtime.SizeX(SemanticSize.Text()))
        using (runtime.SizeY(SemanticSize.Text()))
            runtime.Text(Dots, textColor: empty ? TextColor.Secondary : textColor);
        return line;
    }
}
