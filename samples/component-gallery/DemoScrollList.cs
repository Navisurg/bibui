using Bibui;
using Bibui.Runtime;

namespace ComponentGallery;

// A hundred thousand rows, of which only the screenful in sight is built. The
// list scrolls by rows: the wheel and the bar move a ScrollPt the page keeps,
// and every row is one pitch tall.
internal static class DemoScrollList
{
    private const int Rows = 100_000;
    private const float Pitch = 28f;

    private static ScrollPt _scroll;   // the page's, between frames

    internal static void Build(BibuiRuntime runtime)
    {
        var panel = runtime.BoxFromId("sl-panel");
        (panel.SizeX, panel.SizeY) = (SemanticSize.Pixels(420f), SemanticSize.Pixels(320f));
        panel.Style.Draw = DrawFlags.Border;
        panel.Style.CornerRadius = 8f;
        panel.Style.Padding = EdgeInsets.All(1f);   // inside the border

        using (runtime.PushParent(panel))
        {
            runtime.BeginScrollList("sl-list", ref _scroll, Pitch, Rows);
            for (int i = _scroll.First; i < _scroll.Last; i++)
            {
                var row = runtime.RowFillX(Pitch);
                row.Style.Padding = new EdgeInsets(0, 12, 0, 12);
                row.Style.BackgroundColor = i % 2 == 0 ? (UiColor?)UiColor.Surface : null;
                using (runtime.PushParent(row))
                    runtime.Text(RowLabels.Of(i), ComponentSize.Sm, TextColor.Primary);
            }
            runtime.EndScrollList(ref _scroll);
        }

        // The caption counts the rows the list built, so it is built after the
        // list, and the panel is moved after it.
        Bold(runtime, $"{Rows:N0} rows of {Pitch}px, about {_scroll.Last - _scroll.First} built. Wheel, drag the thumb, or press the track to page.",
            ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(16f);
        UiNode.PushIntoParent(runtime.ParentInScope, panel);
    }

    // A row's label, made once per row rather than once a frame.
    private static class RowLabels
    {
        private static readonly Dictionary<int, string> Labels = [];

        internal static string Of(int row) =>
            Labels.TryGetValue(row, out var label) ? label : Labels[row] = $"Row {row + 1:N0}";
    }
}
