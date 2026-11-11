using System.Numerics;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

/// <summary>
/// The Markdown editor: a document rendered, with the block being edited shown
/// as its source in a field, driven by presses and keys as an app's window
/// sends them.
/// </summary>
static class MarkdownEditorTests
{
    private const float Width = 360f;

    // A heading, a paragraph, a task list, a link, a three-item list and a last paragraph.
    private const string Sample =
        "# Notes\n\nA paragraph of plain text.\n\n- [ ] write it\n- [x] test it\n\n"
        + "See [the site](https://example.com) for more.\n\n- one\n- two\n- three\n\nThe end.";

    public static void Register(Action<string, Action> run)
    {

        run("MarkdownEditor: viewing draws the pixels runtime.Markdown does", () =>
        {
            var source = BibuiStr.FromStrLit(Sample);
            var viewer = BibuiRuntime.Create();
            viewer.SetBuild(() => viewer.Into(viewer.Column().Sized(Size.Fill, Size.Fill), () => viewer.Markdown("md", source, width: Width)),
                new WindowFrame(400, 600, 1f, 60f));
            viewer.Step();
            var (editor, _, _) = Editor(Sample);
            IsTrue(viewer.Frame().RawBgraBytes.AsSpan().SequenceEqual(editor.LastFrame().RawBgraBytes), "the frames differ");
            Equal(Sample, Source(editor), "the source, untouched");
        });

        run("MarkdownEditor: a press on a block edits it, focused, the caret on the source line under the press", () =>
        {
            var (runtime, state, _) = Editor(Sample);
            int list = Block(Sample, "- one");
            var box = Find(runtime, $"ed/blocks/b{list}");
            Press(runtime, box.Rect.X + 20f, box.Rect.Y + box.Extent.Y - 4f);
            Steps(runtime, 2);
            Equal(Sample.IndexOf("- one"), state.Active, "the active block's start");
            Equal(Sample.IndexOf("- three") + "- three".Length, state.Edit.Cursor, "the caret, at the end of the last line");
            IsTrue(runtime.IsFocused("ed/field"), "the field has focus");
            Equal("- one\n- two\n- three\n", FieldText(runtime), "the field holds the block's source and the blank line after it");
            IsTrue(runtime.FindById($"ed/blocks/b{list}") is null, "the block is not also rendered");

            var top = Find(runtime, $"ed/blocks/b{list - 1}");
            Press(runtime, top.Rect.X + 20f, top.Rect.Y + 2f);
            Steps(runtime, 2);
            Equal(Sample.IndexOf("See"), state.Active, "a press on another block edits that one");
            IsTrue(runtime.IsFocused("ed/field"), "the field keeps focus");
        });

        run("MarkdownEditor: typing goes into the source, which is parsed again, and reports Edited", () =>
        {
            var (runtime, state, last) = Editor(Sample);
            Open(runtime, state, Sample.IndexOf("plain text.") + "plain text.".Length);
            Type(runtime, "!");
            IsTrue(last().Edited, "Edited, the frame the source changed");
            Steps(runtime, 1);
            IsTrue(!last().Edited, "not the frame after");

            var source = Source(runtime);
            IsTrue(source.Contains("plain text.!\n"), "the char typed, in the caller's source");
            runtime.Step([WindowEvent.KeyPress(Bibui.Core.Key.Enter)]);
            runtime.Step([WindowEvent.KeyPress(Bibui.Core.Key.Enter)]);
            Type(runtime, "New");
            Steps(runtime, 2);
            source = Source(runtime);
            IsTrue(source.Contains("plain text.!\n\nNew\n"), $"a blank line typed splits the block: {source}");
            Equal(source.IndexOf("New"), state.Active, "the caret's half is the block edited");
            Equal("New\n", FieldText(runtime), "the field holds the new block");
        });

        run("MarkdownEditor: Escape and a press outside the editor render every block again", () =>
        {
            var (runtime, state, _) = Editor(Sample, windowWidth: 600);
            Open(runtime, state, 0);
            runtime.Step([WindowEvent.KeyPress(Bibui.Core.Key.Escape)]);
            Steps(runtime, 1);
            Equal(-1, state.Active, "after Escape");
            IsTrue(runtime.FindById("ed/field") is null, "no field");

            Open(runtime, state, 0);
            Press(runtime, 500f, 20f);
            Steps(runtime, 2);
            Equal(-1, state.Active, "after a press beside it");
            IsTrue(runtime.FindById("ed/field") is null, "no field");
        });

        run("MarkdownEditor: a task box ticks itself in the source and reports Edited, without editing", () =>
        {
            var (runtime, state, last) = Editor(Sample);
            int tasks = Block(Sample, "- [ ] write it");
            var box = Find(runtime, $"ed/blocks/b{tasks}/0");
            Press(runtime, box.Rect.X + 4f, box.Rect.Y + 4f);
            IsTrue(last().Edited, "Edited");
            IsTrue(Source(runtime).Contains("- [x] write it"), "ticked in the source");
            Equal(-1, state.Active, "nothing edited");
        });

        run("MarkdownEditor: a link reports ClickedLink, without editing", () =>
        {
            var (runtime, state, last) = Editor(Sample);
            int paragraph = Block(Sample, "See");
            var link = Find(runtime, $"ed/blocks/b{paragraph}/0");
            Press(runtime, link.Rect.X + 4f, link.Rect.Y + 4f);
            Equal("https://example.com", last().ClickedLink, "the link");
            IsTrue(!last().Edited, "not Edited");
            Steps(runtime, 1);
            Equal(-1, state.Active, "nothing edited");
        });

        run("MarkdownEditor: the app setting the state opens that block, focused, and scrolls to it", () =>
        {
            var text = string.Concat(Enumerable.Range(1, 40).Select(i => $"Paragraph {i}.\n\n")) + "End.";
            var (runtime, state, last) = Editor(text, windowHeight: 200);
            int at = text.IndexOf("Paragraph 35.");
            state.Active = at;
            state.Edit.Cursor = state.Edit.Mark = at + 4;
            Steps(runtime, 30);
            Equal(at, state.Active, "the block opened");
            Equal(at + 4, state.Edit.Cursor, "the caret where it was put");
            IsTrue(runtime.IsFocused("ed/field"), "the field has focus");
            var view = last().Node;
            var field = Find(runtime, "ed/field");
            IsTrue(view.ViewOffset.Y > 0f, "the view scrolled");
            IsTrue(field.Rect.Y >= view.Rect.Y && field.Rect.Y + field.Extent.Y <= view.Rect.Y + view.Extent.Y + 0.01f,
                $"the field at {field.Rect.Y}..{field.Rect.Y + field.Extent.Y} is in the view at {view.Rect.Y}..{view.Rect.Y + view.Extent.Y}");
        });

        run("MarkdownEditor: Up, Down, Left and Right at the field's edges move the caret into the next block", () =>
        {
            const string text = "# Head\nPara line\nsecond\n\nEnd";
            var (runtime, state, _) = Editor(text);
            int para = text.IndexOf("Para"), end = text.IndexOf("End");
            Open(runtime, state, para + 2);

            KeyDown(runtime, Bibui.Core.Key.Up);
            Equal(0, state.Active, "Up from the first line: the block above");
            Equal(2, state.Edit.Cursor, "on its last line, at the same column");

            KeyDown(runtime, Bibui.Core.Key.Down);
            Equal(para, state.Active, "Down from the last line: the block below");
            Equal(para + 2, state.Edit.Cursor, "on its first line, at the same column");

            state.Edit.Cursor = state.Edit.Mark = para;
            Steps(runtime, 2);
            KeyDown(runtime, Bibui.Core.Key.Left);
            Equal(0, state.Active, "Left at the start: the block before");
            Equal(para - 1, state.Edit.Cursor, "at its end");

            KeyDown(runtime, Bibui.Core.Key.Right);
            Equal(para, state.Active, "Right at the end: the block after");
            Equal(para, state.Edit.Cursor, "at its start");

            state.Edit.Cursor = state.Edit.Mark = end - 1;
            Steps(runtime, 2);
            KeyDown(runtime, Bibui.Core.Key.Right);
            Equal(end, state.Active, "Right at the end of a block's trailing blank line");
            Equal(end, state.Edit.Cursor, "at the next block's start");

            KeyDown(runtime, Bibui.Core.Key.Down);
            Equal(text.Length, state.Edit.Cursor, "Down on the last block's last line stays in it, at its end");
            Equal(end, state.Active, "still the last block");
        });

        run("MarkdownEditor: Backspace at a block's start and Delete at its end join it to the block beside it", () =>
        {
            var source = "One\n\nTwo";
            var (runtime, state, last) = Editor(source);
            Open(runtime, state, source.IndexOf("Two"));
            runtime.Step([WindowEvent.KeyPress(Bibui.Core.Key.Backspace)]);
            IsTrue(last().Edited, "Edited");
            Equal("One\nTwo", Source(runtime), "the line break between them removed");
            Steps(runtime, 1);
            Equal(0, state.Active, "one block now");
            Equal(4, state.Edit.Cursor, "the caret where the break was");

            var (other, otherState, _) = Editor("One\n\n\nTwo");
            Open(other, otherState, 5);
            Equal("One\n\n", FieldText(other), "the first block's text runs to the break before the next");
            other.Step([WindowEvent.KeyPress(Bibui.Core.Key.Delete)]);
            Equal("One\n\nTwo", Source(other), "Delete at its end removes the break after it");
        });

        run("MarkdownEditor: blank lines and link definitions after a block, and before the first, are in its field", () =>
        {
            const string text = "\n\nIntro\n\n[site]: https://example.com\n\n\nEnd";
            var (runtime, state, _) = Editor(text);
            Open(runtime, state, 0);
            Equal("\n\nIntro\n\n[site]: https://example.com\n\n", FieldText(runtime), "the first block's text");
            Open(runtime, state, text.IndexOf("[site]"));
            Equal(0, state.Active, "a definition is in the block before it");

            int blank = text.IndexOf("\n\nEnd");
            Open(runtime, state, blank);
            Type(runtime, "More");
            Steps(runtime, 2);
            Equal("\n\nIntro\n\n[site]: https://example.com\nMore\n\nEnd", Source(runtime), "typed on a trailing blank line");
        });

        run("MarkdownEditor: the block being edited has its markers dimmed, strong text bold and emphasis italic", () =>
        {
            const string text = "# Head\n\nSome **strong** and *soft* and __more__ words.\n\n> - [x] _done_";
            var (runtime, state, _) = Editor(text);
            Open(runtime, state, text.IndexOf("Some"));
            var texts = FieldTexts(runtime);
            Equal(FontWeight.Bold, Piece(texts, "strong").Weight, "strong");
            Equal(FontWeight.Italic, Piece(texts, "soft").Weight, "emphasis");
            Equal(FontWeight.Bold, Piece(texts, "more").Weight, "strong, in underscores");
            IsTrue(texts.Where(t => t.Content is "**" or "*" or "__").Count() == 6
                && texts.Where(t => t.Content is "**" or "*" or "__").All(t => Equals(t.Color, UiColor.TextSecondary)), "every marker dimmed");
            IsTrue(texts.Where(t => t.Content.IndexOf("Some") >= 0 || t.Content.IndexOf("words") >= 0)
                .All(t => t.Weight == FontWeight.Regular && !Equals(t.Color, UiColor.TextSecondary)), "the rest as the field's");

            Open(runtime, state, 0);
            Equal<UiColor?>(UiColor.TextSecondary, Piece(FieldTexts(runtime), "#").Color, "a heading's #");
            Open(runtime, state, text.IndexOf('>'));
            texts = FieldTexts(runtime);
            Equal<UiColor?>(UiColor.TextSecondary, Piece(texts, ">").Color, "a quote's >");
            Equal<UiColor?>(UiColor.TextSecondary, Piece(texts, "-").Color, "a list marker");
            Equal<UiColor?>(UiColor.TextSecondary, Piece(texts, "[x]").Color, "a task box");
            Equal(FontWeight.Italic, Piece(texts, "done").Weight, "emphasis in the item");
        });

        run("MarkdownEditor: code in the block being edited is in the accent colour, its backticks and fences dimmed", () =>
        {
            const string text = "Call `run()` now.\n\n```cs\nvar x = **1**;\n```";
            var (runtime, state, _) = Editor(text);
            Open(runtime, state, 0);
            var texts = FieldTexts(runtime);
            Equal<UiColor?>(UiColor.Accent, Piece(texts, "run()").Color, "inline code");
            IsTrue(texts.Count(t => t.Content is "`" && Equals(t.Color, UiColor.TextSecondary)) == 2, "its backticks dimmed");

            Open(runtime, state, text.IndexOf("```"));
            texts = FieldTexts(runtime);
            Equal<UiColor?>(UiColor.Accent, Piece(texts, "var x = **1**;").Color, "a code block's text, its stars not markers");
            Equal(FontWeight.Regular, Piece(texts, "var x = **1**;").Weight, "and not bold");
            IsTrue(texts.Count(t => t.Content is "```" && Equals(t.Color, UiColor.TextSecondary)) == 2, "its fences dimmed");
        });

        run("MarkdownEditor: strong inside emphasis is bold italic, beside the italic around it", () =>
        {
            const string text = "An *aside with **both** in it* and ***all three***.";
            var (runtime, state, _) = Editor(text);
            Open(runtime, state, 0);
            var texts = FieldTexts(runtime);
            Equal(FontWeight.Italic, Piece(texts, "aside with ").Weight, "emphasis before");
            Equal(FontWeight.BoldItalic, Piece(texts, "both").Weight, "strong inside emphasis");
            Equal(FontWeight.Italic, Piece(texts, " in it").Weight, "emphasis after");
            Equal(FontWeight.BoldItalic, Piece(texts, "all three").Weight, "three stars");
            IsTrue(StyledText.InOrder(state.Ranges.AsSpan(0, state.RangeCount), state.Field.Str.Length), "sorted, none overlapping");
        });

        run("MarkdownEditor: the ranges follow an edit in the frame it is made", () =>
        {
            const string text = "Say **ab** twice.";
            var (runtime, state, _) = Editor(text);
            Open(runtime, state, text.IndexOf("ab") + 1);
            Type(runtime, "x");
            Equal("Say **axb** twice.", Source(runtime), "typed");
            var texts = FieldTexts(runtime);
            Equal(FontWeight.Bold, Piece(texts, "ax").Weight, "the bold text before the caret, this frame");
            Equal(FontWeight.Bold, Piece(texts, "b").Weight, "and after it, moved by the char");
            IsTrue(texts.Count(t => t.Content is "**" && Equals(t.Color, UiColor.TextSecondary)) == 2, "both markers dimmed, moved with it");
        });

        run("MarkdownEditor: a steady frame editing a block allocates nothing, its styled field included", () =>
        {
            Equal(0L, SteadyFrame("Some **strong** and *soft* words, `code` and [a link](https://example.com).\n\n- [x] item\n\nEnd."),
                "bytes the editor's call allocated");
        });

        run("MarkdownEditor: a block the app draws is drawn its way, and a press on it edits its source", () =>
        {
            const string text = "First.\n\n```diagram\nx\n```\n\nLast.";
            var (runtime, _, _) = Editor(text, drawBlock: MarkdownTests.Diagram);
            var drawn = Find(runtime, "diagram");
            Equal(new Vector2(Width, MarkdownTests.DiagramHeight), drawn.Extent, "as the app made it, the view's width");
            Press(runtime, drawn.Rect.X + 10f, drawn.Rect.Y + 10f);
            Steps(runtime, 2);
            var (plain, plainState, _) = Editor(text);
            Open(plain, plainState, text.IndexOf("```"));
            Equal(FieldText(plain), FieldText(runtime), "its source in the field, as the block opened without a drawer");
        });

        run("MarkdownEditor: a steady frame with blocks the app draws allocates nothing", () =>
        {
            Equal(0L, SteadyFrame("Some **strong** words.\n\n```diagram\nx\n```\n\nEnd.", MarkdownTests.Diagram),
                "bytes the editor's call allocated");
        });

        run("MarkdownHighlight: block markers at a line's start, and none in a word or a break", () =>
        {
            const string text = "> 1. [ ] a snake_case_name\n---\n10) x\n~~~\n> not a quote\n~~~";
            TextStyleRange[] ranges = [];
            int count = MarkdownHighlight.RangesFromSource(text, ref ranges);
            var found = ranges.AsSpan(0, count).ToArray().Select(r => (text.Substring(r.Start, r.Length), r.Color, r.Weight)).ToArray();
            (string, UiColor?, FontWeight?)[] expected =
            [
                (">", UiColor.TextSecondary, null), ("1.", UiColor.TextSecondary, null), ("[ ]", UiColor.TextSecondary, null),
                ("10)", UiColor.TextSecondary, null), ("~~~", UiColor.TextSecondary, null),
                ("> not a quote", UiColor.Accent, null), ("~~~", UiColor.TextSecondary, null),
            ];
            IsTrue(found.SequenceEqual(expected), $"found {string.Join(", ", found)}");
            IsTrue(ranges.Length >= count, "grown to hold them");
        });
    }

    // ── The editor, driven ──────────────────────────────────────────────────

    private sealed class Run(BibuiStr source, MarkdownEditState state)
    {
        public readonly BibuiStr Source = source;
        public readonly MarkdownEditState State = state;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BibuiRuntime, Run> s_runs = new();

    // The editor "ed" alone in a window, `Width` wide, laid out twice.
    private static (BibuiRuntime Runtime, MarkdownEditState State, Func<MarkdownEditorWidget> Last) Editor(
        string text, int windowWidth = 400, int windowHeight = 600, MarkdownBlockDraw? drawBlock = null)
    {
        var source = BibuiStr.FromStrLit(text);
        var state = new MarkdownEditState();
        MarkdownEditorWidget last = default;
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() => runtime.Into(runtime.Column().Sized(Size.Fill, Size.Fill),
            () => last = runtime.MarkdownEditor("ed", source, state, width: Width, drawBlock: drawBlock)),
            new WindowFrame(windowWidth, windowHeight, 1f, 60f));
        s_runs.AddOrUpdate(runtime, new Run(source, state));
        Steps(runtime, 2);
        return (runtime, state, () => last);
    }

    private static string Source(BibuiRuntime runtime) => s_runs.TryGetValue(runtime, out var run) ? run.Source.ToString() : "";

    // As the app would: the caret put at `at`, its block opened.
    private static void Open(BibuiRuntime runtime, MarkdownEditState state, int at)
    {
        state.Active = at;
        state.Edit.Cursor = state.Edit.Mark = at;
        Steps(runtime, 3);
    }

    private static void Steps(BibuiRuntime runtime, int frames)
    {
        for (int i = 0; i < frames; i++) runtime.Step();
    }

    private static void Press(BibuiRuntime runtime, float x, float y)
    {
        runtime.Step([WindowEvent.Mouse(WindowEventKind.MouseMove, x, y, MouseButton.Unknown),
            WindowEvent.Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left)]);
        runtime.Step([WindowEvent.Mouse(WindowEventKind.MouseUp, x, y, MouseButton.Left)]);
    }

    private static void KeyDown(BibuiRuntime runtime, Key key)
    {
        runtime.Step([WindowEvent.KeyPress(key)]);
        Steps(runtime, 2);
    }

    private static void Type(BibuiRuntime runtime, string text)
    {
        foreach (var rune in text.EnumerateRunes()) runtime.Step([WindowEvent.Text(rune)]);
    }

    // What the field shows: the editor keeps it in the state, as the field edits it.
    private static string FieldText(BibuiRuntime runtime)
    {
        Find(runtime, "ed/field");
        return s_runs.TryGetValue(runtime, out var run) ? run.State.Field.ToString() : "";
    }

    // The texts the field drew, and the one that is exactly `content`.
    private static List<Text> FieldTexts(BibuiRuntime runtime) =>
        Descendants(Find(runtime, "ed/field")).OfType<Text>().Where(t => t.Content.Length > 0).ToList();

    private static Text Piece(List<Text> texts, string content) =>
        texts.FirstOrDefault(t => t.Content.SequenceEqual(content))
        ?? throw new Exception($"no piece \"{content}\" in [{string.Join("|", texts.Select(t => new string(t.Content)))}]");

    // The bytes the editor's call allocates in a steady frame, its first block
    // open and focused: its ranges found and its field built, as the frames
    // before. The frame around it is the runtime's.
    private static long SteadyFrame(string text, MarkdownBlockDraw? drawBlock = null)
    {
        var source = BibuiStr.FromStrLit(text);
        var state = new MarkdownEditState { Active = 0 };
        var runtime = BibuiRuntime.Create();
        long allocated = -1;
        runtime.SetBuild(() => runtime.Into(runtime.Column().Sized(Size.Fill, Size.Fill), () =>
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                runtime.MarkdownEditor("ed", source, state, width: Width, drawBlock: drawBlock);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }), new WindowFrame(400, 600, 1f, 60f));
        Steps(runtime, 5);
        IsTrue(runtime.IsFocused("ed/field") && state.RangeCount > 0, "editing a block with ranges");
        return allocated;
    }

    // The index of the top-level block whose source starts with `start`.
    private static int Block(string text, string start)
    {
        var doc = MarkdownDocument.Parse(text);
        var blocks = doc.Root.Children;
        for (int i = 0; i < blocks.Count; i++)
            if (text[blocks[i].SourceStart..].StartsWith(start, StringComparison.Ordinal)) return i;
        throw new Exception($"no block starts with {start}");
    }

    private static UiNode Find(BibuiRuntime runtime, string id) =>
        runtime.FindById(id) ?? throw new Exception($"{id} was not built");

    private static IEnumerable<UiNode> Descendants(UiNode node)
    {
        for (var child = node.FirstChild; child is not null; child = child.Next == child ? null : child.Next)
        {
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private static void IsTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!Equals(expected, actual)) throw new Exception($"{what}: expected {expected} but got {actual}");
    }
}
