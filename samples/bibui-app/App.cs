using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;

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
        Box nameField = runtime.Box().Sized(Size.Fill, Size.Px(44f));
        TextField.InputStyle(runtime, nameField, state.NameFocused);
        runtime.BeginParent(nameField);
        BibuiWidget nameLine = runtime.TextField("name-input", nameText, nameEdit, state.NameFocused,
            placeholder: Labels.NamePlaceholder);
        runtime.EndParent(nameField);
        if (nameLine.Pressed) state.NameFocused = true;
        foreach (var ev in runtime.UnconsumedEvents)
            if (state.NameFocused && ev is { IsKeyDown: true, Key: Bibui.Core.Key.Escape } or { Kind: BibUiEventKind.Submit })
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

        // The widgets above are built first, so what they change this frame is
        // what the page shows; they are linked into their places here.
        var page = runtime.BeginParent(runtime.Column().Sized(Size.Fill, Size.Fill));
        page.Style = new Style { Padding = EdgeInsets.All(28f), BackgroundColor = UiColor.Background };

        // ── Header ────────────────────────────────────────────────────
        Bold(runtime, "bibui interactive demo", ComponentSize.Xxl, TextColor.Primary);
        runtime.Gap(20f);

        // ── Counter ───────────────────────────────────────────────────
        var counter = runtime.BeginParent(runtime.Column().Sized(Size.Fill));
        counter.Style = Card();
        Bold(runtime, "Counter", ComponentSize.Lg);
        runtime.Gap(12f);
        // Centring down the row needs the row to have a height of its own.
        var buttons = runtime.BeginParent(runtime.Row().Sized(Size.Fill, Size.Px(40f)));
        runtime.BeginCenterAcross();
        UiNode.PushIntoParent(runtime.ParentInScope, decrement.Node);
        runtime.EndCenterAcross();
        runtime.Gap(8f);
        runtime.BeginCenterAcross();
        Bold(runtime, state.Counter.ToString(), ComponentSize.Xl, TextColor.Accent, id: "counter-label");
        runtime.EndCenterAcross();
        runtime.Gap(8f);
        runtime.BeginCenterAcross();
        UiNode.PushIntoParent(runtime.ParentInScope, increment.Node);
        runtime.EndCenterAcross();
        runtime.EndParent(buttons);
        runtime.EndParent(counter);
        runtime.Gap(20f);

        // ── Proportional layout (shares of a row) ─────────────────────
        var split = runtime.BeginParent(runtime.Column().Sized(Size.Fill));
        split.Style = Card();
        Bold(runtime, "Proportional Layout (shares of a row)", ComponentSize.Lg);
        runtime.Gap(12f);

        // Two panels whose widths follow the slider-driven shares.
        var panels = runtime.BeginParent(runtime.Row().Sized(Size.Fill, Size.Px(96f)));
        var left = runtime.BeginParent(runtime.Column().Sized(Size.Pct(state.LeftWeight / 100f), Size.Fill));
        left.Style = new Style { BackgroundColor = UiColor.Accent, CornerRadius = 12f, Padding = EdgeInsets.All(12f) };
        Bold(runtime, $"weight {state.LeftWeight:0}", textColor: TextColor.AccentForeground);
        runtime.EndParent(left);
        runtime.Gap(12f);
        var right = runtime.BeginParent(runtime.Column().Sized(Size.Pct((100f - state.LeftWeight) / 100f), Size.Fill));
        right.Style = new Style
        {
            BackgroundColor = UiColor.Surface,
            BorderColor = UiColor.Border,
            BorderWidth = 1f,
            CornerRadius = 12f,
            Padding = EdgeInsets.All(12f),
        };
        Bold(runtime, $"weight {100f - state.LeftWeight:0}", textColor: TextColor.Secondary);
        runtime.EndParent(right);
        runtime.EndParent(panels);
        runtime.Gap(12f);

        var share = runtime.BeginParent(runtime.Row().Sized(Size.Fill));
        runtime.Text("Left share", ComponentSize.Sm, TextColor.Secondary);
        runtime.Spacer(Size.Fill);
        runtime.Text($"{state.LeftWeight:0}", ComponentSize.Sm, TextColor.Primary);
        runtime.EndParent(share);
        runtime.Gap(8f);
        UiNode.PushIntoParent(runtime.ParentInScope, weight.Node.Sized(Size.Fill));
        runtime.Gap(12f);
        runtime.Text($"Split {state.LeftWeight:0} : {100f - state.LeftWeight:0}  — drag to resize", ComponentSize.Sm, TextColor.Secondary);
        runtime.EndParent(split);
        runtime.Gap(20f);

        // ── Input + checkbox row ──────────────────────────────────────
        var cards = runtime.BeginParent(runtime.Row().Sized(Size.Fill));
        var input = runtime.BeginParent(runtime.Column().Sized(Size.Fill));
        input.Style = Card();
        Bold(runtime, "Input", ComponentSize.Lg);
        runtime.Gap(10f);
        UiNode.PushIntoParent(runtime.ParentInScope, nameField);
        runtime.Gap(10f);
        runtime.TextFromId("greeting", greeting, ComponentSize.Sm, string.IsNullOrEmpty(name) ? TextColor.Secondary : TextColor.Accent);
        runtime.EndParent(input);
        runtime.Gap(16f);
        var check = runtime.BeginParent(runtime.Column().Sized(Size.Px(220f)));
        check.Style = Card();
        Bold(runtime, "Checkbox", ComponentSize.Lg);
        runtime.Gap(12f);
        UiNode.PushIntoParent(runtime.ParentInScope, subscribe.Node);
        runtime.Gap(12f);
        Badge.Build(runtime, state.Subscribed ? Labels.Subscribed : Labels.Unsubscribed,
            state.Subscribed ? BadgeVariant.Primary : BadgeVariant.Default);
        runtime.EndParent(check);
        runtime.EndParent(cards);

        runtime.EndParent(page);
    }

    // A text at the bold weight. With an id, it is retained under it, so it can be found.
    private static Text Bold(BibuiRuntime runtime, ReadOnlySpan<char> text, FontSize? fontSize = null, UiColor? textColor = null,
        string? id = null)
    {
        runtime.BeginFontFace(weight: FontWeight.Bold);
        Text bold = id is null ? runtime.Text(text, fontSize, textColor) : runtime.TextFromId(id, text, fontSize, textColor);
        runtime.EndFontFace();
        return bold;
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

        track.Sized(Size.Fill, Size.Px(Knob));
        runtime.BeginParent(track);
        RailPart(runtime, t, UiColor.Accent);
        var knob = runtime.Box().Sized(Size.Px(Knob), Size.Px(Knob));
        knob.Style.CornerRadius = Knob / 2f;
        knob.Style.BackgroundColor = UiColor.TextPrimary;
        knob.Style.BorderColor = UiColor.Accent;
        knob.Style.BorderWidth = 2f;
        RailPart(runtime, 1f - t, UiColor.Border);
        runtime.EndParent(track);
        return (track, value);

        // A share of the track, the knob's height, with the rail drawn centred in it.
        static void RailPart(BibuiRuntime runtime, float share, UiColor color)
        {
            var part = runtime.Box().Sized(Size.Pct(share), Size.Fill);
            part.Style.Padding = new EdgeInsets((Knob - Rail) / 2f, 0f, (Knob - Rail) / 2f, 0f);
            runtime.BeginParent(part);
            var rail = runtime.Box().Sized(Size.Fill, Size.Fill);
            rail.Style.CornerRadius = Rail / 2f;
            rail.Style.BackgroundColor = color;
            runtime.EndParent(part);
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
