using System.Buffers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bibui.Core;
using Bibui.Core.Runtime;

namespace Bibui.Automation;

// The loop's end of a request: its answer, written to the socket at the end of the
// frame, once what it asked for has settled. Only the loop writes to the socket.
public sealed partial class TestPort
{
    // Set by Open.
    private int _sendTimeoutMs = 1000;
    private byte[] _schema = [];

    // The loop's: a reply is written through these, allocated once.
    private SocketWriter? _out;
    private Utf8JsonWriter? _json;

    /// <summary>
    /// Refuses the request in hand: an app command the app cannot read, or a
    /// screenshot it does not take or could not write. Answered as
    /// <c>{"ok":false,"error":...}</c> at the end of the frame.
    /// </summary>
    public void RefuseCmd(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!_pulled) return;
        (_heldKind, _heldError) = (TestCmdKind.None, error);
    }

    /// <summary>
    /// Answers the request in hand, at the end of the frame, after the build. Events
    /// and app commands are answered once <paramref name="settled"/>: when nothing
    /// they set off is still pending in the app; until then the port asks for another
    /// frame and answers in a later one. <c>test.state</c> answers that the app
    /// reports no state; an app that has one passes it to the other overload.
    /// </summary>
    public void AnswerCmds(bool settled = true) => AnswerCmds<object?>(settled, null, null);

    /// <summary>
    /// Answers the request in hand, as <see cref="AnswerCmds(bool)"/>; <c>test.state</c>
    /// answers <paramref name="state"/>, written with <paramref name="stateType"/> (the
    /// app's source-generated JSON, so nothing is reflected).
    /// </summary>
    public void AnswerCmds<TState>(bool settled, TState state, JsonTypeInfo<TState>? stateType)
    {
        if (!_pulled) return;
        if (_heldKind is TestCmdKind.Events or TestCmdKind.App && !settled)
        {
            _runtime?.RequestFrame();
            return;
        }

        var client = Volatile.Read(ref _client);
        if (client is not null) Write(client, state, stateType);
        Release();
    }

    // ── Writing an answer ──────────────────────────────────────────────────

    private void Write<TState>(Socket client, TState state, JsonTypeInfo<TState>? stateType)
    {
        _out ??= new SocketWriter();
        _out.Start(client);
        if (_json is null) _json = new Utf8JsonWriter(_out);
        else _json.Reset(_out);
        var json = _json;

        try
        {
            json.WriteStartObject();
            switch (_heldKind)
            {
                case TestCmdKind.None:
                    Error(json, _heldError ?? "Refused.");
                    break;
                case TestCmdKind.Events:
                case TestCmdKind.App:
                case TestCmdKind.Screenshot:          // written by the app, or refused by it
                    json.WriteBoolean("ok", true);
                    break;
                case TestCmdKind.Tree:
                    json.WriteBoolean("ok", true);
                    json.WritePropertyName("result");
                    WriteTree(json, _runtime?.LastTree);
                    break;
                case TestCmdKind.State:
                    if (stateType is null) { Error(json, "This app reports no state."); break; }
                    json.WriteBoolean("ok", true);
                    json.WritePropertyName("result");
                    JsonSerializer.Serialize(json, state, stateType);
                    break;
                case TestCmdKind.Schema:
                    json.WriteBoolean("ok", true);
                    json.WritePropertyName("result");
                    json.WriteRawValue(_schema, skipInputValidation: true);
                    break;
            }
            json.WriteEndObject();
            json.Flush();
            _out.Finish();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
        {
            // The client is gone, or would not read before the send timeout: drop it.
            // The port thread's next read fails, and it closes the connection.
            try { client.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        }

        static void Error(Utf8JsonWriter json, string error)
        {
            json.WriteBoolean("ok", false);
            json.WriteString("error", error);
        }
    }

    // test.tree: the tree the last frame laid out, walked as it is (the runtime keeps
    // it; nothing is copied), each node saying what it is, where it was laid out and
    // what it holds, leaving out what is empty or false:
    // {"kind":"box","id":"add","x":10,"y":40,"w":80,"h":24,"clickable":true,"children":[...]}
    private static void WriteTree(Utf8JsonWriter json, UiNode? node)
    {
        if (node is null) { json.WriteNullValue(); return; }
        json.WriteStartObject();
        json.WriteString("kind", node switch { Box => "box", Text => "text", _ => "node" });
        if (node.Id is { } id) json.WriteString("id", id);
        json.WriteNumber("x", node.Rect.X);
        json.WriteNumber("y", node.Rect.Y);
        json.WriteNumber("w", node.Extent.X);
        json.WriteNumber("h", node.Extent.Y);
        if (node is Text text) json.WriteString("text", text.Content);
        if (node is Box box)
        {
            if ((box.Claims & InteractionKinds.Press) != 0) json.WriteBoolean("clickable", true);
            if ((box.Claims & InteractionKinds.Disabled) != 0) json.WriteBoolean("disabled", true);
        }
        if (node.FirstChild is not null)
        {
            json.WriteStartArray("children");
            for (var child = node.FirstChild; child is not null; child = child.Next == child ? null : child.Next)
                WriteTree(json, child);
            json.WriteEndArray();
        }
        json.WriteEndObject();
    }

    // A reply's bytes, through one buffer allocated once, sent to the socket each time
    // it fills and at the end, with the line's newline.
    private sealed class SocketWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private byte[]? _wide;                     // a span wider than the buffer was asked for: rare
        private int _used;
        private Socket? _socket;

        public void Start(Socket socket) => (_socket, _used, _wide) = (socket, 0, null);

        public void Finish()
        {
            if (_used == _buffer.Length) Send();
            _buffer[_used++] = (byte)'\n';
            Send();
        }

        public void Advance(int count)
        {
            if (_wide is not null)
            {
                // What was written past the buffer goes out at once.
                Send();
                SendAll(_wide.AsSpan(0, count));
                _wide = null;
                return;
            }
            _used += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => Room(sizeHint) ?? _buffer.AsMemory(_used);
        public Span<byte> GetSpan(int sizeHint = 0) => Room(sizeHint) is { } wide ? wide.Span : _buffer.AsSpan(_used);

        // Room for sizeHint bytes: the rest of the buffer, emptied first if it is too
        // full; or, for more than the whole buffer, a wider array of its own.
        private Memory<byte>? Room(int sizeHint)
        {
            sizeHint = Math.Max(sizeHint, 1);
            if (_buffer.Length - _used >= sizeHint) return null;
            Send();
            if (sizeHint <= _buffer.Length) return null;
            _wide = new byte[sizeHint];
            return _wide;
        }

        private void Send()
        {
            SendAll(_buffer.AsSpan(0, _used));
            _used = 0;
        }

        private void SendAll(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty) bytes = bytes[_socket!.Send(bytes)..];
        }
    }
}
