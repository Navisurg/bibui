using Bibui;
using Bibui.Runtime;
using Bibui.TextEdit;
using static Bibui.Arrange;

namespace BibuiApp;

/// <summary>
/// The interactive demo's build: a counter, a slider-driven split, a name field
/// and a checkbox. Program.cs opens it in a window; the runtime tests
/// (tests/bibui-runtime-tests) mount the same build headless and drive it.
/// </summary>
public static class App
{
    /// <summary>Mounts the demo on <paramref name="runtime"/>, with state of its own.</summary>
    public static void Mount(BibuiRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        // The name field's text, caret and focus: the app's, edited in place by the field.
        var nameText = new BibuiStr(32);
        var nameEdit = new TextEditState();
        bool nameFocused = false;

        runtime.MountStateful(
            initialState: new AppState(
                Counter: 0,
                Subscribed: false,
                LeftWeight: 50f),

            build: (state, setState) =>
            {
                var subscribe = Checkbox.Build(runtime, "subscribe-checkbox", state.Subscribed, Labels.Subscribe);
                if (subscribe.Clicked) setState.Set(state with { Subscribed = !state.Subscribed });

                // A press focuses the name field; Escape or Enter ends editing.
                Box nameField = runtime.Box();
                TextField.InputStyle(runtime, nameField, nameFocused);
                nameField.SizeX = SemanticSize.ParentPct(1f);
                nameField.SizeY = SemanticSize.Pixels(44f);
                BibuiWidget nameLine;
                using (runtime.PushParent(nameField)) nameLine = TextField.SingleLine(runtime, "name-input", nameText, nameEdit, nameFocused,
                    placeholder: Labels.NamePlaceholder);
                if (nameLine.Pressed) nameFocused = true;
                foreach (var handle in runtime.UnconsumedEvents)
                    if (nameFocused && handle.Event is { IsKeyDown: true, Key: Bibui.Key.Escape } or { Kind: BibUiEventKind.Submit })
                    {
                        nameFocused = false;
                        handle.Eat();
                    }
                var decrement = Button.Build(runtime, "decrement-btn", Labels.Minus);
                if (decrement.Clicked) setState.Set(state with { Counter = state.Counter - 1 });
                var increment = Button.Build(runtime, "increment-btn", Labels.Plus);
                if (increment.Clicked) setState.Set(state with { Counter = state.Counter + 1 });

                var weight = Slider(runtime, "weight-slider", state.LeftWeight, min: 5f, max: 95f, step: 1f);
                if (weight.Value != state.LeftWeight) setState.Set(state with { LeftWeight = weight.Value });

                string name = nameText.ToString();
                // Typed text is not worth keeping once it changes, so the greeting is encoded each frame.
                string greeting = string.IsNullOrEmpty(name) ? "…" : $"Hello, {name}!";

                return Column(
                    // ── Header ────────────────────────────────────────────────────
                    Bold(runtime, "bibui interactive demo", ComponentSize.Xxl, TextColor.Primary),
                    Space(20f),

                    // ── Counter ───────────────────────────────────────────────────
                    Column(
                        Bold(runtime, "Counter", ComponentSize.Lg),
                        Space(12f),
                        Row(
                            CentredY(decrement.Node),
                            Space(8f),
                            CentredY(Bold(runtime, state.Counter.ToString(), ComponentSize.Xl, TextColor.Accent, id: "counter-label")),
                            Space(8f),
                            CentredY(increment.Node)
                        ).FillX().Px(height: 40f)   // centring needs a row with a height of its own
                    ).FillX().Styled(Card()),
                    Space(20f),

                    // ── Proportional layout (shares of a row) ─────────────────────
                    Column(
                        Bold(runtime, "Proportional Layout (shares of a row)", ComponentSize.Lg),
                        Space(12f),

                        // Two panels whose widths follow the slider-driven shares.
                        Row(
                            Column(Bold(runtime, $"weight {state.LeftWeight:0}", textColor: TextColor.AccentForeground))
                                .Styled(new Style
                                {
                                    BackgroundColor = UiColor.Accent,
                                    CornerRadius = 12f,
                                    Padding = EdgeInsets.All(12f),
                                }).FillX(state.LeftWeight / 100f).FillY(),
                            Space(12f),
                            Column(Bold(runtime, $"weight {100f - state.LeftWeight:0}", textColor: TextColor.Secondary))
                                .Styled(new Style
                                {
                                    BackgroundColor = UiColor.Surface,
                                    BorderColor = UiColor.Border,
                                    BorderWidth = 1f,
                                    CornerRadius = 12f,
                                    Padding = EdgeInsets.All(12f),
                                }).FillX((100f - state.LeftWeight) / 100f).FillY()
                        ).FillX().Px(height: 96f),
                        Space(12f),

                        Row(
                            runtime.Text("Left share", ComponentSize.Sm, TextColor.Secondary),
                            Spring(),
                            runtime.Text($"{state.LeftWeight:0}", ComponentSize.Sm, TextColor.Primary)
                        ).FillX(),
                        Space(8f),
                        weight.Node.FillX(),
                        Space(12f),
                        runtime.Text($"Split {state.LeftWeight:0} : {100f - state.LeftWeight:0}  — drag to resize", ComponentSize.Sm, TextColor.Secondary)
                    ).FillX().Styled(Card()),
                    Space(20f),

                    // ── Input + checkbox row ──────────────────────────────────────
                    Row(
                        Column(
                            Bold(runtime, "Input", ComponentSize.Lg),
                            Space(10f),
                            nameField,
                            Space(10f),
                            runtime.TextFromId("greeting", greeting, ComponentSize.Sm, string.IsNullOrEmpty(name) ? TextColor.Secondary : TextColor.Accent)
                        ).FillX().Styled(Card()),
                        Space(16f),
                        Column(
                            Bold(runtime, "Checkbox", ComponentSize.Lg),
                            Space(12f),
                            subscribe.Node,
                            Space(12f),
                            Badge.Build(runtime, state.Subscribed ? Labels.Subscribed : Labels.Unsubscribed,
                                state.Subscribed ? BadgeVariant.Primary : BadgeVariant.Default)
                        ).Px(width: 220f).Styled(Card())
                    ).FillX()
                ).FillX().FillY().Styled(new Style
                {
                    Padding = EdgeInsets.All(28f),
                    BackgroundColor = UiColor.Background,
                });
            },
            surface: new SurfaceOptions { Width = 720, Height = 1200 });
    }

    // A text at the bold weight, where a PushFontFace scope cannot go: inside an
    // expression. With an id, it is retained under it, so it can be found.
    private static Text Bold(BibuiRuntime runtime, ReadOnlySpan<char> text, FontSize? fontSize = null, UiColor? textColor = null,
        string? id = null)
    {
        using (runtime.PushFontFace(weight: FontWeight.Bold))
            return id is null ? runtime.Text(text, fontSize, textColor) : runtime.TextFromId(id, text, fontSize, textColor);
    }

    // A slider, composed: a track that takes a press and follows the drag, holding
    // the filled rail, the knob and the rest of the rail side by side. It owns no
    // state — it hands back the value under the pointer and the caller applies it.
    private static (Box Node, float Value) Slider(BibuiRuntime runtime, string id, float value, float min, float max, float step)
    {
        const float Knob = 16f, Rail = 6f;

        Box track = runtime.BoxFromId(id, InteractionKinds.LeftPress);
        Interaction drag = runtime.InteractionsFor(track);
        if ((drag.Pressed || drag.Dragging) && track.Extent.X > Knob)
        {
            float u = Math.Clamp((drag.X - track.Rect.X - Knob / 2f) / (track.Extent.X - Knob), 0f, 1f);
            value = min + u * (max - min);
            if (step > 0f) value = min + MathF.Round((value - min) / step) * step;
        }
        float t = Math.Clamp((value - min) / (max - min), 0f, 1f);

        track.ChildLayoutAxis = LayoutAxis.X;
        track.SizeX = SemanticSize.ParentPct(1f);
        track.SizeY = SemanticSize.Pixels(Knob);
        using (runtime.PushParent(track))
        {
            RailPart(runtime, t, UiColor.Accent);
            var knob = runtime.Box();
            knob.SizeX = knob.SizeY = SemanticSize.Pixels(Knob);
            knob.Style.CornerRadius = Knob / 2f;
            knob.Style.BackgroundColor = UiColor.TextPrimary;
            knob.Style.BorderColor = UiColor.Accent;
            knob.Style.BorderWidth = 2f;
            RailPart(runtime, 1f - t, UiColor.Border);
        }
        return (track, value);

        // A share of the track, the knob's height, with the rail drawn centred in it.
        static void RailPart(BibuiRuntime runtime, float share, UiColor color)
        {
            var part = runtime.Box();
            part.SizeX = SemanticSize.ParentPct(share);
            part.SizeY = SemanticSize.ParentPct(1f);
            part.Style.Padding = new EdgeInsets((Knob - Rail) / 2f, 0f, (Knob - Rail) / 2f, 0f);
            using (runtime.PushParent(part))
            {
                var rail = runtime.Box();
                rail.SizeX = rail.SizeY = SemanticSize.ParentPct(1f);
                rail.Style.CornerRadius = Rail / 2f;
                rail.Style.BackgroundColor = color;
            }
        }
    }

    private static Style Card() => new()
    {
        BackgroundColor = UiColor.SurfaceRaised,
        BorderColor = UiColor.Border,
        BorderWidth = 1f,
        CornerRadius = 16f,
        Padding = EdgeInsets.All(16f),
    };

    private static class Labels
    {
        public const string Minus = "−";
        public const string Plus = "+";
        public const string Subscribe = "Subscribe";
        public const string Subscribed = "Subscribed";
        public const string Unsubscribed = "Unsubscribed";
        public const string NamePlaceholder = "Type your name…";
    }

    private record AppState(
        int Counter,
        bool Subscribed,
        float LeftWeight);
}
