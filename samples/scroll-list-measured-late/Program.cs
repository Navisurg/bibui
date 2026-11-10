// A feed of posts in a ScrollList. A post is wrapped text, so its height is only known
// once it has been built and laid out: the row's box says, a frame late. Until then the
// loop hands in a guess, as the ScrollList docs have the caller do.
//
// The guess is wrong for every post the first frame it scrolls in. Where it is taller
// than the post, the rows built stop short of the view's bottom and that frame shows an
// empty band under the last row, though there are posts still below; the next frame,
// measured, closes it. Scrolling down, that is a flicker at the bottom edge.
//
//   dotnet run -c Release                 headless: scroll down two notches a frame, list each
//                                         frame with a gap, and save the worst as gap.png
//   dotnet run -c Release -- --window     the same feed in a window, to scroll by hand
using Bibui.App;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Raster.Skia;

const int Count = 300;
const float Guess = 160f;   // a post's height before it is measured: a tall guess
var heights = new Dictionary<int, float>();
var sources = Enumerable.Range(0, Count).Select(i => BibuiStr.FromStrLit(Post(i))).ToArray();
var scroll = new ScrollPt();
int lastBuilt = -1;

var runtime = BibuiRuntime.Create();
if (args.Contains("--window"))
{
    using var app = BibuiApp.Open(runtime, new("ScrollList, rows measured late", 520, 640));
    app.Frame = Frame;
    while (app.WaitForFrame()) Frame();

    void Frame()
    {
        runtime.BeginBuild(app.Window.Frame, app.Events);
        Build();
        runtime.EndBuild();
        app.Present();
    }
    return 0;
}

// Headless: a 520×640 window, two notches of the wheel down every frame, a brisk scroll.
var window = new WindowFrame(520, 640, 1f, 60f);
using var renderer = new SkiaCpuRenderer();
int gaps = 0;
float worst = 0f;
for (int frame = 0; frame < 400; frame++)
{
    var events = frame < 2 ? new List<WindowEvent>() : [WindowEvent.Wheel(260f, 320f, 0f, -2f)];
    runtime.BeginBuild(window, events);
    Build();
    runtime.EndBuild();

    // Laid out: where the last post built ends, against the view's bottom.
    var view = (Box)runtime.FindById("feed")!;
    var last = (Box)runtime.FindById("post-" + lastBuilt)!;
    float gap = view.Rect.Y + view.Extent.Y - (last.Rect.Y + last.Extent.Y);
    if (lastBuilt < Count - 1 && gap > 0.5f)
    {
        Console.WriteLine($"frame {frame,3}: posts {scroll.Idx}–{lastBuilt} built, {Count - 1 - lastBuilt} more below, and a {gap:0} px gap under the last");
        if (frame < 2) continue;   // the list's first frame: nothing measured yet
        gaps++;
        if (gap > worst)
        {
            worst = gap;
            File.WriteAllBytes("gap.png", FramePixels.Png(runtime, renderer.Draw));
        }
    }
}
Console.WriteLine(gaps == 0 ? "No frame showed a gap while scrolling." : $"{gaps} of 398 frames showed a gap while scrolling; the worst, {worst:0} px, is in gap.png.");
return gaps == 0 ? 0 : 1;

void Build()
{
    var page = runtime.Column().Sized(Size.Fill, Size.Fill);
    page.Style.BackgroundColor = UiColor.Surface;
    using var _ = runtime.Parent(page);
    var rows = runtime.BeginScrollList("feed", ref scroll, Count);
    var view = (Box)runtime.ParentInScope!;
    float width = view.Extent.X > 0f ? view.Extent.X - 40f : 480f;
    for (int i = rows.Row; rows.More; i = rows.Row)
    {
        float height = heights.TryGetValue(i, out var h) ? h : Guess;
        if (!rows.Next(ref scroll, height)) continue;
        var row = runtime.BoxFromId("post-" + i, InteractionKinds.None);
        row.ChildLayoutAxis = LayoutAxis.Y;
        row.Sized(Size.Fill, Size.Fit);
        row.Style.Padding = new EdgeInsets(10f, 20f, 10f, 20f);
        // Measured: the height this row was laid out at, known from its second frame on.
        if (row.Extent.Y > 0f && row.Extent.Y != height)
        {
            heights[i] = row.Extent.Y;
            runtime.RequestFrame();
        }
        using (runtime.Parent(row))
            runtime.Markdown("text-" + i, sources[i], width);
        lastBuilt = i;
    }
    runtime.EndScrollList(ref scroll, rows);
}

// Post i: a heading and one to six sentences, so posts differ in height.
static string Post(int i)
{
    string[] words = ["the", "list", "shows", "only", "rows", "in", "sight", "and", "each", "one", "is", "measured", "when", "built", "so", "its", "first", "frame", "uses", "a", "guess"];
    var text = new System.Text.StringBuilder($"**Post {i + 1}**\n\n");
    int sentences = 1 + (i * 7 % 6);
    for (int s = 0; s < sentences; s++)
    {
        int n = 8 + (i + s * 5) % 9;
        var sentence = string.Join(' ', Enumerable.Range(0, n).Select(k => words[(i * 3 + s * 11 + k * 5) % words.Length]));
        text.Append(char.ToUpperInvariant(sentence[0])).Append(sentence, 1, sentence.Length - 1).Append(". ");
    }
    return text.ToString();
}
