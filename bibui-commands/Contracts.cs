namespace Bibui.Commands;

// What a command is, written on its record and read by cmdgen from the source: no
// reflection at run time. Its meaning is the standard [Description]
// (System.ComponentModel), on the record and on any argument whose name does not say
// enough; its label and its contracts are these.

/// <summary>
/// A command set: an <c>abstract partial record</c> whose nested records are its commands. cmdgen
/// makes it a <see cref="Cmd"/>, and writes their wire names (<c>Panel_Close</c> is <c>panel.close</c>),
/// a push method each on a <see cref="Model{TCtx}"/>, and the set's table of <see cref="OpInfo"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CommandSetAttribute : Attribute;

/// <summary>
/// A label: a few words for people. On a command, where it is listed (a palette, a menu, an MCP
/// tool's title); on a kind, its name; on a key, the field's name on screen ("Note", its empty
/// field's placeholder). What it means is its <c>[Description]</c>. Without one, a label is made
/// from its name: <c>Panel_Close</c> is "Panel close".
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Parameter)]
public sealed class LabelAttribute(string label) : Attribute
{
    public string Label { get; } = label;
}

/// <summary>
/// A command's text where it sits in a node's row (<see cref="RowAttribute"/>): a word or a sign
/// (<c>"X"</c>, <c>"Clear"</c>), where its label would not fit. Lists keep the label.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ShortAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

/// <summary>
/// An icon for a command or a kind, by its name in bibui's icon set (<c>"x"</c>, <c>"camera"</c>,
/// or one the app registers), shown beside its label wherever it is listed (a palette, a menu). It is
/// declared once on the command and read from its table, so every place that lists the command shows
/// the same icon without choosing one itself.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class IconAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// A contract: the command changes nothing; it only reads (an MCP tool's read-only hint).
/// Not <c>[ReadOnly]</c>: <c>System.ComponentModel</c>, where <c>[Description]</c> comes from, has one.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ReadsOnlyAttribute : Attribute;

/// <summary>What kind of operation an <see cref="OpInfo"/> describes.</summary>
public enum OpKind { Command }

/// <summary>An operation's contracts, from <see cref="ReadsOnlyAttribute"/>.</summary>
[Flags]
public enum OpFlags { None = 0, ReadOnly = 1 }

/// <summary>
/// One argument of an operation: its name (as in its JSON), its type as written, its description,
/// and for a handle the kind of node it names (a typed id's: CaseId names a case), or null.
/// </summary>
public sealed record ArgInfo(string Name, string Type, string Description, string? Kind = null);

/// <summary>
/// One operation, as its set's generated table holds it: its wire name, label,
/// description, contracts, type and arguments, its text in a row (<see cref="ShortAttribute"/>;
/// null: its label) and its icon (<see cref="IconAttribute"/>). For anything that lists or explains the app's commands: a palette, a test
/// port's schema, an MCP server, a node's row.
/// </summary>
public sealed record OpInfo(string Wire, string Label, string Description, OpKind Kind, OpFlags Flags, Type Type, ArgInfo[] Args, string? Short = null, string? Icon = null)
{
    // cmdgen's, per command.
    public ArgInfo[] Context = [];   // what it reads from the context ([Context.DependsOn]): each field by its wire name (panel_from) and kind
    public Cmd? Cmd;                 // the command, for one that takes no arguments: all it needs is the context, so a row pushes this; null for one with arguments

    /// <summary>The argument named <paramref name="name"/>, or null.</summary>
    public ArgInfo? Arg(string name)
    {
        foreach (var arg in Args) if (arg.Name == name) return arg;
        return null;
    }
}
