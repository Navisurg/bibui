using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// The components that keep a retained box, called on the runtime that builds
/// them: <c>runtime.Button("save", "Save")</c> for <c>Button.Build(runtime, "save", "Save")</c>.
///
/// <para>Each forwards to the component's own function, so there is still one
/// implementation and the two spellings cannot drift. Only components named
/// by an id are here — the ones a caller asks what happened to. Layout
/// (<see cref="Arrange"/>'s Column, Row, ...), the keyless leaves (Badge,
/// Avatar, Separator, ...) and the scopes that hold other content (Dialog,
/// Popover, Menu.Begin, Select.Begin) keep their own spelling.</para>
/// </summary>
public static class BibuiRuntimeComponents
{
    /// <inheritdoc cref="global::Bibui.Runtime.Button.Build"/>
    public static BibuiWidget Button(this BibuiRuntime runtime, string id, ReadOnlySpan<char> label,
        bool ghost = false, bool disabled = false, ComponentSize size = ComponentSize.Md)
        => global::Bibui.Runtime.Button.Build(runtime, id, label, ghost, disabled, size);

    /// <inheritdoc cref="global::Bibui.Runtime.Checkbox.Build"/>
    public static BibuiWidget Checkbox(this BibuiRuntime runtime, string id, bool on, ReadOnlySpan<char> label = default,
        bool disabled = false, bool indeterminate = false)
        => global::Bibui.Runtime.Checkbox.Build(runtime, id, on, label, disabled, indeterminate);

    /// <inheritdoc cref="global::Bibui.Runtime.Switch.Build"/>
    public static BibuiWidget Switch(this BibuiRuntime runtime, string id, bool on, ReadOnlySpan<char> label = default,
        bool disabled = false, float height = 24f)
        => global::Bibui.Runtime.Switch.Build(runtime, id, on, label, disabled, height);

    /// <inheritdoc cref="global::Bibui.Runtime.Toggle.Build"/>
    public static BibuiWidget Toggle(this BibuiRuntime runtime, string id, bool on, ReadOnlySpan<char> label = default,
        string? icon = null, bool disabled = false)
        => global::Bibui.Runtime.Toggle.Build(runtime, id, on, label, icon, disabled);

    /// <inheritdoc cref="global::Bibui.Runtime.Slider.Build"/>
    public static SliderWidget Slider(this BibuiRuntime runtime, string id, float value,
        float min = 0f, float max = 1f, float step = 0f, ReadOnlySpan<char> label = default, bool disabled = false)
        => global::Bibui.Runtime.Slider.Build(runtime, id, value, min, max, step, label, disabled);

    /// <inheritdoc cref="global::Bibui.Runtime.RadioGroup.Build"/>
    public static RadioGroupWidget RadioGroup(this BibuiRuntime runtime, string id,
        IReadOnlyList<RadioOption> items, string? value, bool disabled = false)
        => global::Bibui.Runtime.RadioGroup.Build(runtime, id, items, value, disabled);

    /// <inheritdoc cref="global::Bibui.Runtime.ToggleGroup.Build"/>
    public static ToggleGroupWidget ToggleGroup(this BibuiRuntime runtime, string id,
        IReadOnlyList<ToggleOption> items, string? value)
        => global::Bibui.Runtime.ToggleGroup.Build(runtime, id, items, value);

    /// <inheritdoc cref="global::Bibui.Runtime.Tabs.Build"/>
    public static TabsWidget Tabs(this BibuiRuntime runtime, string id, IReadOnlyList<TabOption> items, string? value)
        => global::Bibui.Runtime.Tabs.Build(runtime, id, items, value);

    /// <inheritdoc cref="global::Bibui.Runtime.Expander.Build"/>
    public static ExpanderWidget Expander(this BibuiRuntime runtime, string id, ReadOnlySpan<char> title, bool open)
        => global::Bibui.Runtime.Expander.Build(runtime, id, title, open);

    /// <inheritdoc cref="global::Bibui.Runtime.Scrollbar.Build"/>
    public static BibuiWidget Scrollbar(this BibuiRuntime runtime, string id, Box view, LayoutAxis axis = LayoutAxis.Y)
        => global::Bibui.Runtime.Scrollbar.Build(runtime, id, view, axis);

    /// <inheritdoc cref="global::Bibui.Runtime.Markdown.Build(BibuiRuntime, string, BibuiStr, float?, ComponentSize)"/>
    public static MarkdownWidget Markdown(this BibuiRuntime runtime, string id, BibuiStr source, float? width = null,
        ComponentSize textSize = ComponentSize.Md)
        => global::Bibui.Runtime.Markdown.Build(runtime, id, source, width, textSize);

    /// <inheritdoc cref="global::Bibui.Runtime.Markdown.Build(BibuiRuntime, string, MarkdownDocument, float?, ComponentSize)"/>
    public static MarkdownWidget Markdown(this BibuiRuntime runtime, string id, MarkdownDocument document, float? width = null,
        ComponentSize textSize = ComponentSize.Md)
        => global::Bibui.Runtime.Markdown.Build(runtime, id, document, width, textSize);

    /// <inheritdoc cref="global::Bibui.Runtime.Menu.Item"/>
    public static BibuiWidget MenuItem(this BibuiRuntime runtime, string id, ReadOnlySpan<char> label,
        string? icon = null, ReadOnlySpan<char> shortcut = default, bool disabled = false)
        => Menu.Item(runtime, id, label, icon, shortcut, disabled);

    /// <inheritdoc cref="global::Bibui.Runtime.Select.Item"/>
    public static BibuiWidget SelectItem(this BibuiRuntime runtime, string id, ReadOnlySpan<char> label,
        bool selected = false, bool disabled = false)
        => Select.Item(runtime, id, label, selected, disabled);

    /// <inheritdoc cref="global::Bibui.Runtime.TextField.SingleLine"/>
    public static BibuiWidget TextField(this BibuiRuntime runtime, string id, BibuiStr text,
        TextEditState edit, bool focused, uint events = global::Bibui.Runtime.TextField.SingleLineEvents,
        TextColor textColor = TextColor.Primary, ReadOnlySpan<char> placeholder = default)
        => global::Bibui.Runtime.TextField.SingleLine(runtime, id, text, edit, focused, events, textColor, placeholder);

    /// <inheritdoc cref="global::Bibui.Runtime.TextField.Multiline"/>
    public static BibuiWidget MultilineTextField(this BibuiRuntime runtime, string id, BibuiStr text,
        TextEditState edit, bool focused, uint events = global::Bibui.Runtime.TextField.MultiLineEvents,
        TextColor textColor = TextColor.Primary, ReadOnlySpan<char> placeholder = default, TextWrap wrap = TextWrap.Words)
        => global::Bibui.Runtime.TextField.Multiline(runtime, id, text, edit, focused, events, textColor, placeholder, wrap);

    /// <inheritdoc cref="global::Bibui.Runtime.TextField.Ellipsis"/>
    public static BibuiWidget EllipsisTextField(this BibuiRuntime runtime, string id, BibuiStr text,
        TextEditState edit, bool focused, uint events = 0,
        TextColor textColor = TextColor.Primary, ReadOnlySpan<char> placeholder = default)
        => global::Bibui.Runtime.TextField.Ellipsis(runtime, id, text, edit, focused, events, textColor, placeholder);
}
