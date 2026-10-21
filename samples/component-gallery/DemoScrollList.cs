using Bibui.Core;
using Bibui.Core.Runtime;

namespace ComponentGallery;

// A hundred thousand rows, of which only the screenful in sight is built. The
// list scrolls by rows: the wheel and the bar move a ScrollPt the page keeps,
// and every row is one pitch tall. Beside it, rows of one to three lines: the
// list measures each row it builds and estimates the rest (RowHeights).
internal static class DemoScrollList
{
    private const int Rows = 100_000;
    private const float Pitch = 28f;

    private static ScrollPt _scroll;   // the page's, between frames
    private static ScrollPt _uneven;
    private static readonly RowHeights _heights = new(40f);

    // One, two or three lines, in an irregular pattern.
    private static int LinesOf(int row) => 1 + (row * 7 + row / 5) % 3;

    internal static void Build(BibuiRuntime runtime)
    {
        // The caption counts the rows the list built last frame, which scrolling
        // asks again for: it is above the list, so it is built first.
        Bold(runtime, $"{Rows:N0} rows of {Pitch}px, about {_scroll.Last - _scroll.First} built. Wheel, drag the thumb, or press the track to page.",
            ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(16f);
        var panels = runtime.BeginParent(runtime.Column());
        var panel = runtime.BoxFromId("sl-panel");
        (panel.SizeX, panel.SizeY) = (SemanticSize.Pixels(420f), SemanticSize.Pixels(320f));
        panel.Style.Draw = DrawFlags.Border;
        panel.Style.CornerRadius = 8f;
        panel.Style.Padding = EdgeInsets.All(1f);   // inside the border

        runtime.BeginParent(panel);
        runtime.BeginScrollList("sl-list", ref _scroll, Pitch, Rows);
        for (int i = _scroll.First; i < _scroll.Last; i++)
        {
            var row = runtime.Row().Sized(Size.Fill, Size.Px(Pitch));
            row.Style.Padding = new EdgeInsets(0, 12, 0, 12);
            row.Style.BackgroundColor = i % 2 == 0 ? (UiColor?)UiColor.Surface : null;
            runtime.BeginParent(row);
            runtime.Text(RowLabels.Of(i), ComponentSize.Sm, TextColor.Primary);
            runtime.EndParent(row);
        }
        runtime.EndScrollList(ref _scroll);
        runtime.EndParent(panel);
        runtime.Gap(16f);
        Bold(runtime, "Rows of one to three lines: each measured when it is built, the rest estimated.", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(8f);

        var uneven = runtime.BoxFromId("sl-uneven-panel");
        (uneven.SizeX, uneven.SizeY) = (SemanticSize.Pixels(420f), SemanticSize.Pixels(320f));
        uneven.Style.Draw = DrawFlags.Border;
        uneven.Style.CornerRadius = 8f;
        uneven.Style.Padding = EdgeInsets.All(1f);
        runtime.BeginParent(uneven);
        runtime.BeginScrollList("sl-uneven", ref _uneven, _heights, Rows);
        for (int i = _uneven.First; i < _uneven.Last; i++)
        {
            var holder = runtime.BeginScrollRow(ref _uneven, i);
            var row = runtime.Column().Sized(Size.Fill);
            row.Style.Padding = new EdgeInsets(6, 12, 6, 12);
            row.Style.BackgroundColor = i % 2 == 0 ? (UiColor?)UiColor.Surface : null;
            runtime.BeginParent(row);
            runtime.Text(RowLabels.Of(i), ComponentSize.Sm, TextColor.Primary);
            for (int l = 1; l < LinesOf(i); l++)
                runtime.Text(l == 1 ? "A second line, so this row is taller" : "and a third", ComponentSize.Sm, TextColor.Secondary);
            runtime.EndParent(row);
            runtime.EndScrollRow(holder);
        }
        runtime.EndScrollList(ref _uneven);
        runtime.EndParent(uneven);
        runtime.EndParent(panels);
    }

    // A row's label, made once per row rather than once a frame.
    private static class RowLabels
    {
        private static readonly Dictionary<int, string> Labels = [];

        internal static string Of(int row) =>
            Labels.TryGetValue(row, out var label) ? label : Labels[row] = $"Row {row + 1:N0}";
    }
}
