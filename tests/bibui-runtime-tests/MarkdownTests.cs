using System.Text;
using Bibui;
using Bibui.Runtime;
using Bibui.TextEdit;
using Bibui.Raster.Skia;

/// <summary>
/// The Markdown parser and viewer: what a source parses to, written as a short
/// outline of blocks and styled runs, and how the viewer lays it out.
/// </summary>
static class MarkdownTests
{
    // The document as an outline: a line per block, indented by depth; a block's
    // runs follow its kind, each in brackets with its style when it has one.
    public static string Dump(string markdown) => Dump(MarkdownDocument.Parse(markdown));

    public static string Dump(MarkdownDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var child in doc.Root.Children) Dump(doc, child, 0, sb);
        return sb.ToString().TrimEnd('\n');
    }

    private static void Dump(MarkdownDocument doc, MarkdownBlock block, int depth, StringBuilder sb)
    {
        sb.Append(' ', depth * 2);
        sb.Append(block.Kind switch
        {
            MarkdownBlockKind.Heading => $"h{block.Level}",
            MarkdownBlockKind.List => (block.Ordered ? $"ol({block.Level})" : "ul") + (block.Tight ? "" : " loose"),
            MarkdownBlockKind.ListItem => block.Task switch
            {
                MarkdownTask.Checked => "li[x]",
                MarkdownTask.Unchecked => "li[ ]",
                _ => "li",
            },
            MarkdownBlockKind.Paragraph => "p",
            MarkdownBlockKind.Quote => "quote",
            MarkdownBlockKind.CodeBlock => "code" + (block.Info is { } info ? $"({info})" : ""),
            MarkdownBlockKind.ThematicBreak => "hr",
            MarkdownBlockKind.Table => "table(" + string.Join(",", block.Aligns.Select(a => a.ToString()[0])) + ")",
            MarkdownBlockKind.TableRow => "tr",
            MarkdownBlockKind.TableCell => "td",
            _ => block.Kind.ToString(),
        });
        if (block.Kind == MarkdownBlockKind.CodeBlock)
            sb.Append(' ').Append(Quote(new string(doc.CodeOf(block).Span)));
        foreach (var run in doc.RunsOf(block))
        {
            sb.Append(' ');
            if ((run.Style & MarkdownStyle.LineBreak) != 0) { sb.Append("<br>"); continue; }
            string text = Quote(new string(doc.TextOf(run).Span));
            var style = run.Style & ~MarkdownStyle.Link;
            string tag = style == MarkdownStyle.None ? "" : style.ToString().Replace(", ", "+") + " ";
            if (run.Link >= 0) tag += $"->{doc.Links[run.Link]} ";
            sb.Append(tag.Length == 0 ? text : $"[{tag}{text}]");
        }
        sb.Append('\n');
        foreach (var child in block.Children) Dump(doc, child, depth + 1, sb);
    }

    private static string Quote(string s) => "\"" + s.Replace("\n", "\\n") + "\"";

    public const string TableSample =
        "**Question:** should the field notes (photos, sketches, readings, voice memos) sync to the `shared` folder by default?\n"
        + "\n"
        + "| Option | Who sees them | Set up | Cost |\n"
        + "|---|---|---|---|\n"
        + "| **Sync** | `Everyone` on the trip (3 people in the group) | Nothing. The folder already exists. | 40 MB a day, most of it photos. |\n"
        + "| **Keep local** | `Only me` until shared (1 device) | Nothing if the default stays. | None now. Two copies to keep apart. |\n"
        + "\n"
        + "**Draft** (saved as `notes-sync-draft`, not sent): settings copied as-is to `trip/settings` …\n";

    public static void Register(Action<string, Action> run)
    {
        void Is(string expected, string markdown)
        {
            string actual = Dump(markdown);
            if (actual != expected)
                throw new Exception($"\n--- expected\n{expected}\n--- got\n{actual}");
        }

        // ── Blocks ──────────────────────────────────────────────────────────

        run("Markdown: ATX headings 1-6, with closing hashes, and a # without a space is text", () => Is(
            "h1 \"One\"\nh2 \"Two\"\nh3 \"Three\"\nh4 \"Four\"\nh5 \"Five\"\nh6 \"Six\"\np \"#seven\"",
            "# One\n## Two ##\n### Three\n#### Four\n##### Five\n###### Six\n#seven"));

        run("Markdown: setext headings", () => Is(
            "h1 \"Title\"\nh2 \"Sub\"",
            "Title\n=====\nSub\n---"));

        run("Markdown: soft breaks join a paragraph; blank lines split them", () => Is(
            "p \"one two\"\np \"three\"",
            "one\ntwo\n\nthree"));

        run("Markdown: two spaces or a backslash before a newline break the line", () => Is(
            "p \"one\" <br> \"two\" <br> \"three\"",
            "one  \ntwo\\\nthree"));

        run("Markdown: fenced code keeps its lines, info string and blank lines", () => Is(
            "code(csharp) \"var x = 1;\\n\\n  y();\"\np \"after\"",
            "```csharp\nvar x = 1;\n\n  y();\n```\nafter"));

        run("Markdown: tilde fences, and an unclosed fence runs to the end", () => Is(
            "code \"a\\n```\\nb\"",
            "~~~\na\n```\nb"));

        run("Markdown: indented code, trailing blank lines dropped", () => Is(
            "p \"para\"\ncode \"code line\\n\\nmore\"",
            "para\n\n    code line\n\n    more\n\n"));

        run("Markdown: nested block quotes, with lazy continuation", () => Is(
            "quote\n  p \"outer lazy\"\n  quote\n    p \"inner\"",
            "> outer\nlazy\n>\n> > inner"));

        run("Markdown: thematic breaks in three spellings", () => Is(
            "p \"a\"\nhr\nhr\nhr",
            "a\n\n---\n* * *\n___"));

        run("Markdown: a tight bullet list with a nested ordered list", () => Is(
            "ul\n  li\n    p \"one\"\n    ol(1)\n      li\n        p \"a\"\n      li\n        p \"b\"\n  li\n    p \"two\"",
            "- one\n  1. a\n  2. b\n- two"));

        run("Markdown: blank lines between items make a list loose", () => Is(
            "ul loose\n  li\n    p \"a\"\n  li\n    p \"b\"",
            "- a\n\n- b"));

        run("Markdown: a blank line inside a nested list loosens it, not the outer one", () => Is(
            "ul\n  li\n    p \"a\"\n    ul loose\n      li\n        p \"b\"\n        p \"c\"",
            "- a\n  - b\n\n    c"));

        run("Markdown: an ordered list keeps its start; a different marker starts a new list", () => Is(
            "ol(3)\n  li\n    p \"three\"\n  li\n    p \"four\"\nul\n  li\n    p \"plus\"",
            "3. three\n4. four\n+ plus"));

        run("Markdown: a list item continues by indentation, and a paragraph after it ends the list", () => Is(
            "ul\n  li\n    p \"item continued\"\np \"after\"",
            "- item\n  continued\n\nafter"));

        run("Markdown: a numbered line that is not 1 does not interrupt a paragraph", () => Is(
            "p \"The year 2024. was good\"",
            "The year\n2024. was good"));

        run("Markdown: task items, ticked and not, with their source offsets", () =>
        {
            const string source = "- [ ] open\n- [x] done\n- [] not a task";
            Is("ul\n  li[ ]\n    p \"open\"\n  li[x]\n    p \"done\"\n  li\n    p \"[] not a task\"", source);
            var doc = MarkdownDocument.Parse(source);
            var items = doc.Root.Children[0].Children;
            if (source[items[0].TaskOffset] != ' ' || source[items[1].TaskOffset] != 'x')
                throw new Exception($"task offsets {items[0].TaskOffset}, {items[1].TaskOffset} do not point into the brackets");
        });

        run("Markdown: a GFM table with alignment, escaped pipes and short rows", () => Is(
            "table(N,L,C,R)\n  tr\n    td \"a\"\n    td \"b\"\n    td \"c\"\n    td \"d\"\n"
            + "  tr\n    td \"1\"\n    td \"x|y\"\n    td [Code \"q\"]\n    td",
            "| a | b | c | d |\n|---|:--|:-:|--:|\n| 1 | x\\|y | `q` |"));

        run("Markdown: a table needs a delimiter row as wide as its header", () => Is(
            "p \"| a | b | |---|\"",
            "| a | b |\n|---|"));

        run("Markdown: a table ends at a blank line, and a line before the header stays a paragraph", () => Is(
            "p \"intro\"\ntable(N)\n  tr\n    td \"h\"\n  tr\n    td \"r\"\np \"after\"",
            "intro\n| h |\n| - |\n| r |\n\nafter"));

        run("Markdown: tabs in leading whitespace count to the next multiple of four", () => Is(
            "code \"tabbed\"",
            "\ttabbed"));

        run("Markdown: link reference definitions vanish and resolve", () => Is(
            "p \"see \" [->https://example.com \"docs\"] \" and \" [->https://example.com \"Docs\"]",
            "[docs]: https://example.com \"Title\"\n\nsee [docs] and [Docs][]"));

        // ── Inlines ─────────────────────────────────────────────────────────

        run("Markdown: emphasis and strong, with * and _", () => Is(
            "p [Emphasis \"em\"] \" \" [Strong \"strong\"] \" \" [Emphasis \"em\"] \" \" [Strong \"strong\"]",
            "*em* **strong** _em_ __strong__"));

        run("Markdown: triple delimiters nest strong and emphasis", () => Is(
            "p [Strong+Emphasis \"both\"]",
            "***both***"));

        run("Markdown: nested emphasis inside strong", () => Is(
            "p [Strong \"a \"] [Strong+Emphasis \"b\"] [Strong \" c\"]",
            "**a *b* c**"));

        run("Markdown: intraword underscores are text, intraword stars are emphasis", () => Is(
            "p \"snake_case_name and \" [Emphasis \"in\"] \"word\"",
            "snake_case_name and *in*word"));

        run("Markdown: unmatched delimiters are literal", () => Is(
            "p \"*a **b\"",
            "*a **b"));

        run("Markdown: GFM strikethrough with ~~ and ~", () => Is(
            "p [Strike \"gone\"] \" \" [Strike \"too\"] \" ~~~no~~~\"",
            "~~gone~~ ~too~ ~~~no~~~"));

        run("Markdown: code spans keep their content literal and strip one space", () => Is(
            "p [Code \"*not em*\"] \" \" [Code \"`tick`\"] \" \" [Code \"a b\"]",
            "`*not em*` `` `tick` `` `a\nb`"));

        run("Markdown: an unclosed code span is literal backticks", () => Is(
            "p \"``a\"",
            "``a"));

        run("Markdown: inline links with titles, emphasis inside, and no links in links", () => Is(
            "p [->https://x.dev \"plain\"] \" \" [Strong ->/b \"bold\"] \" [outer \" [->/in \"in\"] \"](/out)\"",
            "[plain](https://x.dev \"t\") [**bold**](/b) [outer [in](/in)](/out)"));

        run("Markdown: angle-bracket autolinks and emails", () => Is(
            "p [->https://example.com/a?b=1 \"https://example.com/a?b=1\"] \" \" [->mailto:me@example.com \"me@example.com\"]",
            "<https://example.com/a?b=1> <me@example.com>"));

        run("Markdown: bare URLs, trailing punctuation and unbalanced parens left out", () => Is(
            "p \"see \" [->https://a.dev/x \"https://a.dev/x\"] \". (\" [->http://www.b.org \"www.b.org\"] \")\"",
            "see https://a.dev/x. (www.b.org)"));

        run("Markdown: backslash escapes and entities", () => Is(
            "p \"*not em* & < © A \\d\"",
            "\\*not em\\* &amp; &lt; &copy; &#65; \\d"));

        run("Markdown: raw HTML stays text", () => Is(
            "p \"<b>bold?</b>\"",
            "<b>bold?</b>"));

        run("Markdown: images show their alt text and link to their source", () => Is(
            "p [Image ->pic.png \"alt text\"]",
            "![alt text](pic.png)"));

        run("Markdown: a question, a table and a note parse to a paragraph, a table and a paragraph", () => Is(
            "p [Strong \"Question:\"] \" should the field notes (photos, sketches, readings, voice memos) sync to the \" [Code \"shared\"] \" folder by default?\"\n"
            + "table(N,N,N,N)\n"
            + "  tr\n    td \"Option\"\n    td \"Who sees them\"\n    td \"Set up\"\n    td \"Cost\"\n"
            + "  tr\n    td [Strong \"Sync\"]\n    td [Code \"Everyone\"] \" on the trip (3 people in the group)\"\n    td \"Nothing. The folder already exists.\"\n    td \"40 MB a day, most of it photos.\"\n"
            + "  tr\n    td [Strong \"Keep local\"]\n    td [Code \"Only me\"] \" until shared (1 device)\"\n    td \"Nothing if the default stays.\"\n    td \"None now. Two copies to keep apart.\"\n"
            + "p [Strong \"Draft\"] \" (saved as \" [Code \"notes-sync-draft\"] \", not sent): settings copied as-is to \" [Code \"trip/settings\"] \" …\"",
            TableSample));

        run("Markdown: CRLF line ends parse as LF", () => Is(
            "h1 \"T\"\np \"a b\"",
            "# T\r\n\r\na\r\nb\r\n"));

        run("Markdown: an empty source is an empty document", () => Is("", ""));

        // ── Layout ──────────────────────────────────────────────────────────

        run("Markdown layout: a paragraph of equal words wraps greedily into the lines the width allows", () =>
        {
            const float width = 200f;
            const int words = 40;
            var source = string.Join(' ', Enumerable.Repeat("lorem", words));
            var (runtime, root) = Show(source, width);
            Font font = runtime.FontInScope();
            float word = runtime.Fonts.Measure("lorem", font), space = runtime.Fonts.Measure(" ", font);
            int perLine = (int)MathF.Floor((width + space + 0.01f) / (word + space));
            var rows = Rows(root);
            Equal((words + perLine - 1) / perLine, rows.Count, "lines");
            foreach (var row in rows)
                IsTrue(RightEdge(row) <= root.Rect.X + width + 0.5f, $"a line runs to {RightEdge(row)}, past {root.Rect.X + width}");
        });

        run("Markdown layout: bold, code and plain text wrap as one paragraph, sharing lines", () =>
        {
            var source = string.Concat(Enumerable.Repeat("plain words **bold words** `code span` ", 6));
            var (_, root) = Show(source, 260f);
            var rows = Rows(root);
            IsTrue(rows.Count >= 3, $"expected the paragraph to wrap, got {rows.Count} lines");
            bool mixed = rows.Any(row =>
            {
                var texts = Descendants(row).OfType<Text>().ToList();
                return texts.Any(t => t.Weight == FontWeight.Bold) && texts.Any(t => t.Weight == FontWeight.Regular);
            });
            IsTrue(mixed, "no line holds both bold and regular text");
            foreach (var row in rows)
                IsTrue(RightEdge(row) <= root.Rect.X + 260.5f, $"a line runs to {RightEdge(row)}");
        });

        foreach (float width in new[] { 680f, 300f, 200f })
            run($"Markdown layout: the sample table fits {width}px, its cells wrapping", () =>
            {
                var (_, root) = Show(TableSample, width);
                float right = root.Rect.X + width + 0.5f;
                foreach (var node in Descendants(root))
                    IsTrue(node.Rect.X + node.Extent.X <= right,
                        $"{node.GetType().Name} at {node.Rect.X}+{node.Extent.X} runs past the viewer's edge at {right}");
                IsTrue(root.Extent.Y > 100f, "the document should have height");
            });

        run("Markdown: a clicked link reports its destination, and the viewer writes nothing", () =>
        {
            var source = BibuiStr.FromStrLit("Go to [the docs](https://example.com/docs) now.");
            MarkdownWidget last = default;
            var runtime = BibuiRuntime.Create();
            runtime.SetBuild(() =>
            {
                last = runtime.Markdown("md", source, width: 400f);
                return Arrange.Column(last.Node).FillX().FillY();
            }, new SurfaceOptions { Width = 500, Height = 200, ScaleFactor = 1f });
            runtime.Step();
            runtime.Step();
            IsTrue(last.ClickedLink is null, "nothing was clicked yet");
            using var page = Bibui.Testing.BibuiPage.From(runtime, runtime.BuildTree);
            page.Click("md/0");
            Equal("https://example.com/docs", last.ClickedLink, "clicked link");
            Equal("Go to [the docs](https://example.com/docs) now.", source.ToString(), "source");
        });

        run("Markdown: a clicked task box reports the offset ToggleTask flips", () =>
        {
            var source = BibuiStr.FromStrLit("- [ ] write it\n- [x] test it");
            MarkdownWidget last = default;
            var runtime = BibuiRuntime.Create();
            runtime.SetBuild(() =>
            {
                last = runtime.Markdown("tasks", source, width: 300f);
                return Arrange.Column(last.Node).FillX().FillY();
            }, new SurfaceOptions { Width = 400, Height = 200, ScaleFactor = 1f });
            runtime.Step();
            runtime.Step();
            using var page = Bibui.Testing.BibuiPage.From(runtime, runtime.BuildTree);
            page.Click("tasks/0");
            IsTrue(last.TaskClicked, "the first task box should report its click");
            Markdown.ToggleTask(source, last.ToggledTask);
            Equal("- [x] write it\n- [x] test it", source.ToString(), "source after toggling");
        });

        // The runtime's own cost per text drawn is the yardstick: a frame of the
        // viewer may allocate what the same number of plain texts does, and not
        // what parsing the source again would.
        run("Markdown: a steady frame neither parses again nor allocates beyond its texts", () =>
        {
            var text = string.Concat(Enumerable.Repeat("## Section\n\nSome *text* with `code` and a [link](https://x.dev).\n\n- one\n- two\n\n", 20));
            var bytes = text.ToCharArray();
            var surface = new SurfaceOptions { Width = 500, Height = 2000, ScaleFactor = 1f };

            var source = BibuiStr.FromStrLit(bytes);
            var runtime = BibuiRuntime.Create();
            runtime.SetBuild(() => Arrange.Column(runtime.Markdown("same", source, width: 400f).Node).FillX().FillY(), surface);
            long frame = SteadyFrame(runtime);
            var texts = Descendants(runtime.FindById("same")!).OfType<Text>().Select(t => t.Content.ToArray()).ToArray();

            var plain = BibuiRuntime.Create();
            plain.SetBuild(() =>
            {
                var column = plain.Box();
                column.ChildLayoutAxis = LayoutAxis.Y;
                column.SizeX = SemanticSize.Pixels(400f);
                column.SizeY = SemanticSize.ChildrenSum();
                using (plain.PushParent(column))
                    foreach (var t in texts) plain.Text(t);
                return Arrange.Column(column).FillX().FillY();
            }, surface);
            long yardstick = SteadyFrame(plain);

            long parseStart = GC.GetAllocatedBytesForCurrentThread();
            MarkdownDocument.Parse(bytes);
            long parse = GC.GetAllocatedBytesForCurrentThread() - parseStart;

            IsTrue(frame <= yardstick * 5 / 4 + 4096,
                $"a steady frame allocated {frame} bytes; {texts.Length} plain texts allocate {yardstick}, a parse {parse}");
        });
    }

    private static long SteadyFrame(BibuiRuntime runtime)
    {
        runtime.Step();
        runtime.Step();
        long before = GC.GetAllocatedBytesForCurrentThread();
        runtime.Step();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // The viewer alone on a surface, wrapped at `width` and laid out twice: the
    // second frame breaks lines at the width the first one measured.
    private static (BibuiRuntime Runtime, UiNode Root) Show(string markdown, float width)
    {
        var runtime = BibuiRuntime.Create();
        var source = BibuiStr.FromStrLit(markdown);
        runtime.SetBuild(() => Arrange.Column(runtime.Markdown("md", source, width: width).Node).FillX().FillY(),
            new SurfaceOptions { Width = (int)width + 40, Height = 1200, ScaleFactor = 1f });
        runtime.Step();
        runtime.Step();
        return (runtime, runtime.FindById("md") ?? throw new Exception("the viewer was not built"));
    }

    // A paragraph's lines: the children of the viewer that hold something (the rest are gaps).
    private static List<UiNode> Rows(UiNode root)
    {
        var rows = new List<UiNode>();
        for (var child = root.FirstChild; child is not null; child = child.Next == child ? null : child.Next)
            if (child.FirstChild is not null) rows.Add(child);
        return rows;
    }

    private static float RightEdge(UiNode row)
    {
        float right = row.Rect.X;
        foreach (var node in Descendants(row))
            if (node is Text) right = MathF.Max(right, node.Rect.X + node.Extent.X);
        return right;
    }

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
