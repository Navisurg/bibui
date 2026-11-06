using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Schema;
using Bibui.Core.Runtime;

namespace Bibui.Automation;

// The port's socket and its one thread. The port thread only reads: it accepts a
// client, reads its lines into the request buffer one at a time, and wakes the
// loop. Only the loop writes to the socket (AnswerCmds).
public sealed partial class TestPort : IDisposable
{
    // Set by Open; null on a port with no socket.
    private BibuiRuntime? _runtime;
    private Socket? _listener;
    private Thread? _thread;
    private volatile bool _stopping;

    // The connected client. The port thread's: set when it accepts, cleared once the
    // client is gone and the loop has let go of the buffer. The loop reads it to
    // answer, and only between a pull and its answer, when it cannot change.
    private Socket? _client;

    /// <summary>
    /// A client is connected: something is driving the app. Read by the loop at any
    /// time; it changes when the port thread accepts a client and when one leaves,
    /// and the port asks for a frame then, so a loop drawing it sees the change.
    /// </summary>
    public bool HasClient => Volatile.Read(ref _client) is not null;

    /// <summary>The socket file clients connect to; null on a port with no socket.</summary>
    public string? SocketPath { get; private set; }

    /// <summary>
    /// Opens a port for <paramref name="runtime"/>: a Unix socket only the user can
    /// reach, at <c>&lt;folder&gt;/&lt;name&gt;-&lt;pid&gt;.sock</c> (<see cref="TestPortOptions"/>),
    /// and the thread that reads it. The loop then calls <see cref="TryPullCmd"/> at
    /// the top of each frame and <see cref="AnswerCmds"/> at its end.
    /// </summary>
    public static TestPort Open(BibuiRuntime runtime, TestPortOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        options ??= new TestPortOptions();
        var port = new TestPort(options.RequestBytes)
        {
            _runtime = runtime,
            _sendTimeoutMs = (int)Math.Clamp(options.SendTimeout.TotalMilliseconds, 1, int.MaxValue),
            _ops = OpsBytes(options),
        };

        //- the folder, the user's alone; then the socket in it
        var folder = options.Directory ?? DefaultFolder();
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(folder);
        else
        {
            System.IO.Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var path = Path.Combine(folder, $"{FileName(options.Name)}-{Environment.ProcessId}.sock");
        File.Delete(path);    // one left by an earlier process with this id
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(1);

        port._listener = listener;
        port.SocketPath = path;
        port._thread = new Thread(port.PortThread) { IsBackground = true, Name = "test-port" };
        port._thread.Start();
        return port;
    }

    /// <summary>Closes the socket, ends the port thread and deletes the socket file.</summary>
    public void Dispose()
    {
        if (_listener is null || _stopping) return;
        _stopping = true;
        _listener.Dispose();                                   // ends the port thread's Accept
        try { Volatile.Read(ref _client)?.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        _bufferFree.Set();                                     // ends a wait for the loop's answer
        _thread?.Join(TimeSpan.FromSeconds(2));
        if (SocketPath is not null) File.Delete(SocketPath);
    }

    // ── The port thread ────────────────────────────────────────────────────

    private void PortThread()
    {
        var received = new byte[64 * 1024];   // bytes read from the client, not yet in a line
        while (!_stopping)
        {
            Socket client;
            try { client = _listener!.Accept(); }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            client.SendTimeout = _sendTimeoutMs;
            Volatile.Write(ref _client, client);
            _runtime!.RequestFrame();

            try { ReadLines(client, received); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }

            //- the client is gone: close it once the loop has let go of the buffer
            _bufferFree.Wait();
            Volatile.Write(ref _client, null);
            client.Dispose();
            _runtime!.RequestFrame();
        }
    }

    // One client's lines, each into the request buffer while it is the port thread's,
    // then published for the loop. Returns when the client is gone.
    private void ReadLines(Socket client, byte[] received)
    {
        int start = 0, end = 0;          // received[start..end]: read, not yet taken into a line
        int length = 0;                  // the line so far, in the request buffer
        bool tooLong = false;
        _bufferFree.Wait();
        while (!_stopping)
        {
            if (start == end)
            {
                int n = client.Receive(received);
                if (n == 0) return;
                (start, end) = (0, n);
            }

            //- take bytes up to the newline into the line
            var rest = received.AsSpan(start, end - start);
            int newline = rest.IndexOf((byte)'\n');
            var take = newline < 0 ? rest : rest[..newline];
            if (!tooLong && length + take.Length > _request.Length) tooLong = true;
            if (!tooLong) { take.CopyTo(_request.AsSpan(length)); length += take.Length; }
            start += take.Length;
            if (newline < 0) continue;
            start++;

            //- a whole line: publish it, wake the loop, wait for its answer
            if (!tooLong && length > 0 && _request[length - 1] == (byte)'\r') length--;
            if (!tooLong && length == 0) continue;       // an empty line asks nothing
            PublishLength(tooLong ? Oversized : length);
            _runtime!.RequestFrame();
            _bufferFree.Wait();
            (length, tooLong) = (0, false);
        }
    }

    // ── Opening ────────────────────────────────────────────────────────────

    private static string DefaultFolder()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return Path.Combine(string.IsNullOrEmpty(runtimeDir) ? Path.GetTempPath() : runtimeDir, "bibui");
    }

    private static string FileName(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!(char.IsAsciiLetterOrDigit(chars[i]) || chars[i] is '.' or '_' or '-')) chars[i] = '_';
        return chars.Length == 0 ? "app" : new string(chars);
    }

    // test.ops' answer, made once: an entry per command the app's JSON declares, its wire name
    // and input from its own JSON type, the one the app reads it with, so a client that fills
    // in what the input asks for sends what the app accepts; and from its entry in the table,
    // when there is one, its label, description, contracts and its arguments' descriptions.
    private static byte[] OpsBytes(TestPortOptions options)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Relaxed))
        {
            writer.WriteStartArray();
            Commands(writer, options.Commands, [.. options.Ops ?? [], .. Bibui.Commands.BuiltinCmd.Ops]);   // the library's commands described too, when the app's JSON names them (Bibui.Commands.CmdJson)
            writer.WriteEndArray();
        }
        return buffer.WrittenSpan.ToArray();
    }

    // An entry per command commands declares, described by its row in ops.
    private static void Commands(Utf8JsonWriter writer, System.Text.Json.Serialization.Metadata.JsonTypeInfo? commands, IReadOnlyList<Bibui.Commands.OpInfo>? ops)
    {
        foreach (var command in commands?.PolymorphismOptions?.DerivedTypes ?? [])
        {
            var wire = command.TypeDiscriminator?.ToString() ?? command.DerivedType.Name;
            Bibui.Commands.OpInfo? op = null;
            foreach (var o in ops ?? []) if (o.Type == command.DerivedType) op = o;
            writer.WriteStartObject();
            writer.WriteString("kind", "command");
            writer.WriteString("wire", wire);
            writer.WriteString("label", op?.Label ?? wire);
            writer.WriteString("description", op?.Description ?? "");
            writer.WriteBoolean("readOnly", op is not null && (op.Flags & Bibui.Commands.OpFlags.ReadOnly) != 0);
            writer.WritePropertyName("input");
            Input(commands!.Options.GetTypeInfo(command.DerivedType), op).WriteTo(writer);
            writer.WriteEndObject();
        }
    }

    // A command's input: its arguments, as its JSON type has them (types, what is required,
    // whether anything else is taken), each with its description from the table.
    private static System.Text.Json.Nodes.JsonObject Input(System.Text.Json.Serialization.Metadata.JsonTypeInfo type, Bibui.Commands.OpInfo? op)
    {
        var input = type.GetJsonSchemaAsNode(new JsonSchemaExporterOptions
        {
            TransformSchemaNode = (context, node) =>
            {
                if (context.PropertyInfo is not { } argument) return node;
                // A type that writes its own JSON has the schema true (anything): an object here,
                // so it can carry a description. A handle is a string, "#7", and says which kind
                // of node it names.
                var schema = node as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                var arg = op?.Arg(argument.Name);
                bool handle = context.TypeInfo.Type == typeof(Bibui.Commands.NodeId) || arg?.Kind is not null;   // a typed handle says its kind
                if (handle)
                {
                    schema["type"] = "string";
                    schema["pattern"] = "^#?[0-9]+$";
                }
                bool expression = context.TypeInfo.Type == typeof(Bibui.Commands.Expr);   // its JSON is its text (ExprJson)
                if (context.TypeInfo.Type == typeof(Bibui.Commands.ViewRuleType)) schema["type"] = "string";   // its JSON is its name (ViewRuleTypeJson)
                if (expression) schema["type"] = "string";
                if (arg is { Description.Length: > 0 }) schema["description"] = arg.Description;
                else if (handle)
                    schema["description"] = $"A handle to a {arg?.Kind ?? "node"}, as model.get answers it: '#' and its id. It names nothing once that node is gone.";
                else if (expression)
                    schema["description"] = "An expression, its text: 2, \"text\", $7.title, board(it.case).";
                if (arg?.Kind is { } kind) schema["nodeKind"] = kind;
                return schema;
            },
        }).AsObject();
        input["type"] = "object";   // the exporter allows null; a command's arguments never are
        input["properties"] ??= new System.Text.Json.Nodes.JsonObject();   // a command without arguments says so, not nothing
        return input;
    }
}
