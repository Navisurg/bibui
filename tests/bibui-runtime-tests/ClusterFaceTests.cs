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

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
