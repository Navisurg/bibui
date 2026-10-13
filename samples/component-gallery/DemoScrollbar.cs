using Bibui;
using Bibui.Runtime;

namespace ComponentGallery;

// A scrolling view and its bar, along each axis. The view and the bar are
// acquired under one panel laid out across the bar's axis, so the bar takes a
// lane beside the view rather than covering it.
internal static class DemoScrollbar
{
    internal static UiNode Build(BibuiRuntime runtime) => Arrange.Column(
        Caption(runtime, "Vertical: 40 rows in a 200px view. Wheel, drag the thumb, or press the track to page."),
        Arrange.Space(16f),
        Vertical(runtime),
        Arrange.Space(16f),
        Caption(runtime, "Horizontal: 24 cards in a 360px strip. Shift+wheel, drag the thumb, or press the track to page."),
        Arrange.Space(16f),
        Horizontal(runtime));

    private static UiNode Vertical(BibuiRuntime runtime)
    {
        var panel = runtime.BoxFromId("sb-v-panel");
        var view = runtime.BoxFromId("sb-v-view",
            InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip, panel);
        view.ChildLayoutAxis = LayoutAxis.Y;
        for (int i = 1; i <= 40; i++)
        {
            var row = runtime.BoxFromId($"sb-v-row-{i}", parent: view);
            (row.SizeX, row.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.Pixels(28f));
            row.Style.Padding = new EdgeInsets(0, 12, 0, 12);
            row.Style.BackgroundColor = i % 2 == 0 ? (UiColor?)UiColor.Surface : null;
            using (runtime.PushParent(row))
                runtime.Text(U8($"Row {i}"), ComponentSize.Sm, TextColor.Primary);
        }
        using (runtime.PushParent(panel))
            Scrollbar.Build(runtime, "sb-v-bar", view, LayoutAxis.Y);
        runtime.InteractionsFor(view);

        (view.SizeX, view.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
        view.AllowOverflowY = true;
        Frame(panel, 360f, 200f);
        return panel;
    }

    private static UiNode Horizontal(BibuiRuntime runtime)
    {
        var panel = runtime.BoxFromId("sb-h-panel");
        panel.ChildLayoutAxis = LayoutAxis.Y;
        var view = runtime.BoxFromId("sb-h-view",
            InteractionKinds.ViewScrollX | InteractionKinds.ClampX | InteractionKinds.Clip, panel);
        for (int i = 1; i <= 24; i++)
        {
            if (i > 1) Arrange.Gap(view, 8f);
            var card = runtime.BoxFromId($"sb-h-card-{i}", parent: view);
            (card.SizeX, card.SizeY) = (SemanticSize.Pixels(80f), SemanticSize.ParentPct(1f));
            card.Style.CornerRadius = 6f;
            card.Style.Draw = DrawFlags.Background;
            using (runtime.PushParent(card))
                runtime.Text(U8($"Card {i}"), ComponentSize.Sm, TextColor.Primary);
        }
        using (runtime.PushParent(panel))
            Scrollbar.Build(runtime, "sb-h-bar", view, LayoutAxis.X);
        runtime.InteractionsFor(view);

        (view.SizeX, view.SizeY) = (SemanticSize.ParentPct(1f), SemanticSize.ParentPct(1f));
        view.AllowOverflowX = true;
        view.Style.Padding = new EdgeInsets(8, 4, 8, 4);
        Frame(panel, 360f, 74f + Scrollbar.Lane);
        return panel;
    }

    private static void Frame(Box panel, float width, float height)
    {
        (panel.SizeX, panel.SizeY) = (SemanticSize.Pixels(width), SemanticSize.Pixels(height));
        panel.Style.Draw = DrawFlags.Border;
        panel.Style.CornerRadius = 8f;
        panel.Style.Padding = EdgeInsets.All(1f);   // inside the border
    }

    private static Text Caption(BibuiRuntime runtime, string text) =>
        Bold(runtime, U8(text), ComponentSize.Sm, TextColor.Secondary);
}
