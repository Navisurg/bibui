using Bibui.Core;
using Bibui.Core.Draw;
using Bibui.Core.Runtime;
using Bibui.Raster.Managed.Backend;
using Size = Bibui.Core.Size;
using Bibui.Text;
using Bibui.Text.FreeType;

/// <summary>
/// The face grapheme clusters are set in: the built-in Inter until a platform
/// sets up the system's colour emoji font (Fonts.UseClusterFont; on Linux,
/// Fontconfig.UseColorEmoji), and a strike that loads a face with bitmaps and no
/// outlines (CBDT, Noto Color Emoji). The fixture is a subset of Noto Color
/// Emoji with one bitmap size, 109 px. Colour glyphs are drawn as images by
/// both renderers; the COLR fixture is a subset of Noto Znamenny.
/// </summary>
static class ClusterFaceTests
{
    static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoColorEmoji-subset.ttf");
    static readonly string ColrFixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoZnamenny-COLR-subset.ttf");

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

        // Both renderers draw an emoji's run in colour from the cluster face: Skia from
        // the face as it is, the managed rasterizer from the strike's colour masks.
        void Draws(string name, DrawToPixels draw) => run($"Walk: {name} draws an emoji's run in colour, from the cluster face", () =>
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
                return Coloured(runtime.Frame(draw).RawBgraBytes);
            }
            int plain = ColourPixels("ok"), emoji = ColourPixels("ok 👍");
            Check(plain == 0, $"text alone draws no strongly coloured pixel (got {plain})");
            Check(emoji > 40, $"the thumb draws in its own colours (got {emoji} strongly coloured pixels)");
        });
        Draws("Skia", Frames.Skia.Draw);
        Draws("the managed rasterizer", new ManagedRenderer().Draw);

        // The bitmap is 109 px; drawn unscaled from its top, it would cover the lines
        // around it. Where Skia puts the emoji is where the text layer laid it out.
        run("Colour glyphs: the managed rasterizer draws an emoji at the text's size, where Skia draws it", () =>
        {
            var runtime = BibuiRuntime.Create();
            runtime.Fonts.UseClusterFont(FontData.FromFile(Fixture));
            runtime.SetBuild(() =>
            {
                var col = runtime.Column().Sized(Size.Px(200f), Size.Px(120f));
                col.Style.BackgroundColor = UiColor.Rgba(255, 255, 255, 255);
                using (runtime.Parent(col))
                {
                    runtime.Text("a line above");
                    runtime.Text("ok 👍 ok");
                    runtime.Text("a line below");
                }
                return col;
            }, TestBuild.Window(200, 120, 2f));
            runtime.Step();
            var skia = ColouredBounds(runtime.LastFrame(Frames.Skia.Draw));
            var managed = ColouredBounds(runtime.LastFrame(new ManagedRenderer().Draw));
            Check(skia.Right > skia.Left, "Skia draws the emoji");
            Check(Math.Abs(managed.Left - skia.Left) <= 2 && Math.Abs(managed.Top - skia.Top) <= 2
                && Math.Abs(managed.Right - skia.Right) <= 2 && Math.Abs(managed.Bottom - skia.Bottom) <= 2,
                $"the managed rasterizer's emoji spans {managed}, Skia's {skia}");
        });

        run("Colour glyphs: a bitmap glyph is an image, scaled to the strike's size from its 109 px", () =>
        {
            using var fonts = new Fonts();
            fonts.UseClusterFont(FontData.FromFile(Fixture));
            var strike = fonts.ClusterFace.Strike(20f);
            var placed = Placed(strike, fonts.ClusterFace.FreeType.GlyphIndex(0x1F600), 10.3f, 30.6f);
            var mask = placed.Mask;
            Check(mask.IsColor && mask.Coverage.IsEmpty, "an image, not coverage");
            Check(mask.Width is >= 18 and <= 26 && mask.Height is >= 18 and <= 26, $"about 20 px square: {mask.Width}×{mask.Height}");
            Check(mask.Pixels.Length == mask.Width * mask.Height * 4, "4 bytes a pixel");
            bool premultiplied = true;
            for (int i = 0; i < mask.Pixels.Length; i += 4)
                premultiplied &= mask.Pixels[i] <= mask.Pixels[i + 3] && mask.Pixels[i + 1] <= mask.Pixels[i + 3] && mask.Pixels[i + 2] <= mask.Pixels[i + 3];
            Check(premultiplied, "no channel above its alpha");
            Check(placed.X == 10 + mask.Left && placed.Y == 31 - mask.Top, $"placed on whole pixels: ({placed.X}, {placed.Y})");
        });

        run("Colour glyphs: a COLR glyph's layers are blended into one image", () =>
        {
            using var fonts = new Fonts();
            fonts.UseClusterFont(FontData.FromFile(ColrFixture));
            var face = fonts.ClusterFace;
            Check(face.FreeType.HasColor && face.FreeType.IsScalable, "the fixture is a colour face with outlines");
            var mask = Placed(face.Strike(40f), face.FreeType.GlyphIndex(0x1CF00), 0f, 40f).Mask;
            Check(mask.IsColor && !mask.IsEmpty, $"an image: {mask.Width}×{mask.Height}");
        });

        run("Colour glyphs: an emoji that appears in a damage-tracked frame is drawn, as in a whole one", () =>
        {
            var runtime = BibuiRuntime.Create();
            runtime.Fonts.UseClusterFont(FontData.FromFile(Fixture));
            bool shown = false;
            runtime.SetBuild(() =>
            {
                var row = runtime.Row().Sized(Size.Px(240f), Size.Px(40f));
                row.Style.BackgroundColor = UiColor.Rgba(255, 255, 255, 255);
                using (runtime.Parent(row)) runtime.Text(shown ? "ab 😀 👍🏽 x" : "ab x");
                return row;
            }, TestBuild.Window(240, 40, 2f));
            int w = 480, h = 80;
            var bytes = GC.AllocateArray<byte>(w * h * 4, pinned: true);
            var target = new RasterPixels(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(bytes, 0), w, h, w * 4);
            var tracked = new ManagedRenderer { DamageTracking = true };
            runtime.Step();
            tracked.Draw(runtime.DrawList, target);
            shown = true;
            runtime.Step();
            tracked.Draw(runtime.DrawList, target);
            Check(tracked.LastRepaint is { Count: > 0 }, $"the second frame was repainted in part ({tracked.LastFullRepaintReason})");
            var whole = runtime.LastFrame(new ManagedRenderer().Draw).RawBgraBytes;
            Check(whole.Length == bytes.Length, "the same size");
            int differ = 0;
            for (int i = 0; i < whole.Length; i += 4)
                if (BitConverter.ToUInt32(bytes, i) != BitConverter.ToUInt32(whole, i)) differ++;
            Check(differ == 0, $"{differ} px differ from a whole draw");
            Check(Coloured(bytes) > 40, "and the emoji are in it, in colour");
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

    // How many pixels are strongly coloured: an emoji's, never grey text's.
    static int Coloured(byte[] bgra)
    {
        int coloured = 0;
        for (int p = 0; p + 3 < bgra.Length; p += 4)
        {
            int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 80) coloured++;
        }
        return coloured;
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    // The box around a frame's strongly coloured pixels, in device pixels; empty when there are none.
    static (int Left, int Top, int Right, int Bottom) ColouredBounds(Pixels frame)
    {
        int left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
        var bgra = frame.RawBgraBytes;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                int p = (y * frame.Width + x) * 4;
                int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 80) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
            }
        return right == 0 ? (0, 0, 0, 0) : (left, top, right, bottom);
    }

    // One glyph of a strike as the managed rasterizer gets it: its mask, placed at (x, baselineY).
    static Bibui.Glyphs.PlacedGlyph Placed(FontStrike strike, uint glyph, float x, float baselineY)
    {
        var into = new Bibui.Glyphs.PlacedGlyph[1];
        Bibui.Glyphs.GlyphMasks.PlacedFromGlyphs(new(), strike, [(ushort)glyph], [System.Numerics.Vector2.Zero], x, baselineY, into, TextAntialias.Grayscale);
        return into[0];
    }
}
