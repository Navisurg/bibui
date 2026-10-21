using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;

namespace ComponentGallery;

/// <summary>What the Inputs & Forms pages hold between frames. A new page's
/// state starts at these values, and the gallery keeps it while another page is
/// shown.</summary>
internal sealed record InputsState
{
    public bool CheckboxChecked { get; init; }
    public bool SwitchChecked { get; init; }
    public float SliderValue { get; init; } = 40f;
    public string RadioValue { get; init; } = "option-a";
    public string? SelectValue { get; init; }
    public string? CountryValue { get; init; }
    public bool TogglePressed { get; init; }
    public string ToggleGroupValue { get; init; } = "bold";
    public DateOnly? DatePickerValue { get; init; }
    public DateOnly DatePickerMonth { get; init; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? CalendarSelected { get; init; }
    public int TimePickerHour { get; init; } = 9;
    public int TimePickerMinute { get; init; } = 30;
    public string? ColorPickerValue { get; init; } = "#3B82F6";
    public int DemoButtonClickCount { get; init; }
}

internal static class DemoInputs
{
    internal static void Build(BibuiRuntime runtime, string route, InputsState state, Action<InputsState> set)
    {
        switch (route)
        {
            case "Button": ButtonPage(runtime, state, set); break;
            case "Input": InputPage(runtime); break;
            case "Textarea": TextareaPage(runtime); break;
            case "Checkbox": CheckboxPage(runtime, state, set); break;
            case "TextField": TextFieldPage(runtime); break;
            case "Switch": SwitchPage(runtime, state, set); break;
            case "RadioGroup": RadioGroupPage(runtime, state, set); break;
            case "Select": SelectPage(runtime, state, set); break;
            case "Slider": SliderPage(runtime, state, set); break;
            case "Toggle": TogglePage(runtime, state, set); break;
            case "ToggleGroup": ToggleGroupPage(runtime, state, set); break;
            case "DatePicker": DatePickerPage(runtime, state, set); break;
            case "Calendar": CalendarPage(runtime, state, set); break;
            case "TimePicker": TimePickerPage(runtime, state, set); break;
            case "ColorPicker": ColorPickerPage(runtime, state, set); break;
        }
    }

    /// <summary>
    /// The switch owns no state and offers no callback: it reports the press and
    /// the caller applies it. Nothing round-trips through a local, and a record
    /// property is no longer awkward to pass.
    /// </summary>
    private static void SwitchPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Bold(runtime, "Toggle a setting on or off", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(16f);
        if (runtime.Switch("demo-switch", state.SwitchChecked, label: "Enable notifications").Clicked)
            set(state with { SwitchChecked = !state.SwitchChecked });
        runtime.Gap(16f);
        runtime.Text(state.SwitchChecked ? "Notifications enabled" : "Notifications disabled", ComponentSize.Sm, state.SwitchChecked ? TextColor.Accent : TextColor.Secondary);
        runtime.Gap(16f);
        runtime.Switch("demo-switch-disabled", false, label: "Disabled switch", disabled: true);
    }

    private static readonly (string Label, string Value, bool Disabled)[] Fruits =
    [
        ("Apple", "apple", false),
        ("Banana", "banana", true),
        ("Cherry", "cherry", false),
        ("Mango", "mango", false),
    ];

    // A select, and one that is disabled. The rows of the dropdown are the
    // page's to build; these are plain runtime.SelectItem rows.
    private static void SliderPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "Drag to select a value in a range");
        var volume = runtime.Slider("demo-slider", state.SliderValue, 0f, 100f, 5f, "Volume");
        volume.Node.Sized(Size.Px(380f));
        if (volume.Value != state.SliderValue) set(state with { SliderValue = volume.Value });

        runtime.Gap(16f);
        runtime.Text($"Value: {volume.Value:0}", ComponentSize.Sm, TextColor.Accent);

        runtime.Gap(16f);
        runtime.Slider("demo-slider-disabled", 30f, 0f, 100f, label: "Disabled", disabled: true).Node.Sized(Size.Px(380f));
    }

    private static void SelectPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "Choose one from a list");
        var chosen = Fruits.FirstOrDefault(f => f.Value == state.SelectValue);
        var select = Select.Begin(runtime, "demo-select", chosen.Label ?? "Pick a fruit…");
        if (select.Open)
            foreach (var fruit in Fruits)
                if (runtime.SelectItem($"demo-select-option-{fruit.Value}", fruit.Label,
                        fruit.Value == state.SelectValue, fruit.Disabled).Clicked)
                {
                    select.Close();
                    set(state with { SelectValue = fruit.Value });
                }
        select.End();

        runtime.Gap(16f);
        runtime.Text(state.SelectValue is null ? "Nothing selected" : $"Selected: {state.SelectValue}", ComponentSize.Sm, state.SelectValue is null ? TextColor.Secondary : TextColor.Accent);

        runtime.Gap(24f);
        CountrySelect(runtime, state, set);

        runtime.Gap(24f);
        Heading(runtime, "Disabled");
        Select.Begin(runtime, "demo-select-disabled", "Choose a plan", disabled: true).End();
    }

    private static void Heading(BibuiRuntime runtime, string text)
    {
        Bold(runtime, text, ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(16f);
    }

    private static void CheckboxPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "Interactive checkbox");
        if (runtime.Checkbox("demo-checkbox", state.CheckboxChecked, "Accept terms and conditions").Clicked)
            set(state with { CheckboxChecked = !state.CheckboxChecked });
        runtime.Gap(16f);
        Badge.Build(runtime, state.CheckboxChecked ? "Accepted" : "Not accepted",
            state.CheckboxChecked ? BadgeVariant.Success : BadgeVariant.Default);
        runtime.Gap(16f);
        runtime.Checkbox("demo-checkbox-disabled", false, "Disabled (unchecked)", disabled: true);
        runtime.Gap(16f);
        runtime.Checkbox("demo-checkbox-disabled-checked", true, "Disabled (checked)", disabled: true);
        runtime.Gap(24f);
        Heading(runtime, "Indeterminate");
        runtime.Checkbox("demo-checkbox-mixed", false, "Select all (mixed)", indeterminate: true);
        runtime.Gap(16f);
        runtime.Text("Tri-state — dash instead of checkmark. Typical for \"some items selected\".", ComponentSize.Sm, TextColor.Secondary);
    }

    private static void ToggleGroupPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "Group of toggles (single-select)");
        var group = runtime.ToggleGroup("demo-toggle-group",
            [new ToggleOption("Bold", "bold"), new ToggleOption("Italic", "italic"), new ToggleOption("Underline", "underline")],
            state.ToggleGroupValue);
        if (group.Picked is { } picked) set(state with { ToggleGroupValue = picked });
        runtime.Gap(16f);
        runtime.Text($"Active: {state.ToggleGroupValue}", ComponentSize.Sm, TextColor.Accent);
    }

    // ── TextField experiment (TextField.cs) ─────────────────────────────────
    // Each field is shown in one fixed state: a buffer, and where its caret
    // and mark are, in bytes — what an application holds and edits.
    private static void TextFieldPage(BibuiRuntime runtime)
    {
        Heading(runtime, "Live: press to focus, then type");
        LiveField(runtime, "demo-textfield", LiveText, LiveEdit, "Type here");
        runtime.Gap(8f);
        runtime.Text(
            $"\"{LiveText.ToString()}\" — cursor {LiveEdit.Cursor}, mark {LiveEdit.Mark}, "
            + $"{LiveText.Length} bytes, focus {Focused ?? "none"}", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(8f);
        LiveField(runtime, "demo-textfield-2", SecondText, SecondEdit, "A second field");
        runtime.Gap(8f);
        LiveField(runtime, "demo-textfield-multi", MultiText, MultiEdit, "Several lines", lines: true, rows: 3);
        runtime.Gap(8f);
        var readOnly = runtime.BeginParent(InputBox(runtime, "demo-textfield-readonly", Focused == "demo-textfield-readonly", lines: true));
        runtime.BeginFontSize(ComponentSize.Sm);
        runtime.BeginSizeY(TextField.Rows(runtime, 2));
        var field = runtime.MultilineTextField("demo-textfield-readonly", ReadOnlyText, ReadOnlyEdit,
            focused: Focused == "demo-textfield-readonly", events: TextField.MultiLineReadOnlyEvents,
            textColor: TextColor.Secondary);
        if (field.Pressed) Focused = "demo-textfield-readonly";
        runtime.EndSizeY();
        runtime.EndFontSize();
        runtime.EndParent(readOnly);

        EndEditing(runtime);
        runtime.Gap(24f);

        Heading(runtime, "Drawn states");
        Heading(runtime, "Not focused");
        DrawnState(runtime, "demo-textfield-state-1", Sample(), At(0, 0), focused: false);
        runtime.Gap(16f);
        Heading(runtime, "Caret after \"quick\"");
        DrawnState(runtime, "demo-textfield-state-2", Sample(), At(9, 9), focused: true);
        runtime.Gap(16f);
        Heading(runtime, "\"quick brown\" selected, caret at its end");
        DrawnState(runtime, "demo-textfield-state-3", Sample(), At(15, 4), focused: true);
        runtime.Gap(16f);
        Heading(runtime, "Selected backwards, caret at its start");
        DrawnState(runtime, "demo-textfield-state-4", Sample(), At(4, 15), focused: true);
        runtime.Gap(16f);
        Heading(runtime, "Caret at the start and at the end");
        DrawnState(runtime, "demo-textfield-state-5", Sample(), At(0, 0), focused: true);
        runtime.Gap(8f);
        DrawnState(runtime, "demo-textfield-state-6", Sample(), At(SampleText.Length, SampleText.Length), focused: true);
        runtime.Gap(16f);
        Heading(runtime, "Empty, with a placeholder");
        DrawnState(runtime, "demo-textfield-state-7", new BibuiStr(16), At(0, 0), focused: false, placeholder: "Search cases");
        runtime.Gap(8f);
        DrawnState(runtime, "demo-textfield-state-8", new BibuiStr(16), At(0, 0), focused: true, placeholder: "Search cases");
    }

    // A field shown in one state: a single line in its frame, never focused by a press.
    private static void DrawnState(BibuiRuntime runtime, string id, BibuiStr text, TextEditState edit,
        bool focused, ReadOnlySpan<char> placeholder = default)
    {
        var box = runtime.BeginParent(InputBox(runtime, id, focused));
        runtime.TextField(id, text, edit, focused, placeholder: placeholder);
        runtime.EndParent(box);
    }

    // The live fields' state: what an application would hold for them.
    private static readonly BibuiStr LiveText = BibuiStr.FromStrLit("Hello");
    private static readonly TextEditState LiveEdit = new();
    private static string? Focused;      // which live field is being edited
    private static readonly BibuiStr SecondText = new(16);
    private static readonly BibuiStr MultiText = BibuiStr.FromStrLit(
        "A field that wraps by itself: type past the edge and the words move down a line. "
        + "It is three lines tall, and this text runs to more than that, so the lines past the "
        + "third are below its edge: scroll the wheel over it to bring them into view.");
    private static readonly TextEditState MultiEdit = new();
    private static readonly BibuiStr ReadOnlyText = BibuiStr.FromStrLit(
        "A read-only field: press, drag and double-click to select, arrow keys to move, but typing changes nothing.");
    private static readonly TextEditState ReadOnlyEdit = new();
    private static readonly TextEditState SecondEdit = new();

    // The box a field named id sits in, in the input look: 320 px wide; a
    // single line 40 px tall, lines as tall as the field, which takes the
    // height pushed for it. The field takes its own presses, under its id; a
    // disabled box swallows them instead, so they do not fall through to what
    // is beneath, which needs a retained box of its own.
    private static Box InputBox(BibuiRuntime runtime, string id, bool focused, float width = 320f,
        bool lines = false, bool error = false, bool disabled = false)
    {
        Box box = disabled ? runtime.BoxFromId(id + "-frame", InteractionKinds.Disabled) : runtime.Box();
        if (disabled) runtime.InteractionsFor(box);
        TextField.InputStyle(runtime, box, focused, error, disabled);
        box.SizeX = SemanticSize.Pixels(width);
        box.SizeY = lines ? SemanticSize.ChildrenSum() : SemanticSize.Pixels(40f);
        return box;
    }

    // A framed field the page keeps focus for: a press focuses it.
    private static void LiveField(BibuiRuntime runtime, string id, BibuiStr text, TextEditState edit,
        ReadOnlySpan<char> placeholder = default, bool lines = false, int rows = 3, float width = 320f)
    {
        bool focused = Focused == id;
        BibuiWidget field;
        var box = runtime.BeginParent(InputBox(runtime, id, focused, width, lines));
        if (lines)
        {
            runtime.BeginSizeY(TextField.Rows(runtime, rows));
            field = runtime.MultilineTextField(id, text, edit, focused, placeholder: placeholder);
            runtime.EndSizeY();
        }
        else field = runtime.TextField(id, text, edit, focused, placeholder: placeholder);
        runtime.EndParent(box);
        if (field.Pressed) Focused = id;
    }

    // What names a field: a small semibold text.
    internal static Text FieldLabel(BibuiRuntime runtime, string text)
        => Bold(runtime, text, ComponentSize.Sm);

    // A framed single-line field that is never focused: for pages that show an
    // input rather than use one.
    internal static void StaticInput(BibuiRuntime runtime, string id, string placeholder, float width = 320f)
    {
        var box = runtime.BeginParent(InputBox(runtime, id, focused: false, width));
        runtime.TextField(id, new BibuiStr(16), new TextEditState(), focused: false, placeholder: placeholder);
        runtime.EndParent(box);
    }

    // Escape and Submit (Enter) are the page's to read: they end editing. A
    // multi-line field takes Submit itself, to end a line. After the fields.
    private static void EndEditing(BibuiRuntime runtime)
    {
        if (Focused is null) return;
        foreach (var ev in runtime.UnconsumedEvents)
            if (ev is { IsKeyDown: true, Key: Bibui.Core.Key.Escape } or { Kind: BibUiEventKind.Submit })
            {
                Focused = null;
                ev.Eat();
            }
    }


    // ── Input and Textarea: fields in the input look ────────────────────────

    private static readonly BibuiStr InputText = new(32);
    private static readonly TextEditState InputEdit = new();
    private static readonly BibuiStr TextareaText = new(64);
    private static readonly TextEditState TextareaEdit = new();
    private static readonly BibuiStr TextareaLog = BibuiStr.FromStrLit(
        "input dir:  /tmp/job-42/in\noutput dir: /tmp/job-42/out\n--- stdout ---\nframes processed: 128\n"
        + "frames dropped:   0\nelapsed:          1.42s\n--- stderr ---\n(empty)");
    private static readonly TextEditState TextareaLogEdit = new();

    private static void InputPage(BibuiRuntime runtime)
    {
        Heading(runtime, "Basic input");
        LiveField(runtime, "demo-input", InputText, InputEdit, "Enter text…");
        runtime.Gap(16f);
        string value = InputText.ToString();
        runtime.Text(value.Length > 0 ? $"Value: \"{value}\"" : "Start typing above", ComponentSize.Sm, value.Length > 0 ? TextColor.Accent : TextColor.Secondary);
        runtime.Gap(24f);

        Heading(runtime, "Disabled");
        // Disabled: it shows its placeholder and takes nothing, so a press is swallowed, not answered.
        var disabled = runtime.BeginParent(InputBox(runtime, "demo-input-disabled", focused: false, disabled: true));
        runtime.TextField("demo-input-disabled", new BibuiStr(16), new TextEditState(), focused: false,
            events: 0, textColor: TextColor.Disabled, placeholder: "Disabled input");
        runtime.EndParent(disabled);
        runtime.Gap(24f);

        Heading(runtime, "Error");
        var error = runtime.BeginParent(InputBox(runtime, "demo-input-error", focused: false, error: true));
        runtime.TextField("demo-input-error", BibuiStr.FromStrLit("not-an-email"), new TextEditState(), focused: false);
        runtime.EndParent(error);
        runtime.Gap(16f);
        runtime.Text("Destructive border signals validation failure.", ComponentSize.Sm, TextColor.Secondary);

        EndEditing(runtime);
    }

    private static void TextareaPage(BibuiRuntime runtime)
    {
        Heading(runtime, "Multi-line text input");
        LiveField(runtime, "demo-textarea", TextareaText, TextareaEdit, "Write something…", lines: true, rows: 4, width: 400f);
        runtime.Gap(16f);
        runtime.Text($"{TextareaText.Length} characters", ComponentSize.Sm, TextColor.Secondary);
        runtime.Gap(24f);

        Heading(runtime, "Error");
        var error = runtime.BeginParent(InputBox(runtime, "demo-textarea-error", focused: false, width: 400f, lines: true, error: true));
        runtime.BeginSizeY(TextField.Rows(runtime, 3));
        runtime.MultilineTextField("demo-textarea-error", BibuiStr.FromStrLit("too short"), new TextEditState(), focused: false,
            placeholder: "Describe the issue");
        runtime.EndSizeY();
        runtime.EndParent(error);
        runtime.Gap(24f);

        Heading(runtime, "Multi-line content (press and drag across rows to select)");
        LiveField(runtime, "demo-textarea-multiline", TextareaLog, TextareaLogEdit, lines: true, rows: 9, width: 400f);

        EndEditing(runtime);
    }

    private const string SampleText = "The quick brown fox jumps";
    private static BibuiStr Sample() => BibuiStr.FromStrLit(SampleText);
    private static TextEditState At(int cursor, int mark) => new() { Cursor = cursor, Mark = mark };

    // ── Shared: centring in a row of fixed height ───────────────────────────

    private static Text CentredText(BibuiRuntime runtime, ReadOnlySpan<char> text, ComponentSize? size = null,
        UiColor? color = null, FontWeight weight = FontWeight.Regular)
    {
        runtime.BeginCenterAcross();
        runtime.BeginFontFace(weight: weight);
        Text centred = runtime.Text(text, size, color);
        runtime.EndFontFace();
        runtime.EndCenterAcross();
        return centred;
    }

    private static void CentredIcon(BibuiRuntime runtime, string name, float size, UiColor color)
    {
        runtime.BeginCenterAcross();
        runtime.Icon(name, size).Color = color;
        runtime.EndCenterAcross();
    }

    // ── Button ───────────────────────────────────────────────────────────────

    private enum Look { Primary, Secondary }

    // A caption above a row of buttons, 12 px above it.
    private static void Caption(BibuiRuntime runtime, string text, bool accent = false)
    {
        Bold(runtime, text, ComponentSize.Sm, accent ? TextColor.Accent : TextColor.Secondary);
        runtime.Gap(12f);
    }

    private static void ButtonPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Caption(runtime, "Variants");
        var variants = runtime.BeginParent(runtime.Row());
        DemoButton(runtime, "demo-btn-primary", "Primary", Look.Primary);
        runtime.Gap(10f);
        Tall(runtime, runtime.Button("demo-btn-secondary", "Secondary"));
        runtime.Gap(10f);
        Tall(runtime, runtime.Button("demo-btn-ghost", "Ghost", ghost: true));
        runtime.Gap(10f);
        Tall(runtime, runtime.Button("demo-btn-disabled", "Disabled", disabled: true));
        runtime.EndParent(variants);
        runtime.Gap(12f);

        Caption(runtime, "Sizes");
        var sizes = runtime.BeginParent(runtime.Row());
        Tall(runtime, runtime.Button("demo-btn-sm", "Small", size: ComponentSize.Sm), ComponentSize.Sm);
        runtime.Gap(10f);
        Tall(runtime, runtime.Button("demo-btn-md", "Medium", size: ComponentSize.Md));
        runtime.Gap(10f);
        Tall(runtime, runtime.Button("demo-btn-lg", "Large", size: ComponentSize.Lg), ComponentSize.Lg);
        runtime.EndParent(sizes);
        runtime.Gap(12f);

        Caption(runtime, "With icon");
        var withIcon = runtime.BeginParent(runtime.Row());
        DemoButton(runtime, "demo-btn-add", "Add item", Look.Primary, icon: "plus");
        runtime.Gap(10f);
        DemoButton(runtime, "demo-btn-delete", "Delete", Look.Secondary, icon: "trash");
        runtime.EndParent(withIcon);
        runtime.Gap(12f);

        Caption(runtime, "Custom colors");
        var colors = runtime.BeginParent(runtime.Row());
        DemoButton(runtime, "demo-btn-red", "Red", Look.Primary, fill: UiColor.Hex("#DC2626"));
        runtime.Gap(8f);
        DemoButton(runtime, "demo-btn-green", "Green", Look.Primary, fill: UiColor.Hex("#059669"));
        runtime.Gap(8f);
        DemoButton(runtime, "demo-btn-blue", "Blue", Look.Primary, fill: UiColor.Hex("#2563EB"));
        runtime.Gap(8f);
        DemoButton(runtime, "demo-btn-purple", "Purple", Look.Primary, fill: UiColor.Hex("#7C3AED"));
        runtime.Gap(8f);
        DemoButton(runtime, "demo-btn-border", "Custom border", Look.Secondary, border: UiColor.Hex("#3B82F6"));
        runtime.EndParent(colors);
        runtime.Gap(12f);

        Caption(runtime, "Loading (new)", accent: true);
        var loading = runtime.BeginParent(runtime.Row());
        DemoButton(runtime, "demo-btn-saving", "Saving…", Look.Secondary, loading: true);
        runtime.Gap(10f);
        DemoButton(runtime, "demo-btn-uploading", "Uploading", Look.Secondary, loading: true);
        runtime.Gap(10f);
        DemoButton(runtime, "demo-btn-loading-icon", "With icon", Look.Secondary, icon: "plus", loading: true);
        runtime.EndParent(loading);
        runtime.Gap(12f);

        Caption(runtime, "Interactive (clicks do something)", accent: true);
        var interactive = runtime.BeginParent(runtime.Row());
        if (DemoButton(runtime, "demo-click-btn", $"Clicked {state.DemoButtonClickCount}×", Look.Primary).Clicked)
            set(state with { DemoButtonClickCount = state.DemoButtonClickCount + 1 });
        runtime.Gap(10f);
        runtime.Text("Press, hold, release — watch the label count up and the fill darken while pressed.",
            ComponentSize.Sm, TextColor.Secondary);
        runtime.EndParent(interactive);
    }

    // A runtime.Button as tall as the reference button of its size — 32, 40 or 48 —
    // said by the caller: a height, and the padding that centres the label in it.
    private static void Tall(BibuiRuntime runtime, BibuiWidget button, ComponentSize size = ComponentSize.Md)
    {
        float height = size switch { ComponentSize.Sm => 32f, ComponentSize.Lg => 48f, _ => 40f };
        Tall(runtime, button.Node, height, size);
    }

    // A box holding one line of text, made height tall with the line centred
    // down it: the box's own padding, said by the caller.
    private static void Tall(BibuiRuntime runtime, UiNode node, float height, ComponentSize size = ComponentSize.Md)
    {
        float line = runtime.Fonts.LineHeight(runtime.FontInScope(size));
        node.SizeY = SemanticSize.Pixels(height);
        var pad = node.Style.Padding;
        node.Style.Padding = new EdgeInsets((height - line) / 2f, pad.Right, 0f, pad.Left);
    }

    // What runtime.Button does not draw — the primary fill, an icon, a colour of
    // the page's own, a spinner while loading — composed here: 40 px tall, the
    // label and icon centred down it. Loading keeps the label's width and shows
    // a spinner in its place.
    private static BibuiWidget DemoButton(BibuiRuntime runtime, string id, string label, Look look,
        string? icon = null, UiColor? fill = null, UiColor? border = null, bool loading = false)
    {
        Box box = runtime.BoxFromId(id, loading ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        Interaction interaction = runtime.InteractionsFor(box);
        box.ChildLayoutAxis = LayoutAxis.X;
        box.SizeY = SemanticSize.Pixels(40f);
        Style style = box.Style;
        style.CornerRadius = 8f;
        style.Padding = EdgeInsets.Symmetric(horizontal: 16f, vertical: 0f);

        UiColor ink;
        if (fill is { } f)
        {
            style.BackgroundColor = f;
            style.Draw = DrawFlags.HotEffects | DrawFlags.ActiveEffects;
            ink = UiColor.Hex("#FFFFFF");
        }
        else if (look == Look.Primary)
        {
            style.BackgroundColor = UiColor.Accent;
            style.HoverBackgroundColor = UiColor.AccentHover;
            ink = UiColor.AccentForeground;
        }
        else
        {
            style.Draw = DrawFlags.Border | DrawFlags.Background | DrawFlags.HotEffects | DrawFlags.ActiveEffects;
            if (border is { } b) style.BorderColor = b;
            ink = UiColor.TextPrimary;
        }

        ReadOnlySpan<char> text = label;
        if (loading)
        {
            // As wide as icon and label would be, the spinner centred across it.
            float width = runtime.Fonts.Measure(text, runtime.FontInScope(ComponentSize.Md)) + 32f;
            if (icon is not null) width += 24f;
            box.SizeX = SemanticSize.Pixels(width);
            runtime.BeginParent(box);
            runtime.BeginCenterX();
            runtime.BeginCenterY();
            runtime.Spinner(16f);
            runtime.EndCenterY();
            runtime.EndCenterX();
            runtime.EndParent(box);
            return new BibuiWidget(box, interaction);
        }

        box.SizeX = SemanticSize.ChildrenSum();
        runtime.BeginParent(box);
        if (icon is not null)
        {
            CentredIcon(runtime, icon, 16f, ink);
            runtime.Gap(8f);
        }
        CentredText(runtime, text, color: ink);
        runtime.EndParent(box);
        return new BibuiWidget(box, interaction);
    }

    // ── RadioGroup ───────────────────────────────────────────────────────────

    private static readonly RadioOption[] RadioOptions =
    [
        new("Option A", "option-a"),
        new("Option B", "option-b"),
        new("Option C", "option-c"),
        new("Disabled", "option-d", Disabled: true),
    ];

    private static void RadioGroupPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "Select one option");
        if (runtime.RadioGroup("demo-radio", RadioOptions, state.RadioValue).Picked is { } picked)
            set(state with { RadioValue = picked });
        runtime.Gap(16f);
        runtime.Text($"Selected: {state.RadioValue}", ComponentSize.Sm, TextColor.Accent);
    }

    // ── Select, sized by the page ────────────────────────────────────────────

    private static readonly (string Label, string Value)[] Countries =
        [("Denmark", "dk"), ("Germany", "de"), ("Japan", "jp"), ("United States", "us")];

    // The same Select, its trigger and rows sized by the page: a fixed width,
    // and taller rows and trigger.
    private static void CountrySelect(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "A set width, and taller rows");
        var chosen = Countries.FirstOrDefault(c => c.Value == state.CountryValue);
        var select = Select.Begin(runtime, "demo-select-country", chosen.Label ?? "Choose a country…", width: 240f);
        if (select.Open)
            foreach (var country in Countries)
            {
                var item = runtime.SelectItem($"demo-select-country-{country.Value}", country.Label,
                    country.Value == state.CountryValue);
                Tall(runtime, item.Node, 36f);
                if (item.Clicked)
                {
                    select.Close();
                    set(state with { CountryValue = country.Value });
                }
            }
        Tall(runtime, select.End(), TriggerHeight);

        runtime.Gap(16f);
        runtime.Text(state.CountryValue is null ? "No country selected" : $"Country: {state.CountryValue}",
            ComponentSize.Sm, state.CountryValue is null ? TextColor.Secondary : TextColor.Accent);
    }

    // ── Toggle ───────────────────────────────────────────────────────────────

    private static void TogglePage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "Pressable toggle button");
        var row = runtime.BeginParent(runtime.Row().Sized(y: Size.Px(40f)));
        runtime.BeginCenterAcross();
        if (runtime.Toggle("demo-toggle", state.TogglePressed, "Star", icon: "star").Clicked)
            set(state with { TogglePressed = !state.TogglePressed });
        runtime.EndCenterAcross();
        runtime.Gap(10f);
        CentredText(runtime, state.TogglePressed ? "Pressed" : "Not pressed", ComponentSize.Sm,
            state.TogglePressed ? TextColor.Accent : TextColor.Secondary);
        runtime.EndParent(row);
        runtime.Gap(16f);
        runtime.Toggle("demo-toggle-disabled", false, "Disabled", disabled: true);
    }

    // ── Pickers: a trigger, and a popup opened as a dropdown under it ───────

    private const float TriggerHeight = 40f;

    // A trigger in the input look, width wide, that takes its press unless
    // disabled; its contents are the caller's, built into it with it pushed.
    private static Box PickerTrigger(BibuiRuntime runtime, string id, float width, bool disabled, out bool clicked)
    {
        Box trigger = runtime.BoxFromId(id, disabled ? InteractionKinds.Disabled : InteractionKinds.LeftPress);
        clicked = runtime.InteractionsFor(trigger).Clicked;
        trigger.ChildLayoutAxis = LayoutAxis.X;
        trigger.SizeX = SemanticSize.Pixels(width);
        trigger.SizeY = SemanticSize.Pixels(TriggerHeight);
        Style style = trigger.Style;
        style.Padding = EdgeInsets.Symmetric(horizontal: 12f, vertical: 0f);
        style.CornerRadius = 6f;
        style.BorderWidth = 1f;
        style.BorderColor = UiColor.Border;
        style.BackgroundColor = disabled ? UiColor.SurfaceRaised : UiColor.Surface;
        if (!disabled) style.HoverBorderColor = UiColor.BorderHover;
        return trigger;
    }

    // The popup named id, when it is open, 4 px under where trigger was drawn
    // last frame, as large as what is built into it. Null when it is not open.
    private static Box? BeginPopup(BibuiRuntime runtime, string id, Box trigger, float padding)
    {
        if (runtime.BeginDropDown(id) is not { } popup) return null;
        popup.SizeX = popup.SizeY = SemanticSize.ChildrenSum();
        // Under the trigger; above it when the window has no room below, and
        // against the window's bottom edge when it has none above either. Its
        // size is last frame's: unknown on the first, when it opens below.
        float x = trigger.Rect.X, y = trigger.Rect.Y + trigger.Extent.Y + 4f;
        var size = popup.Extent;
        if (y + size.Y > runtime.SurfaceHeight)
        {
            float above = trigger.Rect.Y - 4f - size.Y;
            y = above >= 0f ? above : MathF.Max(0f, runtime.SurfaceHeight - size.Y);
        }
        x = MathF.Max(0f, MathF.Min(x, runtime.SurfaceWidth - size.X));
        popup.At(x, y);
        popup.Style.Padding = EdgeInsets.All(padding);
        popup.Style.CornerRadius = 6f;
        popup.Style.BorderWidth = 1f;
        popup.Style.BorderColor = UiColor.Border;
        popup.Style.BackgroundColor = UiColor.SurfaceRaised;
        return popup;
    }

    // After its contents have asked: a press on the popup's padding stops there
    // rather than reaching what is beneath.
    private static void EndPopup(BibuiRuntime runtime, Box popup)
    {
        popup.Claims = InteractionKinds.Press;
        runtime.InteractionsFor(popup);
    }

    // ── Calendar ─────────────────────────────────────────────────────────────

    private const float DayCell = 36f;
    private const float CalendarRowGap = 4f;
    private static readonly string[] Weekdays = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];

    /// <summary>What a month view reported: a day picked, or a step to the month before or after.</summary>
    private readonly record struct MonthResult(DateOnly? Picked, int Step);

    // A month view of month, selected marked, built under the parent in scope:
    // a caption row with a step either side — across the parent when fill, or
    // the grid's width — then the weekdays and the weeks, Sunday first, 36 px cells.
    private static MonthResult MonthView(BibuiRuntime runtime, string id, DateOnly month, DateOnly? selected,
        bool fill = false)
    {
        int step = 0;
        DateOnly? picked = null;

        var view = runtime.BeginParent(fill ? runtime.Column().Sized(Size.Fill) : runtime.Column());
        var caption = runtime.BeginParent(fill ? runtime.Row().Sized(Size.Fill, Size.Px(DayCell)) : runtime.Row().Sized(Size.Px(7 * DayCell), Size.Px(DayCell)));
        if (NavButton(runtime, id + "-prev", "chevron-left")) step = -1;
        runtime.BeginCenter();
        CentredText(runtime, $"{month:MMMM yyyy}", weight: FontWeight.Bold);
        runtime.EndCenter();
        if (NavButton(runtime, id + "-next", "chevron-right")) step = 1;
        runtime.EndParent(caption);
        runtime.Gap(CalendarRowGap);

        var weekdays = runtime.BeginParent(runtime.Row().Sized(y: Size.Px(DayCell)));
        foreach (var day in Weekdays)
            DayBox(runtime, runtime.Box(), day, TextColor.Primary);
        runtime.EndParent(weekdays);

        var first = new DateOnly(month.Year, month.Month, 1);
        int lead = (int)first.DayOfWeek;
        int days = DateTime.DaysInMonth(month.Year, month.Month);
        for (int week = 0; week * 7 - lead < days; week++)
        {
            runtime.Gap(CalendarRowGap);
            var weekRow = runtime.BeginParent(runtime.Row().Sized(y: Size.Px(DayCell)));
            for (int col = 0; col < 7; col++)
            {
                int d = week * 7 + col - lead + 1;
                if (d < 1 || d > days)
                {
                    DayBox(runtime, runtime.Box(), default, TextColor.Primary);
                    continue;
                }
                var date = new DateOnly(month.Year, month.Month, d);
                bool isSelected = date == selected;
                Box cell = runtime.BoxFromId($"{id}-day-{d}", InteractionKinds.LeftPress);
                if (runtime.InteractionsFor(cell).Clicked) picked = date;
                DayBox(runtime, cell, $"{d}", isSelected ? TextColor.AccentForeground : TextColor.Primary);
                cell.Style.CornerRadius = 6f;
                if (isSelected) cell.Style.BackgroundColor = UiColor.Accent;
                else cell.Style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;
            }
            runtime.EndParent(weekRow);
        }
        runtime.EndParent(view);
        return new MonthResult(picked, step);
    }

    // A 36 px cell, text centred in it.
    private static void DayBox(BibuiRuntime runtime, Box box, ReadOnlySpan<char> text, UiColor color)
    {
        box.ChildLayoutAxis = LayoutAxis.X;
        box.Sized(Size.Px(DayCell), Size.Px(DayCell));
        if (text.IsEmpty) return;
        runtime.BeginParent(box);
        runtime.BeginCenter();
        CentredText(runtime, text, color: color);
        runtime.EndCenter();
        runtime.EndParent(box);
    }

    private static bool NavButton(BibuiRuntime runtime, string id, string icon)
    {
        runtime.BeginCenterAcross();
        Box button = runtime.BoxFromId(id, InteractionKinds.LeftPress);
        bool clicked = runtime.InteractionsFor(button).Clicked;
        button.ChildLayoutAxis = LayoutAxis.X;
        button.Sized(Size.Px(28f), Size.Px(28f));
        button.Style.CornerRadius = 6f;
        button.Style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;
        button.Style.Padding = EdgeInsets.All(7f);
        runtime.BeginParent(button);
        runtime.Icon(icon, 14f).Color = UiColor.TextPrimary;
        runtime.EndParent(button);
        runtime.EndCenterAcross();
        return clicked;
    }

    // The month the standalone calendar shows: the page's own, so it can step
    // through months whether or not a day is selected.
    private static DateOnly? CalendarMonth;

    private static void CalendarPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "Standalone calendar picker");
        DateOnly month = CalendarMonth ?? state.CalendarSelected ?? DateOnly.FromDateTime(DateTime.Today);
        Box frame = runtime.Column().Sized(Size.Fill);
        frame.Style.Padding = EdgeInsets.All(12f);
        // The caption runs the width of the page; the weeks keep their cells.
        runtime.BeginParent(frame);
        MonthResult result = MonthView(runtime, "demo-calendar", month, state.CalendarSelected, fill: true);
        runtime.EndParent(frame);
        if (result.Step != 0) CalendarMonth = month.AddMonths(result.Step);
        if (result.Picked is { } day) set(state with { CalendarSelected = day });

        runtime.Gap(16f);
        runtime.Text(state.CalendarSelected is null ? "No date selected" : $"Selected: {state.CalendarSelected:MMMM d, yyyy}",
            ComponentSize.Sm, state.CalendarSelected is null ? TextColor.Secondary : TextColor.Accent);
    }

    // ── DatePicker ───────────────────────────────────────────────────────────

    private static void DatePickerPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "Pick a date from a popover calendar");
        const string id = "demo-datepicker-popup";
        Box trigger = PickerTrigger(runtime, "demo-datepicker", 240f, disabled: false, out bool toggled);
        if (toggled)
            if (runtime.IsDropDownOpen(id)) runtime.CloseDropDown(id); else runtime.OpenDropDown(id, trigger);
        runtime.BeginParent(trigger);
        CentredIcon(runtime, "calendar", 16f, UiColor.TextSecondary);
        runtime.Gap(8f);
        CentredText(runtime, state.DatePickerValue is { } v ? $"{v:MMMM d, yyyy}" : "Pick a date",
            color: state.DatePickerValue is null ? TextColor.Secondary : TextColor.Primary);
        runtime.EndParent(trigger);

        if (BeginPopup(runtime, id, trigger, padding: 12f) is { } popup)
        {
            runtime.BeginParent(popup);
            MonthResult result = MonthView(runtime, "demo-datepicker", state.DatePickerMonth, state.DatePickerValue);
            runtime.EndParent(popup);
            EndPopup(runtime, popup);
            if (result.Picked is { } day)
            {
                runtime.CloseDropDown(id);
                set(state with { DatePickerValue = day, DatePickerMonth = day });
            }
            else if (result.Step != 0)
                set(state with { DatePickerMonth = state.DatePickerMonth.AddMonths(result.Step) });
        }

        runtime.Gap(16f);
        runtime.Text(state.DatePickerValue is null ? "No date selected" : $"Selected: {state.DatePickerValue:yyyy-MM-dd}",
            ComponentSize.Sm, state.DatePickerValue is null ? TextColor.Secondary : TextColor.Accent);
    }

    // ── TimePicker ───────────────────────────────────────────────────────────

    private static void TimePickerPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "Select a time");
        int hour = state.TimePickerHour, minute = state.TimePickerMinute;
        const string id = "demo-timepicker-popup";
        Box trigger = TimeTrigger(runtime, "demo-timepicker", $"{hour:D2}:{minute:D2}", disabled: false, out bool toggled);
        if (toggled)
            if (runtime.IsDropDownOpen(id)) runtime.CloseDropDown(id); else runtime.OpenDropDown(id, trigger);
        if (BeginPopup(runtime, id, trigger, padding: 0f) is { } popup)
        {
            popup.SizeX = SemanticSize.Pixels(200f);
            runtime.BeginParent(popup);
            var spinners = runtime.BeginParent(runtime.Row().Sized(Size.Fill));
            hour = Spinner(runtime, "demo-timepicker-hour", "Hour", hour, 24, 1);
            Box divider = runtime.Box().Sized(Size.Px(1f), Size.Px(SpinnerHeight));
            divider.Style.BackgroundColor = UiColor.Border;
            minute = Spinner(runtime, "demo-timepicker-minute", "Min", minute, 60, 5);
            runtime.EndParent(spinners);
            runtime.EndParent(popup);
            EndPopup(runtime, popup);
        }
        if (hour != state.TimePickerHour || minute != state.TimePickerMinute)
            set(state with { TimePickerHour = hour, TimePickerMinute = minute });

        runtime.Gap(16f);
        runtime.Text($"Selected: {state.TimePickerHour:D2}:{state.TimePickerMinute:D2}", ComponentSize.Sm, TextColor.Accent);
        runtime.Gap(16f);
        Heading(runtime, "12-hour format");
        int h12 = state.TimePickerHour % 12 == 0 ? 12 : state.TimePickerHour % 12;
        TimeTrigger(runtime, "demo-timepicker-12h", $"{h12}:{state.TimePickerMinute:D2} {(state.TimePickerHour < 12 ? "AM" : "PM")}",
            disabled: false, out _);
        runtime.Gap(16f);
        TimeTrigger(runtime, "demo-timepicker-disabled", "Disabled", disabled: true, out _);
    }

    private static Box TimeTrigger(BibuiRuntime runtime, string id, string text, bool disabled, out bool clicked)
    {
        Box trigger = PickerTrigger(runtime, id, 200f, disabled, out clicked);
        runtime.BeginParent(trigger);
        CentredIcon(runtime, "clock", 16f, disabled ? UiColor.TextDisabled : UiColor.TextSecondary);
        runtime.Gap(8f);
        CentredText(runtime, text, color: disabled ? TextColor.Disabled : TextColor.Primary);
        runtime.EndParent(trigger);
        return trigger;
    }

    private const float SpinnerHeight = 180f;

    // One column of the time popup: its name, and value between a step up and
    // a step down, wrapping at count. Returns the value after this frame's steps.
    private static int Spinner(BibuiRuntime runtime, string id, string name, int value, int count, int step)
    {
        Box column = runtime.Box();
        column.ChildLayoutAxis = LayoutAxis.Y;
        column.SizeX = SemanticSize.ParentPct(1f);
        column.SizeY = SemanticSize.Pixels(SpinnerHeight);
        column.Style.Padding = EdgeInsets.Symmetric(horizontal: 0f, vertical: 8f);
        runtime.BeginParent(column);
        runtime.BeginCenterAcross();
        Bold(runtime, name, ComponentSize.Sm, TextColor.Secondary);
        runtime.EndCenterAcross();
        runtime.Gap(12f);
        var up = runtime.BeginParent(runtime.Row().Sized(Size.Fill, Size.Px(28f)));
        runtime.BeginCenter();
        if (NavButton(runtime, id + "-up", "chevron-up")) value = (value + step) % count;
        runtime.EndCenter();
        runtime.EndParent(up);
        runtime.Gap(8f);
        runtime.BeginCenterAcross();
        Bold(runtime, $"{value:D2}", ComponentSize.Lg);
        runtime.EndCenterAcross();
        runtime.Gap(8f);
        var down = runtime.BeginParent(runtime.Row().Sized(Size.Fill, Size.Px(28f)));
        runtime.BeginCenter();
        if (NavButton(runtime, id + "-down", "chevron-down")) value = (value - step + count) % count;
        runtime.EndCenter();
        runtime.EndParent(down);
        runtime.EndParent(column);
        return value;
    }

    // ── ColorPicker ──────────────────────────────────────────────────────────

    private static readonly string[] DefaultPalette =
    [
        "#EF4444", "#F97316", "#EAB308", "#22C55E", "#06B6D4", "#3B82F6",
        "#6366F1", "#8B5CF6", "#EC4899", "#F43F5E", "#64748B", "#18181B",
    ];

    private static readonly string[] CustomPalette = ["#FF6B6B", "#4ECDC4", "#45B7D1", "#96CEB4", "#FFEAA7", "#DDA0DD"];

    // The custom-palette picker's own value, which the gallery state has no field for.
    private static string CustomColor = "#FF6B6B";

    private static void ColorPickerPage(BibuiRuntime runtime, InputsState state, Action<InputsState> set)
    {
        Heading(runtime, "Pick a color from a palette");
        if (ColorPicker(runtime, "demo-colorpicker", state.ColorPickerValue, DefaultPalette) is { } color)
            set(state with { ColorPickerValue = color });

        runtime.Gap(16f);
        runtime.Text(state.ColorPickerValue is null ? "No color selected" : $"Color: {state.ColorPickerValue}",
            ComponentSize.Sm, state.ColorPickerValue is null ? TextColor.Secondary : TextColor.Accent);
        runtime.Gap(16f);
        Heading(runtime, "Custom palette");
        if (ColorPicker(runtime, "demo-colorpicker-custom", CustomColor, CustomPalette) is { } c) CustomColor = c;
        runtime.Gap(16f);
        ColorPicker(runtime, "demo-colorpicker-disabled", "#3B82F6", DefaultPalette, disabled: true);
    }

    // A trigger showing value as a swatch and its hex, which opens a popup of
    // the palette, six to a row, under it. The colour picked, which closes it.
    private static string? ColorPicker(BibuiRuntime runtime, string id, string? value,
        IReadOnlyList<string> palette, bool disabled = false)
    {
        string popupId = id + "-popup";
        Box trigger = PickerTrigger(runtime, id, 220f, disabled, out bool toggled);
        if (toggled)
            if (runtime.IsDropDownOpen(popupId)) runtime.CloseDropDown(popupId); else runtime.OpenDropDown(popupId, trigger);
        runtime.BeginParent(trigger);
        runtime.BeginCenterAcross();
        Box swatch = runtime.Box().Sized(Size.Px(20f), Size.Px(20f));
        swatch.Style.CornerRadius = 4f;
        swatch.Style.BackgroundColor = value is null ? UiColor.SurfaceRaised : UiColor.Hex(value);
        if (value is null) swatch.Style.BorderColor = UiColor.Border;
        runtime.EndCenterAcross();
        runtime.Gap(8f);
        CentredText(runtime, value ?? "Pick a color",
            color: disabled ? TextColor.Disabled : value is null ? TextColor.Secondary : TextColor.Primary);
        runtime.Spacer(Size.Fill);
        CentredIcon(runtime, "chevron-down", 14f, UiColor.TextSecondary);
        runtime.EndParent(trigger);

        string? picked = null;
        if (BeginPopup(runtime, popupId, trigger, padding: 12f) is { } popup)
        {
            runtime.BeginParent(popup);
            for (int start = 0; start < palette.Count; start += 6)
            {
                if (start > 0) runtime.Gap(6f);
                var row = runtime.BeginParent(runtime.Row());
                for (int i = start; i < Math.Min(start + 6, palette.Count); i++)
                {
                    if (i > start) runtime.Gap(6f);
                    string hex = palette[i];
                    bool chosen = string.Equals(hex, value, StringComparison.OrdinalIgnoreCase);
                    Box option = runtime.BoxFromId($"{id}-swatch-{i}", InteractionKinds.LeftPress);
                    if (runtime.InteractionsFor(option).Clicked) picked = hex;
                    option.Sized(Size.Px(28f), Size.Px(28f));
                    option.Style.CornerRadius = 4f;
                    option.Style.BackgroundColor = UiColor.Hex(hex);
                    option.Style.BorderWidth = chosen ? 2f : 1f;
                    option.Style.BorderColor = chosen ? UiColor.TextPrimary : UiColor.Border;
                }
                runtime.EndParent(row);
            }
            runtime.EndParent(popup);
            EndPopup(runtime, popup);
        }
        if (picked is not null) runtime.CloseDropDown(popupId);
        return picked;
    }
}
