using System.Text.Json.Serialization.Metadata;

namespace Bibui.Automation;

/// <summary>How <see cref="TestPort.Open"/> opens a port.</summary>
public sealed class TestPortOptions
{
    /// <summary>
    /// The app's name, in its socket's file name: <c>&lt;name&gt;-&lt;pid&gt;.sock</c>. Letters,
    /// digits, <c>.</c>, <c>_</c> and <c>-</c>; anything else becomes <c>_</c>.
    /// </summary>
    public string Name { get; init; } = "app";

    /// <summary>
    /// The folder the socket is made in, which only the user may enter (made 0700 if
    /// it is not there). By default <c>$XDG_RUNTIME_DIR/bibui</c>, or the temp
    /// folder's <c>bibui</c> where there is no <c>XDG_RUNTIME_DIR</c>.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>
    /// The app's commands, as the type its <c>$cmd</c> names are declared on (its
    /// source-generated <c>JsonTypeInfo</c>): <c>test.ops</c> lists every command it
    /// declares, with its wire name and its input, as the app reads it. Null lists none.
    /// </summary>
    public JsonTypeInfo? Commands { get; init; }

    /// <summary>
    /// What the app's commands are: their table, as Bibui.Commands' cmdgen writes it from
    /// their records (<c>&lt;Set&gt;Ops.All</c>). It gives <c>test.ops</c>' entries their label,
    /// description and whether they only read, and their arguments'
    /// descriptions, so a client knows what a command does and not only what it takes.
    /// Without it, a command's label is its wire name.
    /// </summary>
    public IReadOnlyList<Bibui.Commands.OpInfo>? Ops { get; init; }

    /// <summary>The request buffer: the longest line a client may send.</summary>
    public int RequestBytes { get; init; } = 1 << 20;

    /// <summary>
    /// How long an answer may wait for a client that does not read it before the
    /// client is dropped. The frame waits at most this long, once.
    /// </summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(1);
}
