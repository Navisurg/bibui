using System.Text.Json;

namespace Bibui.Commands;

// The context: the environment a command runs in, which panel, which case, which node; its arguments are
// what to do. It is the app's own type, cmdgen's Context, a field per thing some command depends on
// ([Context.DependsOn<PanelId>(Role.From)] on a command), and View. The model keeps a stack of them
// (Model.ContextScope: the code inside a scope is in it) and queues each command with a copy of the top,
// as rad's rd_cmd copies its registers; the apply is given it. The library never reads a field of one:
// it stores, copies and hands back, and asks the app's functions (ContextOps) for the rest.

/// <summary>
/// The roles a node can play in a command, a fixed vocabulary: the field a command depends on is its
/// kind's in that role (<c>[Context.DependsOn&lt;PanelId&gt;(Role.From)]</c> is the context's PanelFrom), so
/// two commands with a panel and a second one share one field each, however many commands there are.
/// </summary>
public enum Role : byte
{
    /// <summary>The node the code is about: the context's field named as its kind (Panel).</summary>
    This,
    /// <summary>Where something comes from, of two of a kind (PanelFrom).</summary>
    From,
    /// <summary>Where something goes, of two of a kind (PanelTo).</summary>
    To,
}

/// <summary>The first field <paramref name="op"/> declares that <paramref name="ctx"/> holds none of (<see cref="OpInfo.Context"/>), or null when it holds them all.</summary>
public delegate ArgInfo? MissingContext<TCtx>(in TCtx ctx, OpInfo op) where TCtx : struct;

/// <summary>
/// The fields <paramref name="op"/> declares, read from a request into <paramref name="ctx"/>: each a node id
/// by its name, beside the command's arguments (<c>"panel": "#7"</c>); one left out stays as it was. Null,
/// or why one does not read: not an id, or not a node of its kind in <paramref name="tree"/>.
/// </summary>
public delegate string? ReadContext<TCtx>(OpInfo op, JsonElement request, ModelStore tree, ref TCtx ctx) where TCtx : struct;

/// <summary>The fields <paramref name="op"/> declares, written beside its arguments: only those, and only the ones set.</summary>
public delegate void WriteContext<TCtx>(Utf8JsonWriter json, OpInfo op, in TCtx ctx) where TCtx : struct;

/// <summary>
/// An app's functions over its context, cmdgen's (<c>Context.Ops</c>): what library code that never names a
/// field needs (the model's <see cref="Model{TCtx}.Offers"/>, the test port, the log).
/// </summary>
public sealed class ContextOps<TCtx> where TCtx : struct
{
    public required MissingContext<TCtx> Missing;
    public required ReadContext<TCtx> Read;
    public required WriteContext<TCtx> Write;
}
