using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.TextEdit;
using Bibui.Testing;
using Bibui.Text;

/// <summary>
/// runtime.StyledTextField: a multi-line field whose ranges are drawn in their
/// colours and weights, broken into lines, measured and hit by the text layer
/// with each range in its own face, and kept in step with the field's own edits.
/// </summary>
static class StyledTextFieldTests
{
    public static void Register(Action<string, Action> run)
    {
        run("StyledTextField: with no ranges, or only colours, it breaks and draws as MultilineTextField", () =>
        {
            const string words = "Some words, wrapped at the width of the field and then some more of them.";
            var plain = Frame(words, null, width: 220f);
            Same(plain, Frame(words, [], width: 220f), "no ranges");
            var coloured = Field(words, [new TextStyleRange(5, 6, UiColor.Accent)], width: 220f).Runtime;
            Equal(Lines(Field(words, null, 220f).Runtime), Lines(coloured), "a colour moves no line");
        });

        run("StyledTextField: ranges that only colour are measured as plain text", () =>
        {
            var runtime = BibuiRuntime.Create();
            var font = runtime.FontInScope();
            TextStyleRange[] coloured = [new(0, 4, UiColor.Accent), new(5, 3, UiColor.TextSecondary)];
            var styled = new StyledText(runtime, "some text", font, coloured, [], []);
            IsTrue(styled.Plain, "no runs: the plain functions and their cache");
        });

        run("StyledTextField: a range is drawn in its weight and colour, the rest as the field's", () =>
        {
            var (runtime, _, _) = Field("a bold word, an aside", [new TextStyleRange(2, 4, UiColor.Accent, FontWeight.Bold), new TextStyleRange(16, 5, UiColor.TextSecondary)], 400f);
            var texts = Texts(runtime);
            var bold = texts.Single(t => new string(t.Content) == "bold");
            var aside = texts.Single(t => new string(t.Content) == "aside");
            Equal(FontWeight.Bold, bold.Weight, "the range's weight");
            Equal<UiColor?>(UiColor.Accent, bold.Color, "and colour");
            Equal<UiColor?>(UiColor.TextSecondary, aside.Color, "a range that only colours");
            IsTrue(texts.Where(t => t != bold && t.Content.Length > 0).All(t => t.Weight == FontWeight.Regular), "the rest regular");
        });

        run("StyledTextField: lines break at the widths the ranges are drawn at", () =>
        {
            const string words = "wide wide wide wide wide wide";
            var runtime = BibuiRuntime.Create();
            float width = Width(runtime, words, runtime.FontInScope()) + 2f;
            Equal(1, Lines(Field(words, null, width).Runtime), "regular, one line");
            Equal(2, Lines(Field(words, [new TextStyleRange(0, words.Length, Weight: FontWeight.Bold)], width).Runtime), "bold, two");
        });

        run("StyledTextField: CRLF and character wrapping take ranges too", () =>
        {
            var styles = new[] { new TextStyleRange(0, 3, Weight: FontWeight.Bold) };
            Equal(2, Lines(Field("one\r\ntwo", styles, 400f).Runtime), "a CRLF ends a line");
            var runtime = BibuiRuntime.Create();
            float width = Width(runtime, "mmmm", runtime.FontInScope()) + 1f;
            Equal(3, Lines(Field("mmmmmmmmmmmm", null, width, wrap: TextWrap.Characters).Runtime), "regular, four Ms a line");
            Equal(4, Lines(Field("mmmmmmmmmmmm", [new TextStyleRange(0, 12, Weight: FontWeight.Bold)], width, wrap: TextWrap.Characters).Runtime),
                "bold, fewer a line");
        });

        run("StyledTextField: the caret after a bold range stands at its end", () =>
        {
            var edit = new TextEditState { Cursor = 6, Mark = 6 };
            var (runtime, _, _) = Field("a bold word", [new TextStyleRange(2, 4, Weight: FontWeight.Bold)], 400f, edit, focused: true);
            var bold = Texts(runtime).Single(t => new string(t.Content) == "bold");
            var caret = Caret(runtime);
            float centre = caret.Rect.X + caret.Extent.X / 2f;
            IsTrue(MathF.Abs(centre - (bold.Rect.X + bold.Extent.X)) < 0.5f, $"the caret at {centre}, the range ending at {bold.Rect.X + bold.Extent.X}");
        });

        run("StyledTextField: an italic range is drawn in italic and measured by its own widths", () =>
        {
            var edit = new TextEditState { Cursor = 9, Mark = 9 };
            var (runtime, _, _) = Field("an italic word", [new TextStyleRange(3, 6, Weight: FontWeight.Italic)], 400f, edit, focused: true);
            var italic = Texts(runtime).Single(t => new string(t.Content) == "italic");
            Equal(FontWeight.Italic, italic.Weight, "the range's weight");
            Font face;
            using (runtime.FontFace(weight: FontWeight.Italic)) face = runtime.FontInScope();
            float start = Width(runtime, "an ", runtime.FontInScope());
            float italicWidth = Width(runtime, "italic", face);
            IsTrue(italicWidth != Width(runtime, "italic", runtime.FontInScope()), "italic widths are its own");
            var caret = Caret(runtime);
            float centre = caret.Rect.X + caret.Extent.X / 2f - Texts(runtime)[0].Rect.X;
            IsTrue(MathF.Abs(centre - (start + italicWidth)) < 0.5f, $"the caret at {centre}, the italic range ending at {start + italicWidth}");
        });

        run("StyledTextField: a press inside a bold word lands by the bold widths", () =>
        {
            var edit = new TextEditState();
            var runtime = Clickable("WWWWWWWW word", [new TextStyleRange(0, 8, Weight: FontWeight.Bold)], 400f, edit);
            var bold = Texts(runtime).Single(t => new string(t.Content) == "WWWWWWWW");
            Click(runtime, bold.Rect.X + bold.Extent.X * 5.6f / 8f, bold.Rect.Y + 4f);   // well into the sixth W
            Equal(6, edit.Cursor, "after the sixth bold W");
        });

        run("StyledTextField: a press after a bold range lands in the regular text by its widths", () =>
        {
            var edit = new TextEditState();
            var runtime = Clickable("WWWW iiiiiiii", [new TextStyleRange(0, 4, Weight: FontWeight.Bold)], 400f, edit);
            var rest = Texts(runtime).Single(t => new string(t.Content) == " iiiiiiii");
            Click(runtime, rest.Rect.X + rest.Extent.X - 0.5f, rest.Rect.Y + 4f);
            Equal(13, edit.Cursor, "at the end of the line");
        });

        run("StyledTextField: spaces at the end of a line are the line's, as in a plain field", () =>
        {
            var edit = new TextEditState();
            var runtime = Clickable("word   \nnext", [new TextStyleRange(0, 4, Weight: FontWeight.Bold)], 400f, edit);
            var bold = Texts(runtime).Single(t => new string(t.Content) == "word");
            Click(runtime, bold.Rect.X + 300f, bold.Rect.Y + 4f);
            Equal(7, edit.Cursor, "a press right of the first line puts the caret after its spaces");
        });

        run("StyledTextField: Up and Down keep the caret's x across bold text", () =>
        {
            var edit = new TextEditState { Cursor = 8, Mark = 8 };   // after "WWWWWWWW" on the first line
            var (runtime, page, _) = Field("WWWWWWWW\n" + new string('i', 40), [new TextStyleRange(0, 8, Weight: FontWeight.Bold)], 400f, edit, focused: true);
            page.Press(Key.Down);
            float boldWidth = Texts(runtime).Single(t => new string(t.Content) == "WWWWWWWW").Extent.X;
            float below = Width(runtime, new string('i', edit.Cursor - 9), runtime.FontInScope());
            IsTrue(MathF.Abs(below - boldWidth) < Width(runtime, "i", runtime.FontInScope()), $"at {below} below a bold line {boldWidth} wide");
            page.Press(Key.Up);
            Equal(8, edit.Cursor, "and back to the end of the bold line");
        });

        run("StyledTextField: a selection across a range is highlighted over all of it", () =>
        {
            var edit = new TextEditState { Cursor = 9, Mark = 0 };
            var (runtime, _, _) = Field("a bold word", [new TextStyleRange(2, 4, Weight: FontWeight.Bold)], 400f, edit, focused: true);
            var selection = Below(runtime.FindById("field")!).OfType<Box>().Single(b => Equals(b.Style.BackgroundColor, (UiColor?)TextField.Highlight));
            Equal("a bold wo", string.Concat(Below(selection).OfType<Text>().Select(t => new string(t.Content))), "every piece it covers");
            IsTrue(Below(selection).OfType<Text>().Any(t => t.Weight == FontWeight.Bold), "the bold one among them");
        });

        run("StyledTextField: without focus, the selection is drawn dimmer and no caret", () =>
        {
            var edit = new TextEditState { Cursor = 6, Mark = 2 };   // "bold"
            var (runtime, _, _) = Field("a bold word", [new TextStyleRange(2, 4, Weight: FontWeight.Bold)], 400f, edit);
            var dimmer = TextField.Highlight.WithAlpha(55 / 255f);
            var behind = Below(runtime.FindById("field")!).OfType<Box>().Select(b => b.Style.BackgroundColor).OfType<UiColor>()
                .Where(c => c.Equals(dimmer) || c.Equals(TextField.Highlight)).ToList();
            Equal(1, behind.Count, "one selection box");
            IsTrue(behind[0].Equals(dimmer), "in the dimmer blue");
            IsTrue(!Below(runtime.FindById("field")!).OfType<Box>().Any(b => b.Extent.X == TextField.CaretWidth), "no caret");
        });

        run("StyledTextField: an edit the field makes moves the ranges with it", () =>
        {
            var edit = new TextEditState();
            var (runtime, page, text) = Field("a bold word", [new TextStyleRange(2, 4, Weight: FontWeight.Bold)], 400f, edit, focused: true);
            page.TypeChar('X');
            Equal("Xa bold word", text.ToString(), "the text");
            Equal("bold", new string(Texts(runtime).Single(t => t.Weight == FontWeight.Bold).Content), "what is drawn bold");
        });

        run("StyledTextField: an edit shifts the caller's ranges in place", () =>
        {
            var edit = new TextEditState { Cursor = 2, Mark = 2 };
            TextStyleRange[] ranges = [new(0, 1, UiColor.Accent), new(2, 4, Weight: FontWeight.Bold), new(7, 4, UiColor.Accent)];
            var (_, page, _) = Field("a bold word", ranges, 400f, edit, focused: true);
            page.TypeChar('X');
            Equal(new TextStyleRange(0, 1, UiColor.Accent), ranges[0], "before the edit: where it was");
            Equal(new TextStyleRange(3, 4, Weight: FontWeight.Bold), ranges[1], "typed at its start: moved past the X");
            Equal(new TextStyleRange(8, 4, UiColor.Accent), ranges[2], "after it: moved too");
        });

        run("StyledTextField: an empty range left by an edit draws nothing", () =>
        {
            var edit = new TextEditState { Cursor = 6, Mark = 2 };   // "bold" selected
            var (runtime, page, text) = Field("a bold word", [new TextStyleRange(2, 4, Weight: FontWeight.Bold)], 400f, edit, focused: true);
            page.PressBackspace();
            Equal("a  word", text.ToString(), "the bold word deleted");
            IsTrue(Texts(runtime).All(t => t.Content.Length > 0 || t.Weight != FontWeight.Bold), "no empty bold text left");
        });

        run("StyledTextField: text inserted where a range starts or ends goes outside it; a deletion across it shrinks it", () =>
        {
            TextStyleRange[] ranges = [new(2, 4)];
            StyledText.Shift(ranges, 2, 2, 1);
            Equal(new TextStyleRange(3, 4), ranges[0], "typed at its start: it moves");
            StyledText.Shift(ranges, 7, 7, 1);
            Equal(new TextStyleRange(3, 4), ranges[0], "typed at its end: it stays");
            StyledText.Shift(ranges, 4, 4, 2);
            Equal(new TextStyleRange(3, 6), ranges[0], "typed inside: it grows");
            StyledText.Shift(ranges, 1, 5, 0);
            Equal(new TextStyleRange(1, 4), ranges[0], "a deletion over its start");
            StyledText.Shift(ranges, 0, 9, 0);
            Equal(0, ranges[0].Length, "deleted with it: empty");
        });

        run("StyledTextField: a replacement where a range starts puts the new text outside it", () =>
        {
            TextStyleRange[] ranges = [new(5, 5)];
            StyledText.Shift(ranges, 5, 7, 1);
            Equal(new TextStyleRange(6, 3), ranges[0], "two chars at its start replaced by one: the one before it");
            ranges = [new(5, 5)];
            StyledText.Shift(ranges, 5, 12, 1);
            Equal(0, ranges[0].Length, "the whole range replaced: empty");
        });

        run("StyledTextField: ranges unsorted, overlapping or past the text break the contract the field asserts", () =>
        {
            const int length = 10;
            IsTrue(StyledText.InOrder([new(0, 3), new(3, 0), new(3, 2), new(8, 2)], length), "sorted, touching, an empty one, to the end");
            IsTrue(StyledText.InOrder([], length), "none");
            IsTrue(!StyledText.InOrder([new(5, 2), new(0, 3)], length), "unsorted");
            IsTrue(!StyledText.InOrder([new(0, 4), new(3, 2)], length), "overlapping");
            IsTrue(!StyledText.InOrder([new(8, 3)], length), "past the text");
            IsTrue(!StyledText.InOrder([new(-1, 2)], length), "before it");
        });

        run("StyledTextField: a range that would split a cluster takes all of it, after an edit too", () =>
        {
            var runtime = BibuiRuntime.Create();
            runtime.Fonts.UseClusterFont(Bibui.Text.FreeType.FontData.FromFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoColorEmoji-subset.ttf")));
            const string text = "ok 👍🏽 yes";   // the emoji is four chars, two code points: one cluster
            const string thumb = "👍🏽";
            int at = text.IndexOf(thumb, StringComparison.Ordinal);
            TextStyleRange[] given = [new(at, 2)];
            StyledText.Snap(runtime.Fonts, runtime.FontInScope(), text, given);
            Equal(new TextStyleRange(at, thumb.Length), given[0], "the thumb and its skin tone together");

            TextStyleRange[] ranges = [new(0, 1)];
            StyledText.Snap(runtime.Fonts, runtime.FontInScope(), "é and", ranges);   // a combining mark typed at its end
            Equal(new TextStyleRange(0, 2), ranges[0], "the mark joins the range");
        });

        run("StyledTextField: Page Down moves a page of lines, the view with it, bold lines and all", () =>
        {
            const int count = 50, rows = 10;
            var state = new StyledTextState();
            var edit = state.Edit;
            string words = string.Join("\n", Enumerable.Range(1, count).Select(i => $"line {i:00}"));
            int stride = "line 00\n".Length;
            var styles = Enumerable.Range(0, count).Select(k => new TextStyleRange(k * stride, "line".Length, Weight: FontWeight.Bold)).ToArray();
            var text = BibuiStr.FromStrLit(words);
            var runtime = BibuiRuntime.Create();
            Box view = null!;
            using var page = BibuiPage.From(runtime, () =>
            {
                using (runtime.Parent(runtime.Column()))
                using (runtime.Parent(runtime.Column().Sized(Size.Px(300f))))
                using (runtime.SizeY(TextField.Rows(runtime, rows)))
                {
                    runtime.Focus("field");
                    view = (Box)runtime.StyledTextField("field", text, state, styles).Node;
                }
            }, window: new WindowFrame(400, 600, 1f, 60f));
            page.Frame();
            page.Frame();
            page.Press(Key.PageDown);
            Equal((rows - 1) * stride, edit.Cursor, "a page of ten rows, less one");
            float advance = runtime.Fonts.LineAdvance(runtime.FontInScope());
            IsTrue(MathF.Abs(view.TargetViewOffset.Y - (rows - 1) * advance) < 0.01f, $"the view a page down: {view.TargetViewOffset.Y}");
            page.Press(Key.PageUp);
            Equal(0, edit.Cursor, "and a page back up");
        });

        run("StyledTextField: Page Down counts the lines bold text wraps into", () =>
        {
            const string words = "wide wide wide wide wide wide wide wide";
            var runtime = BibuiRuntime.Create();
            float width = Width(runtime, words, runtime.FontInScope()) + 2f;
            var edit = new TextEditState();
            var (styled, page, _) = Field(words, [new TextStyleRange(0, words.Length, Weight: FontWeight.Bold)], width, edit, focused: true, rows: 1);
            Equal(2, Lines(styled), "bold, two lines");
            page.Press(Key.PageDown);
            IsTrue(edit.Cursor > 0 && edit.Cursor < words.Length, $"a page of one row goes to the second line, not the end: {edit.Cursor}");
        });

        run("StyledTextField: in a steady frame, the styled field allocates nothing", () =>
        {
            const int repeats = 40;
            const string phrase = "some bold words and more of them, ";
            string words = string.Concat(Enumerable.Repeat(phrase, repeats));
            int bold = phrase.IndexOf("bold", StringComparison.Ordinal), more = phrase.IndexOf("more", StringComparison.Ordinal);
            var styles = Enumerable.Range(0, repeats).SelectMany(k => new[]
            {
                new TextStyleRange(k * phrase.Length + bold, "bold".Length, UiColor.TextSecondary, FontWeight.Bold),
                new TextStyleRange(k * phrase.Length + more, "more".Length, UiColor.Accent),
            }).ToArray();
            Equal(0L, SteadyFrame(words, styles), "bytes the styled field allocated");
        });
    }

    // The bytes the field's call allocates in a steady frame, its state and
    // ranges the same as the frames before; the frame around it is the runtime's.
    private static long SteadyFrame(string words, TextStyleRange[] styles)
    {
        var text = BibuiStr.FromStrLit(words);
        var runtime = BibuiRuntime.Create();
        var state = new StyledTextState();
        long allocated = 0;
        using var page = BibuiPage.From(runtime, () =>
        {
            using (runtime.Parent(runtime.Column()))
            using (runtime.Parent(runtime.Column().Sized(Size.Px(300f))))
            using (runtime.SizeY(SemanticSize.ChildrenSum()))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                runtime.StyledTextField("field", text, state, styles);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }, window: new WindowFrame(400, 2000, 1f, 60f));
        for (int k = 0; k < 4; k++) page.Frame();
        page.Frame();
        return allocated;
    }

    private static (BibuiRuntime Runtime, BibuiPage Page, BibuiStr Text) Field(string words, TextStyleRange[]? styles, float width,
        TextEditState? edit = null, bool focused = false, TextWrap wrap = TextWrap.Words, int? rows = null)
    {
        var text = BibuiStr.FromStrLit(words);
        var runtime = BibuiRuntime.Create();
        var state = new StyledTextState { Edit = edit ?? new TextEditState() };
        var page = BibuiPage.From(runtime, () => Build(runtime, text, styles, width, state, focused, wrap, rows),
            window: new WindowFrame(500, 300, 1f, 60f));
        page.Frame();
        page.Frame();
        return (runtime, page, text);
    }

    private static Pixels Frame(string words, TextStyleRange[]? styles, float width)
    {
        var (runtime, page, _) = Field(words, styles, width);
        using (page) return runtime.LastFrame();
    }

    // The styled field, or MultilineTextField when styles is null; as tall as
    // its lines, or as rows of them.
    private static void Build(BibuiRuntime runtime, BibuiStr text, TextStyleRange[]? styles, float width,
        StyledTextState state, bool focused, TextWrap wrap, int? rows)
    {
        using (runtime.Parent(runtime.Column()))
        using (runtime.Parent(runtime.Column().Sized(Size.Px(width))))
        using (runtime.SizeY(rows is { } n ? TextField.Rows(runtime, n) : SemanticSize.ChildrenSum()))
        {
            if (focused) runtime.Focus("field");
            if (styles is not null) runtime.StyledTextField("field", text, state, styles, wrap: wrap);
            else runtime.MultilineTextField("field", text, state.Edit, wrap: wrap);
        }
    }

    // A focused field on a runtime of its own build, laid out, for presses at a point.
    private static BibuiRuntime Clickable(string words, TextStyleRange[] styles, float width, TextEditState edit)
    {
        var text = BibuiStr.FromStrLit(words);
        var runtime = BibuiRuntime.Create();
        var state = new StyledTextState { Edit = edit };
        runtime.SetBuild(() => runtime.Into(runtime.Column(), () => Build(runtime, text, styles, width, state, focused: true, TextWrap.Words, null)),
            new WindowFrame(500, 300, 1f, 60f));
        runtime.Step();
        runtime.Step();
        return runtime;
    }

    private static void Click(BibuiRuntime runtime, float x, float y)
    {
        runtime.Step([WindowEvent.Mouse(WindowEventKind.MouseMove, x, y, MouseButton.Unknown), WindowEvent.Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left)]);
        runtime.Step([WindowEvent.Mouse(WindowEventKind.MouseUp, x, y, MouseButton.Left)]);
    }

    private static float Width(BibuiRuntime runtime, string text, Font font) =>
        TextLayer.WidthFromChars(runtime.Fonts, text, TextLayer.StyleFromFont(runtime.Fonts, font, font.Scale), font.Scale);

    private static Box Caret(BibuiRuntime runtime) =>
        Below(runtime.FindById("field")!).OfType<Box>().Single(b => b.Extent.X == TextField.CaretWidth);

    private static List<Text> Texts(BibuiRuntime runtime) => Below(runtime.FindById("field")!).OfType<Text>().ToList();

    // The rows that hold text: one per wrapped line.
    private static int Lines(BibuiRuntime runtime) =>
        Texts(runtime).Where(t => t.Content.Length > 0).Select(t => t.Rect.Y).Distinct().Count();

    private static IEnumerable<UiNode> Below(UiNode node)
    {
        for (var child = node.FirstChild; child is not null; child = child.Next == child ? null : child.Next)
        {
            yield return child;
            foreach (var deeper in Below(child)) yield return deeper;
        }
    }

    private static void Same(Pixels a, Pixels b, string what) => IsTrue(a.RawBgraBytes.AsSpan().SequenceEqual(b.RawBgraBytes), $"{what}: the frames differ");

    private static void IsTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!Equals(expected, actual)) throw new Exception($"{what}: expected {expected} but got {actual}");
    }
}
