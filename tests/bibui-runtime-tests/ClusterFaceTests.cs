using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Text;
using Bibui.Text.FreeType;

/// <summary>
/// The face grapheme clusters are set in: the built-in Inter until a platform
/// sets up the system's colour emoji font (Fonts.UseClusterFont; on Linux,
/// Fontconfig.UseColorEmoji), and a strike that loads a face with bitmaps and no
/// outlines (CBDT, Noto Color Emoji). The fixture is a subset of Noto Color
/// Emoji with one bitmap size, 109 px.
/// </summary>
static class ClusterFaceTests
{
    static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoColorEmoji-subset.ttf");

    public static void Register(Action<string, Action> run)
    {
        run("Cluster face: with no platform setting one up, it is the built-in Inter", () =>
        {
            using var fonts = new Fonts();
            Check(fonts.ClusterFace == TextLayer.FaceBuiltIn(fonts, 400), "a new Fonts sets clusters in Inter");
        });

        run("Cluster face: Fontconfig.UseColorEmoji sets the system's colour emoji font, or leaves Inter when there is none", () =>
        {
            string path = Fontconfig.ColorEmojiFile;
            using var fonts = new Fonts();
            Fontconfig.UseColorEmoji(fonts);
            if (!OperatingSystem.IsLinux()) Check(path == "", "fontconfig answers on Linux only");
            if (path == "")
            {
                Check(fonts.ClusterFace == TextLayer.FaceBuiltIn(fonts, 400), "with no colour emoji font, the cluster face stays Inter");
                return;
            }
            Check(File.Exists(path), $"fontconfig's match is a file: {path}");
            Check(fonts.ClusterFace != TextLayer.FaceBuiltIn(fonts, 400), "the cluster face is the font fontconfig found");
            Check(fonts.ClusterFace.FreeType.HasColor, "and it has colour glyphs");
            Check(fonts.ClusterFace.HasGlyph(0x1F44D), "and it has 👍");

            using var other = new Fonts();
            Fontconfig.UseColorEmoji(other);
            Check(other.ClusterFace != fonts.ClusterFace, "another Fonts opens a face of its own, in its own engine");
            Check(ReferenceEquals(other.ClusterFace.FreeType.Data, fonts.ClusterFace.FreeType.Data), "from the same bytes, read once a process");
        });

        run("Cluster face: UseClusterFont takes a font's bytes; bytes that are no font leave the face as it was", () =>
        {
            using var fonts = new Fonts();
            var inter = TextLayer.FaceBuiltIn(fonts, 400);
            fonts.UseClusterFont(new FontData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OFL.txt"))));
            Check(fonts.ClusterFace == inter, "a file that is no font leaves Inter");

            var emoji = FontData.FromFile(Fixture);
            fonts.UseClusterFont(emoji);
            var face = fonts.ClusterFace;
            Check(face != inter && face.HasGlyph(0x1F44D), "the fixture is the cluster face");
            fonts.UseClusterFont(emoji);
            Check(fonts.ClusterFace == face, "the same bytes again are the same face");
            fonts.UseClusterFont(new FontData("not a font"u8));
            Check(fonts.ClusterFace == face, "bytes that are no font leave the fixture");
        });

        run("Walk: plain text is one run in the text's face, as before", () =>
        {
            var (fonts, style, runs) = Walk("Hello, wörld → done");
            using var _ = fonts;
            Check(runs.Length == 1 && runs[0].Style == style && runs[0].X == 0f, Describe(fonts, runs));
            Check(runs[0].Width == TextLayer.WidthFromChars(fonts, "Hello, wörld → done", style, 1f), "as wide as WidthFromChars says");
        });

        run("Walk: with no cluster face set up, an emoji stays in the text's face, as before", () =>
        {
            var (fonts, style, runs) = Walk("ok 👍", emoji: false);
            using var _ = fonts;
            Check(runs.Length == 1 && runs[0].Style == style, Describe(fonts, runs));
        });

        run("Walk: an emoji is a run of its own in the cluster face, from where the text's run ends", () =>
        {
            var (fonts, style, runs) = Walk("ok 👍 done");
            using var _ = fonts;
            Check(runs.Length == 3, Describe(fonts, runs));
            Check(runs[0].Style == style && runs[0].GlyphCount == 3 && runs[0].X == 0f, "ok and the space in the text's face: " + Describe(fonts, runs));
            Check(runs[1].Style.Face == fonts.ClusterFace && runs[1].GlyphCount == 1, "👍 in the cluster face: " + Describe(fonts, runs));
            Check(runs[1].X == runs[0].Width && runs[2].X == runs[1].X + runs[1].Width, "each run starts where the one before ends");
            Check(runs[2].Style == style && runs[2].GlyphCount == 5, "the space and done in the text's face again");
            Check(runs[1].Width > 0f, "and the emoji has a width");
        });

        run("Walk: a text that starts with an emoji still has its own style first, with no glyphs", () =>
        {
            var (fonts, style, runs) = Walk("👍 ok");
            using var _ = fonts;
            Check(runs.Length == 3 && runs[0].Style == style && runs[0].GlyphCount == 0, Describe(fonts, runs));
            Check(runs[1].Style.Face == fonts.ClusterFace && runs[2].Style == style, Describe(fonts, runs));
        });

        run("Walk: clusters next to each other share a run; a sequence is shaped whole", () =>
        {
            foreach (var (text, what, glyphs) in new[] { ("👍🏽", "a skin tone", 1), ("🇩🇰", "a flag", 1), ("1\uFE0F\u20E3", "a keycap, after its digit", 1),
                ("🧑\u200D🚀", "a ZWJ sequence", 1), ("❤\uFE0F", "a heart asking for emoji", 1), ("👍😀🚀", "three emoji", 3) })
            {
                var (fonts, style, runs) = Walk("a" + text + "b");
                using var _ = fonts;
                Check(runs.Length == 3 && runs[1].Style.Face == fonts.ClusterFace, $"{what}: one run in the cluster face, between the text's: {Describe(fonts, runs)}");
                Check(runs[0].GlyphCount == 1 && runs[2].GlyphCount == 1, $"{what}: a and b in the text's face: {Describe(fonts, runs)}");
                Check(runs[1].GlyphCount == glyphs, $"{what}: {glyphs} glyph(s), the font's ligature for each sequence: {Describe(fonts, runs)}");
            }
        });

        run("Walk: Skia draws an emoji's run in colour, from the cluster face, with nothing of its own to change", () =>
        {
            int ColourPixels(string text)
            {
                var runtime = BibuiRuntime.Create();
                runtime.Fonts.UseClusterFont(FontData.FromFile(Fixture));
                runtime.SetBuild(() =>
                {
                    var row = runtime.Row().Sized(Size.Px(120f), Size.Px(40f));
                    using (runtime.Parent(row)) runtime.Text(text);
                    return row;
                }, TestBuild.Window(120, 40));
                var bgra = runtime.Frame().RawBgraBytes;
                int coloured = 0;
                for (int p = 0; p + 3 < bgra.Length; p += 4)
                {
                    int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                    if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 80) coloured++;
                }
                return coloured;
            }
            int plain = ColourPixels("ok"), emoji = ColourPixels("ok 👍");
            Check(plain == 0, $"text alone draws no strongly coloured pixel (got {plain})");
            Check(emoji > 40, $"the thumb draws in its own colours (got {emoji} strongly coloured pixels)");
        });

        run("Strike: a face with bitmaps and no outlines loads at any size, its metrics from its bitmap size", () =>
        {
            using var fonts = new Fonts();
            fonts.UseClusterFont(FontData.FromFile(Fixture));
            var face = fonts.ClusterFace;
            var ft = face.FreeType;
            Check(!ft.IsScalable && ft.HasColor, "the fixture is a colour face with no outlines");
            Check(ft.FixedSizes.Length == 1 && ft.FixedSizes[0] == 109f, $"with one bitmap size, 109 px (got {string.Join(", ", ft.FixedSizes)})");

            foreach (float size in (float[])[16f, 17.5f, 109f, 200f])
            {
                var strike = face.Strike(size);
                var m = strike.Metrics;
                Check(strike.BitmapScale == size / 109f, $"at {size} px its bitmaps are scaled from 109 px");
                Check(float.IsFinite(m.Ascent) && float.IsFinite(m.Descent) && float.IsFinite(m.Leading), $"at {size} px its metrics are numbers");
                Check(m.Ascent < -0.6f * size && m.Ascent > -1.2f * size, $"at {size} px about an em above the baseline (ascent {m.Ascent})");
                Check(m.Descent > 0f && m.Descent < 0.5f * size, $"at {size} px a little below it (descent {m.Descent})");
            }
            Check(TextLayer.FaceBuiltIn(fonts, 400).Strike(16f).BitmapScale == 1f, "an outline face's strike scales nothing");
        });
    }

    // The runs a text is walked into, in a Fonts whose cluster face is the fixture (or Inter), at 16 px.
    static (Fonts Fonts, FontStyle Text, TextRun[] Runs) Walk(string text, bool emoji = true)
    {
        var fonts = new Fonts();
        if (emoji) fonts.UseClusterFont(FontData.FromFile(Fixture));
        TextLayer.RunsBegin(fonts);
        var style = TextLayer.StyleFromFaceSize(fonts, TextLayer.FaceBuiltIn(fonts, 400), 16f);
        var runs = TextLayer.RunsOf(fonts, TextLayer.RunsFromChars(fonts, text, style)).ToArray();
        return (fonts, style, runs);
    }

    static string Describe(Fonts fonts, TextRun[] runs) => string.Join(" | ", runs.Select(r =>
        $"{(r.Style.Face == fonts.ClusterFace && fonts.ClusterFace != TextLayer.FaceBuiltIn(fonts, 400) ? "emoji" : "text")} {r.GlyphCount} glyphs at {r.X:F2} wide {r.Width:F2}"));

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
