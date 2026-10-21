using Bibui.Core;
using Bibui.Core.Draw;
using Bibui.Core.Runtime;
using Bibui.Raster.Managed.Backend;
using Size = Bibui.Core.Size;

/// <summary>
/// Colour glyphs and font fallback: a font registered with
/// <see cref="FontRegistry.RegisterFallback"/> sets what the text's family lacks,
/// a grapheme cluster at a time, and a colour face's glyphs are drawn as images by
/// both renderers. The font is a subset of Noto Color Emoji (CBDT bitmaps at 109 px).
/// </summary>
static class ColorGlyphTests
{
    public const string Sample = "hi 🧑‍🚀 👍🏽 🇩🇰 1️⃣";

    public static byte[] EmojiFont => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoColorEmoji-subset.ttf"));

    /// <summary>Runs <paramref name="body"/> with the emoji font as the one fallback, and none after.</summary>
    public static void WithEmoji(Action body)
    {
        FontRegistry.Clear();
        FontRegistry.RegisterFallback(EmojiFont);
        try { body(); }
        finally { FontRegistry.Clear(); }
    }

    public static void Register(Action<string, Action> run)
    {
        run("Fallback: without one, a character the family lacks is its missing glyph, in one run", () =>
        {
            FontRegistry.Clear();
            using var fonts = new Fonts();
            var runs = fonts.SplitRuns(Sample, null, 400)!;
            Check(runs.Next is null && !runs.Shaped, "one unshaped run in Inter");
        });

        run("Fallback: each emoji cluster is a run in the colour face, shaped; the text between stays the family's", () => WithEmoji(() =>
        {
            using var fonts = new Fonts();
            var parts = new List<(string Text, bool Color, bool Shaped)>();
            for (var r = fonts.SplitRuns(Sample, null, 400); r is not null; r = r.Next)
                parts.Add((Sample[r.Start..r.End], r.Face.IsColor, r.Shaped));
            string Show() => string.Join(" | ", parts.Select(p => $"{p.Text}:{(p.Color ? "colour" : "text")}"));
            Check(parts.Count == 8, $"text, emoji, text… 8 runs: {Show()}");
            Check(parts[0] == ("hi ", false, false), $"\"hi \" is Inter's, unshaped: {Show()}");
            Check(parts[1] == ("🧑‍🚀", true, true), $"the astronaut, ZWJ and all, is one colour run: {Show()}");
            Check(parts[3] == ("👍🏽", true, true), $"the skin tone goes with its hand: {Show()}");
            Check(parts[5] == ("🇩🇰", true, true), $"the flag's two indicators are one run: {Show()}");
            Check(parts[7] == ("1️⃣", true, true), $"the keycap's 1 goes with its selector and cap, though Inter has a 1: {Show()}");
        }));

        run("Fallback: a ZWJ sequence, a skin tone, a flag and a keycap each shape to one glyph", () => WithEmoji(() =>
        {
            using var fonts = new Fonts();
            foreach (var emoji in new[] { "🧑‍🚀", "👍🏽", "🇩🇰", "1️⃣", "😀" })
            {
                var run = fonts.SplitRuns(emoji, null, 400)!;
                var shaped = fonts.Shape(emoji, run.Face, 20f, run.Shaped);
                Check(shaped.Glyphs.Length == 1 && shaped.Glyphs[0] != 0, $"{emoji}: one glyph, not .notdef ({shaped.Glyphs.Length})");
                Check(Math.Abs(fonts.Measure(emoji, new Font(20f, null, 400, 1f)) - shaped.Width) < 1e-3f, $"{emoji}: measured as drawn");
            }
        }));

        run("Fallback: a width measured up to any grapheme boundary is the sum of what is drawn before it", () => WithEmoji(() =>
        {
            using var fonts = new Fonts();
            var font = new Font(14f, null, 400, 1.25f);
            float whole = fonts.Measure(Sample, font);
            float sum = 0f;
            for (var r = fonts.SplitRuns(Sample, null, 400); r is not null; r = r.Next)
                sum += fonts.Shape(Sample.AsSpan(r.Start, r.End - r.Start), r.Face, 14f * 1.25f, r.Shaped).Width / 1.25f;
            Check(Math.Abs(whole - sum) < 1e-3f, $"the line is its runs ({whole} vs {sum})");
            float last = 0f;
            for (int i = 0; i < Sample.Length; i += System.Globalization.StringInfo.GetNextTextElementLength(Sample.AsSpan(i)))
            {
                float w = fonts.Measure(Sample.AsSpan(0, i), font);
                Check(w >= last, $"widths grow at boundary {i}");
                last = w;
            }
            float emoji = fonts.Measure("👍🏽", font);
            Check(emoji > 14f && emoji < 14f * 1.5f, $"an emoji is about an em and a quarter wide, not three boxes: {emoji}");
        }));

        run("Fallback: OffsetAt lands on a grapheme boundary, never inside an emoji", () => WithEmoji(() =>
        {
            using var fonts = new Fonts();
            var font = new Font(14f, null, 400, 1f);
            float start = fonts.Measure("hi ", font), astronaut = fonts.Measure("🧑‍🚀", font);
            Check(fonts.OffsetAt(Sample, font, start + astronaut * 0.3f) == 3, "before the astronaut, left of its middle");
            Check(fonts.OffsetAt(Sample, font, start + astronaut * 0.7f) == 3 + "🧑‍🚀".Length, "after all five chars of it, right of its middle");
        }));

        run("Fallback: registering one drops what was measured before it", () =>
        {
            FontRegistry.Clear();
            using var fonts = new Fonts();
            var font = new Font(14f, null, 400, 1f);
            float boxes = fonts.Measure("🧑‍🚀", font);
            FontRegistry.RegisterFallback(EmojiFont);
            try
            {
                float emoji = fonts.Measure("🧑‍🚀", font);
                Check(Math.Abs(emoji - boxes) > 1f, $"the width is the emoji's now ({boxes} → {emoji})");
                FontRegistry.RegisterFallback(EmojiFont);   // the same bytes again: added once
                Check(fonts.SplitRuns("🧑‍🚀", null, 400)!.Face.IsColor, "still the colour face");
            }
            finally { FontRegistry.Clear(); }
        });

        run("Fallback: characters wrap a grapheme at a time, so a ZWJ sequence is never split across lines", () => WithEmoji(() =>
        {
            using var fonts = new Fonts();
            var font = new Font(14f, null, 400, 1f);
            const string text = "🧑‍🚀🧑‍🚀🧑‍🚀🧑‍🚀";
            float one = fonts.Measure("🧑‍🚀", font);
            for (var line = fonts.Wrap(text, font, one * 1.5f, TextWrap.Characters); line is not null; line = line.Next)
                Check(line.End - line.Start == "🧑‍🚀".Length, $"one astronaut a line: [{line.Start}, {line.End})");
        }));

        void Draws(string name, Func<DrawToPixels> renderer)
        {
            run($"Colour glyphs: {name} draws an emoji in its colours, and the text beside it in the text colour", () => WithEmoji(() =>
            {
                var runtime = BibuiRuntime.Create();
                runtime.SetBuild(() =>
                {
                    var row = runtime.Row().Sized(Size.Px(240f), Size.Px(40f));
                    row.Style.BackgroundColor = UiColor.Rgba(255, 255, 255, 255);
                    using (runtime.Parent(row)) runtime.Text("ab 😀 👍🏽 🧑‍🚀 🇩🇰 1️⃣ x");
                    return row;
                }, new SurfaceOptions { Width = 240, Height = 40, ScaleFactor = 2f });
                runtime.Step();
                var glyphOps = new List<(FontFace Face, int Count)>();
                var list = runtime.DrawList;
                for (int i = 0; i < list.Ops.Length; i++)
                    if (list.Ops[i].Kind == DrawOpKind.Glyphs) glyphOps.Add((list.FaceAt(list.Ops[i].Resource), list.Ops[i].Count));
                Check(glyphOps.Any(g => g.Face.IsColor), "a Glyphs op in the colour face");
                var frame = runtime.LastFrame(renderer());
                int coloured = 0;
                var px = frame.RawBgraBytes;
                for (int i = 0; i < px.Length; i += 4)
                {
                    int b = px[i], g = px[i + 1], r = px[i + 2];
                    if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 80) coloured++;
                }
                Check(coloured > 100, $"saturated pixels where the emoji are, not grey boxes: {coloured}");
            }));
        }
        Draws("Skia", () => Frames.Skia.Draw);
        var managed = new ManagedRenderer();
        Draws("the managed rasterizer", () => managed.Draw);
        RegisterColr(run);

        run("Colour glyphs: a bitmap strike is scaled to the size asked, from its 109 px", () => WithEmoji(() =>
        {
            using var fonts = new Fonts();
            var run = fonts.SplitRuns("😀", null, 400)!;
            var shaped = fonts.Shape("😀", run.Face, 20f, run.Shaped);
            var mask = run.Face.Strike(20f).GetMask(shaped.Glyphs[0], 0);
            Check(mask.IsColor && mask.Coverage.IsEmpty, "an image, not coverage");
            Check(mask.Width is >= 18 and <= 26 && mask.Height is >= 18 and <= 26, $"about 20 px square: {mask.Width}×{mask.Height}");
            Check(mask.Pixels.Length == mask.Width * mask.Height * 4, "4 bytes a pixel");
            bool premultiplied = true;
            for (int i = 0; i < mask.Pixels.Length; i += 4)
                premultiplied &= mask.Pixels[i] <= mask.Pixels[i + 3] && mask.Pixels[i + 1] <= mask.Pixels[i + 3] && mask.Pixels[i + 2] <= mask.Pixels[i + 3];
            Check(premultiplied, "no channel above its alpha");
        }));
    }

    /// <summary>A COLRv0 face (layers of outlines, each in its palette colour): drawn as an image too.</summary>
    public static void RegisterColr(Action<string, Action> run) => run("Colour glyphs: a COLR glyph's layers are blended into one image", () =>
    {
        FontRegistry.Clear();
        FontRegistry.RegisterFallback(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoZnamenny-COLR-subset.ttf")));
        try
        {
            using var fonts = new Fonts();
            string text = char.ConvertFromUtf32(0x1CF00);
            var r = fonts.SplitRuns(text, null, 400)!;
            Check(r.Face.IsColor && r.Shaped, "the COLR face, shaped");
            var glyph = fonts.Shape(text, r.Face, 40f, r.Shaped).Glyphs[0];
            var mask = r.Face.Strike(40f).GetMask(glyph, 0);
            Check(mask.IsColor && !mask.IsEmpty, $"an image: {mask.Width}×{mask.Height}");
        }
        finally { FontRegistry.Clear(); }
    });

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
