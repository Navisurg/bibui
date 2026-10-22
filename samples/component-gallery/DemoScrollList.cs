using Bibui.Core;
using Bibui.Core.Runtime;

namespace ComponentGallery;

// Two lists of a hundred thousand rows, of which only the screenful in sight
// is built, each with its bar beside it. RowScrollList: every row one pitch
// tall, so everything follows from the pitch. ScrollList: rows of one to three
// lines, each row's height asked as the list reaches it. The switch scrolls
// both by pixels instead of by rows.
internal static class DemoScrollList
{
    private const int Rows = 100_000;
    private const float Pitch = 28f;     // a row of the first list: one Sm line
    private const float PadY = 6f;       // a row of the second: a Md line and up to two Sm lines, in this padding
    private const float RowStep = 32f;   // the second list by rows: a notch of the wheel moves it a row

    private static ScrollPt _even, _uneven;   // the page's, between frames
    private static bool _byPixels;
    private static int _evenBuilt, _unevenBuilt;   // last frame's, for the captions, which are built first

    // One, two or three lines, in an irregular pattern.
    private static int LinesOf(int row) => 1 + (row * 7 + row / 5) % 3;

    internal static void Build(BibuiRuntime runtime)
    {
        if (runtime.Switch("sl-by", _byPixels, label: "Scroll by pixels").Clicked) _byPixels = !_byPixels;
        ScrollBy by = _byPixels ? ScrollBy.Pixels : ScrollBy.Rows;
        runtime.Gap(16f);

        // ── RowScrollList: rows of one pitch ─────────────────────────────────
        Bold(runtime, $"RowScrollList: {Rows:N0} rows of {Pitch}px, {_evenBuilt} built.", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(8f);
        var evenArea = Panel(runtime, "sl-panel");
        var (first, last) = runtime.BeginRowScrollList("sl-list", ref _even, Pitch, Rows, by);
        for (int i = first; i < last; i++)
        {
            var row = runtime.Row().Sized(Size.Fill, Size.Px(Pitch));
            row.Style.Padding = new EdgeInsets(0, 12, 0, 12);
            row.Style.BackgroundColor = i % 2 == 0 ? (UiColor?)UiColor.Surface : null;
            runtime.BeginParent(row);
            runtime.Text(RowLabels.Of(i), ComponentSize.Sm, TextColor.Primary);
            runtime.EndParent(row);
        }
        _evenBuilt = last - first;
        runtime.EndRowScrollList(ref _even, Pitch, Rows, by);
        runtime.Scrollbar("sl-bar", ref _even, Rows, first, last);
        runtime.EndParent(evenArea);
        runtime.EndParent(evenArea.Parent!);
        runtime.Gap(16f);

        // ── ScrollList: rows of their own heights ────────────────────────────
        // A row's height follows from what it holds, known before it is built.
        float primary = runtime.Fonts.LineHeight(runtime.FontInScope(ComponentSize.Md));
        float secondary = runtime.Fonts.LineHeight(runtime.FontInScope(ComponentSize.Sm));
        Bold(runtime, $"ScrollList: {Rows:N0} rows of one to three lines, {_unevenBuilt} built.", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(8f);
        var unevenArea = Panel(runtime, "sl-uneven-panel");
        var rows = runtime.BeginScrollList("sl-uneven", ref _uneven, Rows, by, RowStep);
        for (int i = rows.Row; rows.More; i = rows.Row)
        {
            float height = 2f * PadY + primary + (LinesOf(i) - 1) * secondary;
            if (!rows.Next(ref _uneven, height)) continue;   // a row stepped onto or past, not built
            var row = runtime.Column().Sized(Size.Fill, Size.Px(height));
            row.Style.Padding = new EdgeInsets(PadY, 12, PadY, 12);
            row.Style.BackgroundColor = i % 2 == 0 ? (UiColor?)UiColor.Surface : null;
            runtime.BeginParent(row);
            runtime.Text(RowLabels.Of(i), ComponentSize.Md, TextColor.Primary);
            for (int l = 1; l < LinesOf(i); l++)
                runtime.Text(l == 1 ? "A second line, so this row is taller" : "and a third", ComponentSize.Sm, TextColor.Secondary);
            runtime.EndParent(row);
        }
        _unevenBuilt = rows.Built;
        int top = _uneven.Idx;   // the row at the top, before End moves it for the next frame
        runtime.EndScrollList(ref _uneven, rows);
        runtime.Scrollbar("sl-uneven-bar", ref _uneven, Rows, top, top + rows.Built);
        runtime.EndParent(unevenArea);
        runtime.EndParent(unevenArea.Parent!);
    }

    // A bordered panel, and a row in it to lay a list and its bar out side by
    // side; both pushed, the row returned (its parent is the panel).
    private static Box Panel(BibuiRuntime runtime, string id)
    {
        var panel = runtime.BoxFromId(id);
        (panel.SizeX, panel.SizeY) = (SemanticSize.Pixels(420f), SemanticSize.Pixels(320f));
        panel.Style.Draw = DrawFlags.Border;
        panel.Style.CornerRadius = 8f;
        panel.Style.Padding = EdgeInsets.All(1f);   // inside the border
        runtime.BeginParent(panel);
        return runtime.BeginParent(runtime.Row().Sized(Size.Fill, Size.Fill));
    }

    // A row's label, made once per row rather than once a frame.
    private static class RowLabels
    {
        private static readonly Dictionary<int, string> Labels = [];

        internal static string Of(int row) =>
            Labels.TryGetValue(row, out var label) ? label : Labels[row] = $"Row {row + 1:N0}";
    }
}
