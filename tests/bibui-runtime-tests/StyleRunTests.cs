using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Text;

/// <summary>
/// A text in more than one style, in the text layer (StyleRun): measured and
/// broken into lines with each run in its own face, beside the functions over
/// one style. A text given as one run breaks as the plain functions break it.
/// </summary>
static class StyleRunTests
{
    // Texts whose line breaks are worth comparing: spaces at ends and alone on a
    // line, words wider than the width, hard breaks of each kind, clusters,
    // combining marks and CJK.
    private static readonly string[] Samples =
    [
        "Some words, wrapped at the width of the field and then some more of them.",
        "word   ", "   ", "ab\n   \ncd", "tail   \nnext", "aaaa bbbb      ", "x\r\ny\rz", "\n\n\n", "  leading and trailing  \r\n",
        "averyveryverylongwordthatdoesnotfit and short", "one  two   three", "supercalifragilisticexpialidocious, said twice: supercalifragilisticexpialidocious",
        "thumbs 👍🏽 up, and on", "a👍🏽👍🏽b 👍🏽 c", "cafe\u0301 au lait, re\u0301sume\u0301", "日本語のテキストを折り返す、そして English words 混在 の行",
        "漢字かな交じり文\n第二行", "",
    ];

    private static readonly TextWrap[] Wraps = [TextWrap.Words, TextWrap.Characters];
    private static readonly float[] Widths = [1f, 30f, 60f, 85f, 120f, 1000f];

    // A runtime with the emoji face as its cluster face, as a platform sets up
    // the system's, and its font's styles at Regular and at Bold (SemiBold).
    private static (Fonts Fonts, float Scale, FontStyle Regular, FontStyle Bold) Faces()
    {
        var runtime = BibuiRuntime.Create();
        runtime.Fonts.UseClusterFont(Bibui.Text.FreeType.FontData.FromFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoColorEmoji-subset.ttf")));
        Font regular = runtime.FontInScope();
        Font bold;
        using (runtime.FontFace(weight: FontWeight.Bold)) bold = runtime.FontInScope();
        var fonts = runtime.Fonts;
        var styles = (fonts, regular.Scale, TextLayer.StyleFromFont(fonts, regular, regular.Scale), TextLayer.StyleFromFont(fonts, bold, bold.Scale));
        IsTrue(styles.Item3.Face != styles.Item4.Face, "Regular and Bold are two faces");
        return styles;
    }

    private static string Describe(ReadOnlySpan<TextLine> lines)
    {
        var parts = new List<string>();
        foreach (var l in lines) parts.Add($"{l.Start},{l.End},{l.Next}");
        return string.Join(" ", parts);
    }

    // The lines, one LineFromText at a time.
    private static string ByLine(Fonts fonts, string text, StyleRun[] runs, float scale, float width, TextWrap wrap)
    {
        var lines = new List<TextLine>();
        for (int at = 0; ; )
        {
            var line = TextLayer.LineFromText(fonts, text, runs, scale, width, wrap, at);
            lines.Add(line);
            if (line.End == text.Length) break;
            at = line.Next;
        }
        return Describe(lines.ToArray());
    }

    public static void Register(Action<string, Action> run)
    {
        run("Style runs: a text given as one run breaks, line by line and kept, and measures as the plain functions do", () =>
        {
            var (fonts, scale, regular, bold) = Faces();
            int cases = 0, broken = 0;
            foreach (var style in new[] { regular, bold })
            foreach (var text in Samples)
            {
                StyleRun[] one = [new(0, style)];
                foreach (var wrap in Wraps)
                foreach (float width in Widths)
                {
                    var plain = TextLayer.LinesFromText(fonts, text, style, scale, width, wrap);
                    string expected = Describe(plain);
                    string where = $"{Quote(text)} at {width} by {wrap}";
                    Equal(expected, Describe(TextLayer.LinesFromText(fonts, text, one, scale, width, wrap)), $"{where}, kept");
                    Equal(expected, ByLine(fonts, text, one, scale, width, wrap), $"{where}, line by line");
                    cases++;
                    if (plain.Length > 1) broken++;
                }
                Equal(TextLayer.WidthFromChars(fonts, text, style, scale), TextLayer.WidthFromChars(fonts, text, one, 0, text.Length, scale), $"the width of {Quote(text)}");
            }
            IsTrue(broken > cases / 2, $"most cases break into lines: {broken} of {cases}");
        });

        run("Style runs: a text cut anywhere into runs of one style breaks and measures as it does whole", () =>
        {
            var (fonts, scale, regular, _) = Faces();
            foreach (var text in Samples)
            for (int cut = 1; cut < text.Length; cut++)
            {
                StyleRun[] two = [new(0, regular), new(cut, regular)];
                foreach (var wrap in Wraps)
                foreach (float width in Widths)
                    Equal(Describe(TextLayer.LinesFromText(fonts, text, regular, scale, width, wrap)),
                        Describe(TextLayer.LinesFromText(fonts, text, two, scale, width, wrap)), $"{Quote(text)} cut at {cut}, at {width} by {wrap}");
                Equal(TextLayer.WidthFromChars(fonts, text, regular, scale), TextLayer.WidthFromChars(fonts, text, two, 0, text.Length, scale),
                    $"the width of {Quote(text)} cut at {cut}");
            }
        });

        run("Style runs: a cluster a run starts inside is measured whole in the run it starts in", () =>
        {
            var (fonts, scale, regular, bold) = Faces();
            const string text = "thumbs 👍🏽 up";   // the emoji is four chars, two code points: one cluster
            int emoji = text.IndexOf("👍", StringComparison.Ordinal);
            StyleRun[] after = [new(0, regular), new(emoji + 4, bold)];
            foreach (int inside in new[] { emoji + 1, emoji + 2, emoji + 3 })
            {
                StyleRun[] cut = [new(0, regular), new(inside, bold)];
                StyleRun[] within = [new(0, regular), new(inside, bold), new(emoji + 4, regular)];
                Equal(TextLayer.WidthFromChars(fonts, text, after, 0, text.Length, scale), TextLayer.WidthFromChars(fonts, text, cut, 0, text.Length, scale),
                    $"a run from {inside}: from the cluster's end");
                Equal(TextLayer.WidthFromChars(fonts, text, regular, scale), TextLayer.WidthFromChars(fonts, text, within, 0, text.Length, scale),
                    $"a run from {inside} to the cluster's end: empty");
                foreach (var wrap in Wraps)
                foreach (float width in Widths)
                {
                    Equal(Describe(TextLayer.LinesFromText(fonts, text, after, scale, width, wrap)),
                        Describe(TextLayer.LinesFromText(fonts, text, cut, scale, width, wrap)), $"from {inside}, at {width} by {wrap}");
                    Equal(Describe(TextLayer.LinesFromText(fonts, text, regular, scale, width, wrap)),
                        Describe(TextLayer.LinesFromText(fonts, text, within, scale, width, wrap)), $"within, from {inside}, at {width} by {wrap}");
                }
            }
        });

        run("Style runs: a word across runs is measured piece by piece, each in its run's face", () =>
        {
            var (fonts, scale, regular, bold) = Faces();
            const string text = "plain BOLDplain end";
            StyleRun[] runs = [new(0, regular), new(6, bold), new(10, regular)];
            float W(string s, FontStyle style) => TextLayer.WidthFromChars(fonts, s, style, scale);
            IsTrue(W("BOLD", bold) > W("BOLD", regular), "Bold is wider");
            Equal(W("plain ", regular) + W("BOLD", bold) + W("plain end", regular), TextLayer.WidthFromChars(fonts, text, runs, 0, text.Length, scale), "the whole text");
            Equal(W("LD", bold) + W("pl", regular), TextLayer.WidthFromChars(fonts, text, runs, 8, 12, scale), "a stretch across a run's end");
            Equal(W("OL", bold), TextLayer.WidthFromChars(fonts, text, runs, 7, 9, scale), "a stretch inside a run");

            float whole = TextLayer.WidthFromChars(fonts, text, runs, 0, text.Length, scale);
            Equal("0,19,19", Describe(TextLayer.LinesFromText(fonts, text, runs, scale, whole, TextWrap.Words)), "at its own width, one line");
            Equal("0,15,16 16,19,19", Describe(TextLayer.LinesFromText(fonts, text, runs, scale, whole - 0.5f, TextWrap.Words)), "a little less, two");
        });

        run("Style runs: a space is measured in the run it is in", () =>
        {
            // Inter's space is as wide in SemiBold as in Regular: a run at twice the size tells them apart.
            var (fonts, scale, regular, _) = Faces();
            var large = TextLayer.StyleFromFaceSize(fonts, regular.Face, regular.Strike.Size * 2f);
            const string text = "aaa bbb";
            StyleRun[] runs = [new(0, regular), new(3, large), new(4, regular)];
            float W(string s, FontStyle style) => TextLayer.WidthFromChars(fonts, s, style, scale);
            IsTrue(W(" ", large) > W(" ", regular), "a large space is wider");
            float width = W("aaa", regular) + W(" ", large) + W("bbb", regular);
            Equal(width, TextLayer.WidthFromChars(fonts, text, runs, 0, text.Length, scale), "the width");
            Equal("0,7,7", Describe(TextLayer.LinesFromText(fonts, text, runs, scale, width, TextWrap.Words)), "at its width, one line");
            Equal("0,3,4 4,7,7", Describe(TextLayer.LinesFromText(fonts, text, runs, scale, width - 0.05f, TextWrap.Words)), "a hair less, two");
        });

        run("Style runs: lines break at the offsets their faces put them", () =>
        {
            var (fonts, scale, regular, bold) = Faces();
            float W(string s, FontStyle style) => TextLayer.WidthFromChars(fonts, s, style, scale);
            const string words = "wide wide wide wide";
            float width = W(words, regular);
            IsTrue(W("wide", bold) > W("wide", regular) + 0.05f, "a bold word is wider");
            Equal("0,19,19", Describe(TextLayer.LinesFromText(fonts, words, [new StyleRun(0, regular)], scale, width, TextWrap.Words)), "regular, one line");
            Equal("0,14,15 15,19,19", Describe(TextLayer.LinesFromText(fonts, words, [new StyleRun(0, regular), new StyleRun(15, bold)], scale, width, TextWrap.Words)),
                "the last word bold: it goes down");
            Equal("0,9,10 10,19,19", Describe(TextLayer.LinesFromText(fonts, words, [new StyleRun(0, bold), new StyleRun(10, regular), new StyleRun(17, bold)],
                scale, W("wide wide", bold) + 0.5f, TextWrap.Words)), "two bold words a line, the second line's half in each");

            const string ms = "mmmmmmmm";
            float room = W("mmmm", bold) + W("mm", regular);
            StyleRun[] runs = [new(0, bold), new(4, regular)];
            Equal("0,6,6 6,8,8", Describe(TextLayer.LinesFromText(fonts, ms, runs, scale, room, TextWrap.Characters)), "by characters, four bold Ms and two regular");
            Equal("0,5,5 5,8,8", Describe(TextLayer.LinesFromText(fonts, ms, [new StyleRun(0, bold)], scale, room, TextWrap.Characters)), "all bold, fewer");
        });

        run("Style runs: a text in two faces breaks into the same lines kept as line by line", () =>
        {
            var (fonts, scale, regular, bold) = Faces();
            const string text = "aaaa bbbbBBBB cccc dddd bbbbBBBB eeee 日本語の文 👍🏽👍🏽 ok";
            // The capitals bold: a word that does not fit crosses into a bold run.
            StyleRun[] runs = [new(0, regular), new(9, bold), new(13, regular), new(28, bold), new(32, regular), new(40, bold), new(45, regular)];
            for (float width = 1f; width < 400f; width += 1f)
            foreach (var wrap in Wraps)
                Equal(ByLine(fonts, text, runs, scale, width, wrap), Describe(TextLayer.LinesFromText(fonts, text, runs, scale, width, wrap)), $"at {width} by {wrap}");
        });

        run("Style runs: runs not starting at 0, out of order, or without a style are refused", () =>
        {
            var (fonts, _, regular, _) = Faces();
            Throws<ArgumentException>(() => TextLayer.LinesFromText(fonts, "ab", [], 1f, 100f, TextWrap.Words), "none");
            Throws<ArgumentException>(() => TextLayer.LinesFromText(fonts, "ab", [new StyleRun(1, regular)], 1f, 100f, TextWrap.Words), "from 1");
            Throws<ArgumentException>(() => TextLayer.LinesFromText(fonts, "abcd",
                [new StyleRun(0, regular), new StyleRun(2, regular), new StyleRun(1, regular)], 1f, 100f, TextWrap.Words), "out of order");
            Throws<ArgumentException>(() => TextLayer.LinesFromText(fonts, "ab", [new StyleRun(0, regular), default], 1f, 100f, TextWrap.Words), "no style");
            Throws<ArgumentException>(() => TextLayer.LineFromText(fonts, "ab", [new StyleRun(1, regular)], 1f, 100f, TextWrap.Words, 0), "a line, from 1");
            Throws<ArgumentException>(() => TextLayer.WidthFromChars(fonts, "ab", [], 0, 2, 1f), "a width, none");
        });

        run("Style runs: breaking a styled text again allocates nothing, and a new wrap only what the wrap cache does", () =>
        {
            var (fonts, scale, regular, bold) = Faces();
            string text = string.Concat(Enumerable.Repeat("bold and plain words, ", 200));
            var runs = Enumerable.Range(0, 200).SelectMany(k => new[] { new StyleRun(k * 22, bold), new StyleRun(k * 22 + 4, regular) }).ToArray();
            void Break(float width)
            {
                for (int at = 0; ; )
                {
                    var line = TextLayer.LineFromText(fonts, text, runs, scale, width, TextWrap.Words, at);
                    if (line.End == text.Length) break;
                    at = line.Next;
                }
                TextLayer.LinesFromText(fonts, text, runs, scale, width, TextWrap.Words);
                TextLayer.WidthFromChars(fonts, text, runs, 0, text.Length, scale);
            }
            Break(200f);
            Break(300f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Break(200f);
            Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before, "bytes allocated breaking it again");

            for (ulong k = 0; k <= Fonts.KeepWrapFrames + 1; k++) TextLayer.FontsEndFrame(fonts);   // both wraps back on the free list
            before = GC.GetAllocatedBytesForCurrentThread();
            TextLayer.LinesFromText(fonts, text, runs, scale, 300f, TextWrap.Words);
            Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before, "bytes allocated by a new wrap on a freed node");
        });

        run("Style runs: breaking a text with a run a word takes time in proportion to its lines", () =>
        {
            var (fonts, scale, regular, bold) = Faces();
            double Time(int lines)
            {
                string text = string.Concat(Enumerable.Repeat("bold and plain words\n", lines));
                var runs = Enumerable.Range(0, lines).SelectMany(k => new[] { new StyleRun(k * 21, bold), new StyleRun(k * 21 + 4, regular) }).ToArray();
                double best = double.MaxValue;
                for (int round = 0; round < 3; round++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    for (int at = 0; ; )
                    {
                        var line = TextLayer.LineFromText(fonts, text, runs, scale, 1000f, TextWrap.Words, at);
                        if (line.End == text.Length) break;
                        at = line.Next;
                    }
                    TextLayer.LinesFromText(fonts, text, runs, scale, 1000f - round, TextWrap.Words);   // a new width: not kept
                    best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                }
                return best;
            }
            Time(500);
            double small = Time(4000), large = Time(16000);
            IsTrue(large / small < 8, $"four times the lines took {large / small:0.0} times as long ({small:0.0} ms, {large:0.0} ms)");
        });
    }

    private static string Quote(string s) => "\"" + s.Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static void Throws<T>(Action body, string what) where T : Exception
    {
        try { body(); }
        catch (T) { return; }
        throw new Exception($"{what}: expected {typeof(T).Name}");
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
