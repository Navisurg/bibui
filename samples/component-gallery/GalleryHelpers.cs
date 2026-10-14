using Bibui;
using Bibui.Runtime;

namespace ComponentGallery;

internal static class GalleryHelpers
{
    // The text a read-only field shows: one buffer per field id, refilled when
    // the text changes. A field draws from a BibuiStr, and keeping one per field
    // (not one per string ever shown) keeps the count to the gallery's fields.
    private static readonly Dictionary<string, BibuiStr> Shown = new();

    internal static BibuiStr Held(string id, ReadOnlySpan<char> text)
    {
        if (!Shown.TryGetValue(id, out var str)) Shown[id] = str = new BibuiStr(0);
        if (!str.Str.SequenceEqual(text)) str.ReplaceRange((0, (int)str.Length), text);
        return str;
    }

    // What each page shows, under its title: what the thing is, and the bibui
    // call that builds it where there is one.
    internal static string ComponentDescription(string route) => route switch
    {
    "Button"           => "Button.Build: a press target that reports Clicked. The caller decides what a click does.",
    "Input"            => "One line of text to type into: TextField.SingleLine in a framed box.",
    "Textarea"         => "Several lines of text to type into: TextField.Multiline, wrapping and scrolling.",
    "TextField"        => "The text field itself: a buffer and an edit state the caller holds, drawn with its caret and selection.",
    "Checkbox"         => "Checkbox.Build: on, off or mixed, with a label. It reports a click; the caller flips the value.",
    "Switch"           => "Switch.Build: an on/off setting that slides between its two ends.",
    "RadioGroup"       => "RadioGroup.Build: exactly one of a few options, all shown at once.",
    "Select"           => "Select.Begin, Item and End: a button that opens a list of choices under it.",
    "Slider"           => "Slider.Build: a value in a range, set by dragging the thumb.",
    "Toggle"           => "Toggle.Build: a button that stays pressed until pressed again.",
    "ToggleGroup"      => "ToggleGroup.Build: toggles side by side, of which one is on.",
    "DatePicker"       => "A field showing a date, which opens a month to pick another from.",
    "Calendar"         => "A month of days to pick one from, stepping a month at a time.",
    "TimePicker"       => "A field showing a time, which opens hours and minutes to step through.",
    "ColorPicker"      => "A field showing a colour, which opens a palette to pick another from.",
    "Text"             => "runtime.Text: one run of text, in a size and a colour from the theme.",
    "Badge"            => "Badge.Build: a short label in a coloured pill, for a status or a count.",
    "Avatar"           => "Avatar.Build: a round mark for a person, from their initials.",
    "Card"             => "A raised, bordered box holding a title, a line under it and a body.",
    "Alert"            => "A banner across the page that says something the reader should know.",
    "Separator"        => "Separator.Build: a thin rule between two things, across or down.",
    "Label"            => "The name above an input, saying what goes in it.",
    "Progress"         => "Progress.Build: how much of a task is done, as a filled bar.",
    "Skeleton"         => "Grey shapes where content will be, shown while it loads.",
    "Spinner"          => "A turning mark that says something is in progress.",
    "RatioBox"         => "A box whose height follows from its width, at a fixed ratio.",
    "KeyboardShortcut" => "KeyboardShortcut.Build: the keys of a shortcut, drawn as key caps.",
    "Table"            => "Rows of values under column names, with a rule between rows.",
    "Markdown"         => "Markdown.Build: Markdown source drawn as a document, again as it is typed.",
    "PagedTable"       => "A table that shows a page of its rows at a time, with buttons to step through them.",
    "FormField"        => "An input with its label, a hint under it, and an error when the value is wrong.",
    "ListItem"         => "A row with an icon and a label, which can be selected or disabled.",
    "EmptyState"       => "What a list or a page shows before it has anything in it, and how to begin.",
    "Toast"            => "A short message in the corner of the window, which closes or can be undone.",
    "Dialog"           => "Dialog.Begin: a panel over the page that takes the input until it is closed.",
    "AlertDialog"      => "A dialog that asks before something that cannot be undone. Only its buttons answer it.",
    "Popover"          => "Popover.Begin: a panel under the control that opened it, holding anything.",
    "HoverPreview"     => "A popover the pointer opens: point at a name to see more about it.",
    "Tooltip"          => "Tooltip.Build: a short hint beside what the pointer is over.",
    "Drawer"           => "Dialog.Begin from a side: a panel that slides in from the right or from the bottom.",
    "DropdownMenu"     => "Menu.BeginBelow: a list of actions that opens under a button.",
    "ContextMenu"      => "Menu.Begin: a list of actions that opens where the right button was pressed.",
    "CommandPalette"   => "A search field over every command: type to filter, pick one to run it.",
    "Tabs"             => "Tabs.Build: a row of tabs, each showing its own panel below.",
    "Accordion"        => "Several expanders in a column, of which one is open at a time.",
    "Expander"         => "Expander.Build: a header that shows or hides what is under it.",
    "NavBar"           => "A row of links to the parts of an app, the current one marked.",
    "Sidebar"          => "Links down the side of an app, the current one marked.",
    "Breadcrumb"       => "The way down to the current page, each step above it a link.",
    "Pagination"       => "Previous, next, and the numbered pages around the current one.",
    "MenuBar"          => "Menus along the top of a window. With one open, pointing at another opens that.",
    "SplitPane"        => "Two panes either side of a divider that drags to share the room between them.",
    "Carousel"         => "One slide at a time, with arrows and dots to move between them.",
    "ButtonStrip"      => "Buttons joined into one outline, for a few related actions.",
    "Stack"            => "Children placed at an offset with .At, over children laid out in flow.",
    "InteractiveBox"   => "A box that claims a press: a styled container without button semantics.",
    "Section"          => "Section.Build: an uppercase caption over a column of controls.",
    "Scrollbar"        => "Scrollbar.Build: a bar beside a scrolling view. Drag the thumb, or press the track to page.",
    _                  => "",
};

    internal static string Slug(string value)
    => value.ToLowerInvariant()
            .Replace(" ", "-", StringComparison.Ordinal)
            .Replace("&", "and", StringComparison.Ordinal)
            .Replace("/", "-", StringComparison.Ordinal);
}
