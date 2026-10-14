using Bibui;
using Bibui.Runtime;

namespace ComponentGallery;

/// <summary>What the Layout pages hold between frames.</summary>
internal sealed record LayoutState
{
    public int CarouselIndex { get; init; }
    public int StackClickCount { get; init; }
    public int BoxClickCount { get; init; }
    public string ColormapValue { get; init; } = "viridis";
    public float ColormapAlpha { get; init; } = 0.67f;

}

internal static class DemoLayout
{
    internal static UiNode Build(BibuiRuntime runtime, string route, LayoutState state, Action<LayoutState> set) => route switch
    {
    "SplitPane" => SplitPanePage(runtime),

    "Carousel" => CarouselPage(runtime, state, set),

    "ButtonStrip" => ButtonStripPage(runtime),

    "Stack" => StackPage(runtime, state, set),

    "InteractiveBox" => InteractiveBoxPage(runtime, state, set),

    "Section" => SectionPage(runtime, state, set),

    _ => runtime.Text("Select a component from the sidebar.", textColor: TextColor.Secondary),
    };

    // ── SplitPane ────────────────────────────────────────────────────────────

    // Where each split's divider sits, as a share of the room either side of
    // it: page state the gallery's record does not carry.
    private static readonly Dictionary<string, float> SplitFractions = new()
    {
        ["demo-split-h"] = 0.3f,
        ["demo-split-v"] = 0.4f,
    };

    private static UiNode SplitPanePage(BibuiRuntime runtime)
    {
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            DemoNavigation.Heading(runtime, "Two panes side by side: drag the divider between them");
            Split(runtime, "demo-split-h", "Sidebar", "Content", vertical: false);
            runtime.Gap(16f);
            DemoNavigation.Heading(runtime, "One above the other");
            Split(runtime, "demo-split-v", "Top", "Bottom", vertical: true);
        }
        return page;
    }

    private const float SplitHandle = 8f;

    // Two outlined panes, 560 by 200, either side of an 8 px divider that
    // takes the press: dragging it moves the split to under the pointer.
    private static void Split(BibuiRuntime runtime, string id, string first, string second, bool vertical)
    {
        const float Width = 560f, Height = 200f;
        float along = vertical ? Height : Width;
        float fraction = SplitFractions[id];

        // Retained, so its rect last frame is there to measure the pointer against.
        Box frame = runtime.BoxFromId(id);
        frame.ChildLayoutAxis = vertical ? LayoutAxis.Y : LayoutAxis.X;
        frame.Px(Width, Height);
        using (runtime.PushParent(frame))
        {
            // Sized once the divider has said where the split is now.
            Box firstPane = Pane(runtime, first);

            Box handle = runtime.BoxFromId(id + "-handle", InteractionKinds.LeftPress);
            var drag = runtime.InteractionsFor(handle);
            if (drag.Dragging && frame.Extent.X > 0f)
            {
                float start = vertical ? frame.Rect.Y : frame.Rect.X;
                float at = vertical ? drag.Y : drag.X;
                SplitFractions[id] = fraction = Math.Clamp((at - start - SplitHandle / 2f) / (along - SplitHandle), 0.1f, 0.9f);
            }
            handle.ChildLayoutAxis = vertical ? LayoutAxis.X : LayoutAxis.Y;
            if (vertical) { handle.SizeX = SemanticSize.ParentPct(1f); handle.SizeY = SemanticSize.Pixels(SplitHandle); }
            else { handle.SizeX = SemanticSize.Pixels(SplitHandle); handle.SizeY = SemanticSize.ParentPct(1f); }
            using (runtime.PushParent(handle))
            {
                // A grip in the middle of the divider, brighter while it is hot.
                Arrange.Spring(handle);
                Box grip = runtime.Box();
                grip.Px(vertical ? 32f : 4f, vertical ? 4f : 32f);
                grip.Style.CornerRadius = 9999f;
                grip.Style.BackgroundColor = handle.HotT > 0.5f || drag.Dragging ? UiColor.BorderHover : UiColor.Border;
                Arrange.Spring(handle);
            }

            Pane(runtime, second).Along(vertical, SemanticSize.ParentPct(1f));
            firstPane.Along(vertical, SemanticSize.Pixels((along - SplitHandle) * fraction));
        }
    }

    // Sets the size along the split, and all of the other axis.
    private static Box Along(this Box pane, bool vertical, SemanticSize size)
    {
        if (vertical) { pane.SizeX = SemanticSize.ParentPct(1f); pane.SizeY = size; }
        else { pane.SizeX = size; pane.SizeY = SemanticSize.ParentPct(1f); }
        return pane;
    }

    // An outlined pane with its label in the middle, sized by the caller.
    private static Box Pane(BibuiRuntime runtime, string label)
    {
        Box pane = runtime.Box();
        pane.ChildLayoutAxis = LayoutAxis.Y;
        pane.Style.BorderColor = UiColor.Border;
        pane.Style.BorderWidth = 1f;
        pane.Style.CornerRadius = 6f;
        using (runtime.PushParent(pane))
        {
            Arrange.Spring(pane);
            Box row = runtime.Box();
            row.ChildLayoutAxis = LayoutAxis.X;
            row.SizeX = SemanticSize.ParentPct(1f);
            row.SizeY = SemanticSize.ChildrenSum();
            using (runtime.PushParent(row))
            {
                Arrange.Spring(row);
                runtime.Text(label, ComponentSize.Sm, TextColor.Secondary);
                Arrange.Spring(row);
            }
            Arrange.Spring(pane);
        }
        return pane;
    }

    // ── Carousel ─────────────────────────────────────────────────────────────

    private static readonly string[] Slides = ["Slide 1 — Introduction", "Slide 2 — Features", "Slide 3 — Pricing", "Slide 4 — Contact"];

    private static UiNode CarouselPage(BibuiRuntime runtime, LayoutState state, Action<LayoutState> set)
    {
        int index = Math.Clamp(state.CarouselIndex, 0, Slides.Length - 1);
        int go = index;
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            DemoNavigation.Heading(runtime, "One slide at a time: the arrows, the dots or the buttons move between them");

            Box carousel = runtime.Box();
            carousel.ChildLayoutAxis = LayoutAxis.Y;
            carousel.Px(480f, 200f);
            using (runtime.PushParent(carousel))
            {
                // The slide between its two arrows.
                Box stage = runtime.Box();
                stage.ChildLayoutAxis = LayoutAxis.X;
                stage.SizeX = SemanticSize.ParentPct(1f);
                stage.SizeY = SemanticSize.Pixels(168f);
                using (runtime.PushParent(stage))
                {
                    if (Arrow(runtime, "demo-carousel-arrow-prev", "chevron-left", index > 0)) go = index - 1;

                    Box slide = runtime.Box();
                    slide.ChildLayoutAxis = LayoutAxis.Y;
                    slide.SizeX = SemanticSize.ParentPct(1f);
                    slide.SizeY = SemanticSize.ParentPct(1f);
                    slide.Style.BackgroundColor = UiColor.SurfaceRaised;
                    slide.Style.BorderColor = UiColor.Border;
                    slide.Style.BorderWidth = 1f;
                    slide.Style.CornerRadius = 8f;
                    using (runtime.PushParent(slide))
                    {
                        Arrange.Spring(slide);
                        Box row = runtime.Box();
                        row.ChildLayoutAxis = LayoutAxis.X;
                        row.SizeX = SemanticSize.ParentPct(1f);
                        row.SizeY = SemanticSize.ChildrenSum();
                        using (runtime.PushParent(row))
                        {
                            Arrange.Spring(row);
                            runtime.Text(Slides[index], textColor: TextColor.Secondary);
                            Arrange.Spring(row);
                        }
                        Arrange.Spring(slide);
                    }

                    if (Arrow(runtime, "demo-carousel-arrow-next", "chevron-right", index < Slides.Length - 1)) go = index + 1;
                }

                // A dot per slide, the current one lit; a dot takes you to its slide.
                runtime.Gap(8f);
                Box dots = runtime.Box();
                dots.ChildLayoutAxis = LayoutAxis.X;
                dots.SizeX = SemanticSize.ParentPct(1f);
                dots.SizeY = SemanticSize.Pixels(8f);
                using (runtime.PushParent(dots))
                {
                    Arrange.Spring(dots);
                    for (int i = 0; i < Slides.Length; i++)
                    {
                        if (i > 0) runtime.Gap(8f);
                        Box dot = runtime.BoxFromId($"demo-carousel-dot-{i}", InteractionKinds.LeftPress);
                        if (runtime.InteractionsFor(dot).Clicked) go = i;
                        dot.Px(8f, 8f);
                        dot.Style.CornerRadius = 9999f;
                        dot.Style.BackgroundColor = i == index ? UiColor.TextPrimary : UiColor.Border;
                        dot.Style.HoverBackgroundColor = i == index ? (UiColor?)null : UiColor.TextSecondary;
                    }
                    Arrange.Spring(dots);
                }
            }

            runtime.Gap(16f);
            using (runtime.PushParent(DemoNavigation.Row(runtime)))
            {
                if (DemoNavigation.WideButton(runtime, "demo-carousel-prev", "← Prev", 88f, disabled: index == 0).Clicked) go = index - 1;
                runtime.Gap(8f);
                if (DemoNavigation.WideButton(runtime, "demo-carousel-next", "Next →", 88f, disabled: index == Slides.Length - 1).Clicked) go = index + 1;
            }

            runtime.Gap(16f);
            runtime.Text($"Slide {index + 1} of {Slides.Length}", ComponentSize.Sm, TextColor.Secondary);
        }
        if (go != index) set(state with { CarouselIndex = go });
        return page;
    }

    // A round 36 px arrow button centred down the stage, dimmed and taking
    // nothing when there is nowhere to go.
    private static bool Arrow(BibuiRuntime runtime, string id, string icon, bool enabled)
    {
        Box cell = DemoNavigation.MidCell(runtime);
        bool clicked;
        using (runtime.PushParent(cell))
        {
            Box arrow = runtime.BoxFromId(id, enabled ? InteractionKinds.LeftPress : InteractionKinds.Disabled);
            clicked = runtime.InteractionsFor(arrow).Clicked;
            arrow.ChildLayoutAxis = LayoutAxis.X;
            arrow.Px(36f, 36f);
            arrow.Style.CornerRadius = 9999f;
            arrow.Style.BackgroundColor = UiColor.SurfaceRaised;
            arrow.Style.HoverBackgroundColor = enabled ? UiColor.SurfaceRaisedHover : (UiColor?)null;
            arrow.Style.BorderColor = UiColor.Border;
            arrow.Style.BorderWidth = 1f;
            arrow.Style.Padding = EdgeInsets.Symmetric(horizontal: 10f, vertical: 0f);
            using (runtime.PushParent(arrow))
                DemoNavigation.MidIcon(runtime, icon, 16f, enabled ? UiColor.TextPrimary : UiColor.TextDisabled);
            Arrange.Spring(cell);
        }
        return clicked;
    }

    // ── ButtonStrip ──────────────────────────────────────────────────────────

    private static UiNode ButtonStripPage(BibuiRuntime runtime)
    {
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            DemoNavigation.Heading(runtime, "Each button lights under the pointer; this page gives them nothing to do");
            ButtonStrip(runtime, "demo-button-strip-a", "Left", "Middle", "Right");
            runtime.Gap(16f);
            ButtonStrip(runtime, "demo-button-strip-b", "Day", "Week", "Month");
        }
        return page;
    }

    // Buttons joined into one outline, a rule between each. Each takes its own
    // press and lights under the pointer; the demo does nothing with them.
    private static void ButtonStrip(BibuiRuntime runtime, string id, params string[] labels)
    {
        Box strip = runtime.Box();
        strip.ChildLayoutAxis = LayoutAxis.X;
        strip.SizeX = SemanticSize.ChildrenSum();
        strip.SizeY = SemanticSize.Pixels(36f);
        strip.Style.CornerRadius = 6f;
        strip.Style.BorderColor = UiColor.Border;
        strip.Style.BorderWidth = 1f;
        strip.Style.Padding = EdgeInsets.All(1f);
        using (runtime.PushParent(strip))
            for (int i = 0; i < labels.Length; i++)
            {
                if (i > 0)
                {
                    Box divider = runtime.Box();
                    divider.SizeX = SemanticSize.Pixels(1f);
                    divider.SizeY = SemanticSize.ParentPct(1f);
                    divider.Style.BackgroundColor = UiColor.Border;
                }
                Box button = runtime.BoxFromId($"{id}-{labels[i].ToLowerInvariant()}", InteractionKinds.LeftPress);
                runtime.InteractionsFor(button);
                button.ChildLayoutAxis = LayoutAxis.X;
                button.SizeX = SemanticSize.ChildrenSum();
                button.SizeY = SemanticSize.ParentPct(1f);
                button.Style.Padding = EdgeInsets.Symmetric(horizontal: 11f, vertical: 0f);
                button.Style.CornerRadius = i == 0 || i == labels.Length - 1 ? 5f : 0f;
                button.Style.HoverBackgroundColor = UiColor.SurfaceRaised;
                using (runtime.PushParent(button))
                    DemoNavigation.MidText(runtime, labels[i], color: TextColor.Secondary);
            }
    }

    // ── Stack: flow children, and children placed at an offset ───────────────

    private static UiNode StackPage(BibuiRuntime runtime, LayoutState state, Action<LayoutState> set)
    {
        bool clicked = false;
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            DemoNavigation.Heading(runtime, "Flow children (vertical, like Column)");
            Box flow = runtime.Box();
            flow.ChildLayoutAxis = LayoutAxis.Y;
            flow.Px(560f, 80f);
            flow.Style.BackgroundColor = "#18181B";
            flow.Style.CornerRadius = 8f;
            using (runtime.PushParent(flow))
                for (int i = 1; i <= 2; i++)
                {
                    Box child = runtime.Box();
                    child.ChildLayoutAxis = LayoutAxis.Y;
                    child.SizeX = SemanticSize.ParentPct(1f);
                    child.SizeY = SemanticSize.Pixels(40f);
                    child.Style.Padding = new EdgeInsets(8f, 12f, 0f, 12f);
                    using (runtime.PushParent(child))
                        runtime.Text($"Flow child {i}", ComponentSize.Sm, TextColor.Secondary);
                }

            runtime.Gap(16f);
            DemoNavigation.Heading(runtime, "Positioned children (absolute offsets)");
            Box stack = runtime.Box();
            stack.ChildLayoutAxis = LayoutAxis.Y;
            stack.Px(560f, 200f);
            stack.Style.BackgroundColor = "#18181B";
            stack.Style.CornerRadius = 8f;
            using (runtime.PushParent(stack))
            {
                // In the flow: four striped rows down the box.
                for (int i = 0; i < 4; i++)
                {
                    Box row = runtime.Box();
                    row.ChildLayoutAxis = LayoutAxis.Y;
                    row.SizeX = SemanticSize.ParentPct(1f);
                    row.SizeY = SemanticSize.Pixels(50f);
                    row.Style.BackgroundColor = i % 2 == 0 ? "#1a1a2e" : "#16213e";
                    row.Style.Padding = new EdgeInsets(12f, 12f, 0f, 12f);
                    using (runtime.PushParent(row))
                        runtime.Text($"Row {i + 1}", ComponentSize.Sm, TextColor.Disabled);
                }

                // Out of it: two cards at offsets from the box's corner, over
                // the rows. The blue one takes the press.
                Box cardA = runtime.BoxFromId("demo-stack-card-a", InteractionKinds.LeftPress);
                clicked = runtime.InteractionsFor(cardA).Clicked;
                Card(runtime, cardA, "Floating card A", "#2563EB", "#3B82F6").Px(180f, 70f).At(20f, 20f);
                Card(runtime, runtime.Box(), "Floating card B", "#7C3AED", null).Px(200f, 60f).At(240f, 80f);
            }

            runtime.Gap(16f);
            runtime.Text(state.StackClickCount > 0 ? $"Card A clicked {state.StackClickCount} time(s)" : "Click the blue card",
                ComponentSize.Sm, state.StackClickCount > 0 ? TextColor.Accent : TextColor.Secondary);
        }
        if (clicked) set(state with { StackClickCount = state.StackClickCount + 1 });
        return page;
    }

    private static Box Card(BibuiRuntime runtime, Box card, string label, UiColor fill, UiColor? hover)
    {
        card.ChildLayoutAxis = LayoutAxis.Y;
        card.Style.BackgroundColor = fill;
        card.Style.HoverBackgroundColor = hover;
        card.Style.CornerRadius = 6f;
        card.Style.Padding = EdgeInsets.All(10f);
        using (runtime.PushParent(card))
            runtime.Text(label, ComponentSize.Sm);
        return card;
    }

    // ── Interactive box ──────────────────────────────────────────────────────

    private static UiNode InteractiveBoxPage(BibuiRuntime runtime, LayoutState state, Action<LayoutState> set)
    {
        int clicks = 0;
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            DemoNavigation.Heading(runtime, "Clickable styled containers");
            using (runtime.PushParent(DemoNavigation.Row(runtime)))
            {
                // A box from an id that claims the press, asked what happened: no callback.
                Box first = runtime.BoxFromId("demo-ibox-1", InteractionKinds.LeftPress);
                if (runtime.InteractionsFor(first).Clicked) clicks++;
                Tile(runtime, first, "Clickable", "Claims a press", "#1e3a5f", "#24476f", TextColor.Secondary);
                runtime.Gap(12f);

                Box second = runtime.BoxFromId("demo-ibox-2", InteractionKinds.LeftPress);
                if (runtime.InteractionsFor(second).Clicked) clicks++;
                Tile(runtime, second, "Another", "Interactive box", "#3b1f2b", "#4a2736", TextColor.Secondary);
                runtime.Gap(12f);

                // Keyless, claiming nothing: no hover, no press.
                Tile(runtime, runtime.Box(), "Plain", "Claims nothing", "#27272A", null, TextColor.Disabled);
            }
            runtime.Gap(16f);
            runtime.Text(state.BoxClickCount > 0 ? $"Boxes clicked {state.BoxClickCount} time(s)" : "Click the colored boxes",
                ComponentSize.Sm, state.BoxClickCount > 0 ? TextColor.Accent : TextColor.Secondary);
        }
        if (clicks > 0) set(state with { BoxClickCount = state.BoxClickCount + clicks });
        return page;
    }

    private static void Tile(BibuiRuntime runtime, Box tile, string title, string caption, UiColor fill, UiColor? hover, TextColor captionColor)
    {
        tile.ChildLayoutAxis = LayoutAxis.Y;
        tile.Px(140f, 80f);
        tile.Style.BackgroundColor = fill;
        tile.Style.HoverBackgroundColor = hover;
        tile.Style.CornerRadius = 8f;
        tile.Style.Padding = EdgeInsets.All(12f);
        using (runtime.PushParent(tile))
        {
            using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text(title, ComponentSize.Sm);
            runtime.Text(caption, ComponentSize.Sm, captionColor);
        }
    }

    // A panel holding a section: a colormap select and an alpha slider.
    private static UiNode SectionPage(BibuiRuntime runtime, LayoutState state, Action<LayoutState> set)
    {
        string[] colormaps = ["inferno", "viridis", "magma", "plasma", "turbo"];
        var page = Arrange.Column();
        using (runtime.PushParent(page))
        {
            using (runtime.PushFontFace(weight: FontWeight.Bold)) runtime.Text("Composite panel: header + stack of controls", ComponentSize.Sm, TextColor.Secondary);
            Arrange.Gap(page, 16f);

            var panel = runtime.Box();
            panel.ChildLayoutAxis = LayoutAxis.Y;
            panel.Px(width: 360f).SizeY = SemanticSize.ChildrenSum();
            panel.Style.BackgroundColor = "#0B0B10";
            panel.Style.BorderColor = "#1F1F25";
            panel.Style.CornerRadius = 10f;
            panel.Style.Padding = EdgeInsets.All(20f);
            using (runtime.PushParent(panel))
            using (runtime.PushParent(Section.Build(runtime, "APPEARANCE")))
            {
                ColormapSelect(runtime, state, set, colormaps, width: 320f);
                runtime.Gap(Section.Spacing);
                var alpha = Slider.Build(runtime, "demo-colormap-alpha", state.ColormapAlpha, 0f, 1f, 0.01f, "Alpha");
                if (alpha.Value != state.ColormapAlpha) set(state with { ColormapAlpha = alpha.Value });
            }

            Arrange.Gap(page, 16f);
            runtime.Text($"Colormap: {state.ColormapValue}    Alpha: {state.ColormapAlpha:0.00}", ComponentSize.Sm, TextColor.Secondary);
        }
        return page;
    }

    // A select whose rows are the page's own: a gradient swatch and the name,
    // which Select.Item does not draw.
    private static UiNode ColormapSelect(BibuiRuntime runtime, LayoutState state,
        Action<LayoutState> set, IReadOnlyList<string> colormaps, float width)
    {
        var select = Select.Begin(runtime, "demo-colormap", state.ColormapValue, width);
        if (select.Open)
            foreach (var name in colormaps)
            {
                Box row = runtime.BoxFromId($"demo-colormap-option-{name}", InteractionKinds.LeftPress);
                if (runtime.InteractionsFor(row).Clicked)
                {
                    select.Close();
                    set(state with { ColormapValue = name });
                }
                row.ChildLayoutAxis = LayoutAxis.X;
                row.SizeX = SemanticSize.ParentPct(1f);
                row.SizeY = SemanticSize.ChildrenSum();
                row.Style.Padding = EdgeInsets.Symmetric(horizontal: 8f, vertical: 8f);
                row.Style.CornerRadius = 4f;
                row.Style.BackgroundColor = name == state.ColormapValue ? UiColor.SurfaceRaisedHover : (UiColor?)null;
                row.Style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;
                using (runtime.PushParent(row))
                {
                    // The swatch a little down, about level with the text beside it.
                    Box cell = runtime.Box();
                    cell.ChildLayoutAxis = LayoutAxis.Y;
                    cell.SizeX = cell.SizeY = SemanticSize.ChildrenSum();
                    using (runtime.PushParent(cell))
                    {
                        Arrange.Gap(cell, 3f);
                        Box swatch = runtime.Box();
                        swatch.Px(48f, 12f);
                        swatch.Style.CornerRadius = 3f;
                        swatch.Style.BackgroundGradient = Gradient.Horizontal(
                            [.. (Bibui.Theming.Colormaps.Get(name) ?? []).Select(s => new ColorStop(s.Stop, UiColor.Hex(s.Color)))]);
                    }
                    Arrange.Gap(row, 8f);
                    runtime.Text(name);
                }
            }
        return select.End();
    }
}
