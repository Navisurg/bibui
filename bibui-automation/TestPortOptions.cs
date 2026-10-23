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
    /// The app's commands, as the type its <c>$cmd</c> names are declared on, for
    /// <c>test.schema</c>: its JSON schema is given beside the <c>test.*</c> commands'.
    /// Null gives the <c>test.*</c> commands only.
    /// </summary>
    public JsonTypeInfo? Commands { get; init; }

    /// <summary>The request buffer: the longest line a client may send.</summary>
    public int RequestBytes { get; init; } = 1 << 20;

    /// <summary>
    /// How long an answer may wait for a client that does not read it before the
    /// client is dropped. The frame waits at most this long, once.
    /// </summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(1);
}
