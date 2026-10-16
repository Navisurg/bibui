using Bibui;
using Bibui.Runtime;
using Bibui.TextEdit;
using static Bibui.Arrange;

namespace BibuiApp;

/// <summary>
/// The demo's state: the app's own, read and written by its build. A counter, a
/// slider-driven split, a name field and a checkbox.
/// </summary>
public sealed class DemoState
{
    public int Counter;
    public bool Subscribed;
    public float LeftWeight = 50f;

    // The name field's text, caret and focus, edited in place by the field.
    public readonly BibuiStr NameText = new(32);
    public readonly TextEditState NameEdit = new();
    public bool NameFocused;
}

/// <summary>
/// The interactive demo's build: one frame, between the loop's BeginBuild and
/// EndBuild. Program.cs runs it in a window; the runtime tests
/// (tests/bibui-runtime-tests) run the same build headless and drive it.
/// </summary>
public static class Demo
{
    public static void Build(BibuiRuntime runtime, DemoState state)
    {
        var nameText = state.NameText;
        var nameEdit = state.NameEdit;

        var subscribe = runtime.Checkbox("subscribe-checkbox", state.Subscribed, Labels.Subscribe);
        if (subscribe.Clicked) state.Subscribed = !state.Subscribed;

        // A press focuses the name field; Escape or Enter ends editing.
        Box nameField = runtime.Box();
        TextField.InputStyle(runtime, nameField, state.NameFocused);
        nameField.SizeX = SemanticSize.ParentPct(1f);
        nameField.SizeY = SemanticSize.Pixels(44f);
        BibuiWidget nameLine;
        using (runtime.PushParent(nameField)) nameLine = runtime.TextField("name-input", nameText, nameEdit, state.NameFocused,
            placeholder: Labels.NamePlaceholder);
        if (nameLine.Pressed) state.NameFocused = true;
        foreach (var ev in runtime.UnconsumedEvents)
            if (state.NameFocused && ev is { IsKeyDown: true, Key: Bibui.Key.Escape } or { Kind: BibUiEventKind.Submit })
            {
                state.NameFocused = false;
                ev.Eat();
            }
        var decrement = runtime.Button("decrement-btn", Labels.Minus);
        if (decrement.Clicked) state.Counter--;
        var increment = runtime.Button("increment-btn", Labels.Plus);
        if (increment.Clicked) state.Counter++;

        var weight = Slider(runtime, "weight-slider", state.LeftWeight, min: 5f, max: 95f, step: 1f);
        state.LeftWeight = weight.Value;

        string name = nameText.ToString();
        // Typed text is not worth keeping once it changes, so the greeting is encoded each frame.
        string greeting = string.IsNullOrEmpty(name) ? "…" : $"Hello, {name}!";

        var page = Column(
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
        // Built with Arrange, so linked here: under the frame's root.
        UiNode.PushIntoParent(runtime.ParentInScope, page);
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

}
