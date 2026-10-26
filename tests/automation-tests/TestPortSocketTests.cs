using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bibui.Automation;
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.Theming;
using Bibui.Raster.Skia;

/// <summary>
/// TestPort's socket, port thread and answers: a headless app (a runtime, its
/// port, and its frame loop on a thread of its own) driven by a real client over
/// the Unix socket, the way a script or an agent drives one.
/// </summary>
static class TestPortSocketTests
{
    static void Eq<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
    }

    static void IsTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    public static void Register(Action<string, Action> run)
    {
        run("TestPort.Open: the socket is the user's alone, named for the app and its process", () =>
        {
            using var app = new CounterApp();
            var path = app.Port.SocketPath!;
            Eq($"counter-{Environment.ProcessId}.sock", Path.GetFileName(path), "the file name");
            if (OperatingSystem.IsWindows()) return;
            Eq(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path), "the socket's mode");
            Eq(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
               File.GetUnixFileMode(Path.GetDirectoryName(path)!), "its folder's mode");
        });

        run("TestPort: every kind of answer, through the socket", () =>
        {
            using var app = new CounterApp();
            using var client = app.Connect();

            var tree = client.Ask("{\"$cmd\":\"test.tree\"}");
            IsTrue(Ok(tree) && tree["result"]!["kind"]!.GetValue<string>() == "box", $"the tree: {tree.ToJsonString()[..Math.Min(200, tree.ToJsonString().Length)]}");
            var add = Find(tree["result"]!, "add");
            IsTrue(add is not null && Claims(add) == "LeftPress", "the tree holds the button, claiming a press");

            var schema = client.Ask("{\"$cmd\":\"test.schema\"}").ToJsonString();
            IsTrue(schema.Contains("\"test.events\"") && schema.Contains("\"counter.set\""), "the schema names the test commands and the app's");

            var state = client.Ask("{\"$cmd\":\"test.state\"}");
            Eq(0, state["result"]!["Count"]!.GetValue<int>(), "the state");
            IsTrue(state["result"]!["Blob"] is null, "a [JsonIgnore] field is left out");

            IsTrue(Ok(client.Ask("{\"$cmd\":\"counter.set\",\"Value\":5}")), "an app command");
            Eq(5, client.Ask("{\"$cmd\":\"test.state\"}")["result"]!["Count"]!.GetValue<int>(), "and its effect");

            var unknown = client.Ask("{\"$cmd\":\"test.nope\"}");
            IsTrue(!Ok(unknown) && unknown["error"]!.GetValue<string>().Contains("test.nope"), $"a refusal naming the command: {unknown.ToJsonString()}");
            var notTheApps = client.Ask("{\"$cmd\":\"counter.nope\"}");
            IsTrue(!Ok(notTheApps) && notTheApps["error"]!.GetValue<string>().Contains("counter.nope"), $"an app command the app cannot read: {notTheApps.ToJsonString()}");

            var png = Path.Combine(app.Folder, "frame.png");
            IsTrue(Ok(client.Ask("{\"$cmd\":\"test.screenshot\",\"path\":" + JsonSerializer.Serialize(png) + "}")), "a screenshot");
            IsTrue(File.ReadAllBytes(png).AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']), "written as a PNG");
        });

        run("TestPort: a box's claims are written one name per bit, and answers write ' and é as themselves", () =>
        {
            using var app = new CounterApp(list: true);
            using var client = app.Connect();
            var list = Find(client.Ask("{\"$cmd\":\"test.tree\"}")["result"]!, "list")!;
            Eq("ViewScrollY ClampY Clip", Claims(list), "the list's claims, not a composite name");
            var refusal = client.ReadLineAfter("{\"$cmd\":\"test.nope'é\"}");
            IsTrue(refusal.Contains("test.nope'é"), $"unescaped: {refusal}");
        });

        run("TestPort: an app with no renderer or no state says so", () =>
        {
            using var app = new CounterApp(draw: false, state: false);
            using var client = app.Connect();
            var shot = client.Ask("{\"$cmd\":\"test.screenshot\",\"path\":\"x.png\"}");
            IsTrue(!Ok(shot) && shot["error"]!.GetValue<string>().Contains("no screenshots"), "no screenshot");
            var state = client.Ask("{\"$cmd\":\"test.state\"}");
            IsTrue(!Ok(state) && state["error"]!.GetValue<string>().Contains("no state"), "no state");
        });

        run("TestPort: a click is answered once the command it set off has run", () =>
        {
            using var app = new CounterApp();
            using var client = app.Connect();
            var add = Find(client.Ask("{\"$cmd\":\"test.tree\"}")["result"]!, "add")!;
            float x = add["x"]!.GetValue<float>() + add["w"]!.GetValue<float>() / 2;
            float y = add["y"]!.GetValue<float>() + add["h"]!.GetValue<float>() / 2;
            string Events(string kind) => $"{{\"$cmd\":\"test.events\",\"events\":[{{\"kind\":\"{kind}\",\"x\":{x},\"y\":{y},\"button\":\"left\"}}]}}";

            IsTrue(Ok(client.Ask(Events("mouse_move"))), "move");
            IsTrue(Ok(client.Ask(Events("mouse_down"))), "press");
            IsTrue(Ok(client.Ask(Events("mouse_up"))), "release");
            Eq(1, client.Ask("{\"$cmd\":\"test.state\"}")["result"]!["Count"]!.GetValue<int>(), "the release's answer came after the increment ran");
        });

        run("TestPort: lines sent without waiting are answered in order", () =>
        {
            using var app = new CounterApp();
            using var client = app.Connect();
            client.Send("{\"$cmd\":\"counter.set\",\"Value\":1}\n{\"$cmd\":\"counter.increment\"}\r\n\n{\"$cmd\":\"test.state\"}\n");
            IsTrue(Ok(client.Read()), "the first");
            IsTrue(Ok(client.Read()), "the second (an empty line between asks nothing)");
            Eq(2, client.Read()["result"]!["Count"]!.GetValue<int>(), "the third saw both");
        });

        run("TestPort: a line longer than the request buffer is refused, and the next one works", () =>
        {
            using var app = new CounterApp(requestBytes: 64);
            using var client = app.Connect();
            var refused = client.Ask("{\"$cmd\":\"counter.set\",\"Value\":1,\"pad\":\"" + new string('x', 200) + "\"}");
            IsTrue(!Ok(refused) && refused["error"]!.GetValue<string>().Contains("longer than the request buffer"), "refused");
            IsTrue(Ok(client.Ask("{\"$cmd\":\"test.state\"}")), "the next request");
        });

        run("TestPort: a client that stops reading is dropped after the send timeout, and the loop goes on", () =>
        {
            using var app = new CounterApp(rows: 40000, sendTimeout: TimeSpan.FromMilliseconds(200));
            var stalled = app.Connect();
            stalled.Send("{\"$cmd\":\"test.tree\"}\n");          // megabytes, never read
            var watch = Stopwatch.StartNew();
            using var next = app.Connect();                     // waits for the port to drop the first
            Eq(0, next.Ask("{\"$cmd\":\"test.state\"}")["result"]!["Count"]!.GetValue<int>(), "the next client is served");
            IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5), $"soon after the timeout: {watch.Elapsed.TotalMilliseconds:F0} ms");
            stalled.Dispose();
        });

        run("TestPort: one client at a time: a second waits, and one leaving mid-request does not stop the next", () =>
        {
            using var app = new CounterApp();
            using (var leaving = app.Connect()) leaving.Send("{\"$cmd\":\"test.tree\"}\n");
            var first = app.Connect();
            IsTrue(Ok(first.Ask("{\"$cmd\":\"test.state\"}")), "the first is served");
            using var second = app.Connect();
            second.Send("{\"$cmd\":\"test.state\"}\n");
            IsTrue(!second.TryRead(TimeSpan.FromMilliseconds(300), out _), "the second waits");
            first.Dispose();
            IsTrue(second.TryRead(TimeSpan.FromSeconds(5), out var answer) && Ok(answer!), "then is served");
        });

        run("TestPort.Dispose: the port thread ends and the socket file is gone", () =>
        {
            var app = new CounterApp();
            var path = app.Port.SocketPath!;
            app.Dispose();
            IsTrue(!File.Exists(path), "the socket file is gone");
            var refused = false;
            try { using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); s.Connect(new UnixDomainSocketEndPoint(path)); }
            catch (SocketException) { refused = true; }
            IsTrue(refused, "nobody listens");
        });

        run("TestPort.AnswerCmds: a big tree is one line, sent in 64 KB pieces, and how long answers take", () =>
        {
            using var app = new CounterApp(rows: 8000);
            using var client = app.Connect();
            for (int i = 0; i < 3; i++) { app.Time(client, "{\"$cmd\":\"test.state\"}", out _); app.Time(client, "{\"$cmd\":\"test.tree\"}", out _); }   // warm
            var small = app.Time(client, "{\"$cmd\":\"test.state\"}", out _);
            var big = app.Time(client, "{\"$cmd\":\"test.tree\"}", out var answer);
            var tree = answer.ToJsonString().Length;
            IsTrue(tree > 4 * 64 * 1024 && Ok(answer), $"one well-formed answer, larger than four pieces: {tree} bytes");
            Console.WriteLine($"         measured: a small answer {small.TotalMilliseconds:F3} ms; a {tree / 1024} KB tree {big.TotalMilliseconds:F2} ms");
            IsTrue(small < TimeSpan.FromMilliseconds(5), "a small answer is far inside a frame");
            IsTrue(big < TimeSpan.FromMilliseconds(100), "a megabyte tree takes a few frames' time at most");
        });
    }

    static bool Ok(JsonNode answer) => answer["ok"]?.GetValue<bool>() == true;

    // A tree node's claims, space-separated, or "" for none.
    static string Claims(JsonNode node) =>
        node["claims"] is JsonArray claims ? string.Join(' ', claims.Select(c => c!.GetValue<string>())) : "";

    // The node with this id in a tree answer, or null.
    static JsonNode? Find(JsonNode node, string id)
    {
        if (node["id"]?.GetValue<string>() == id) return node;
        if (node["children"] is JsonArray children)
            foreach (var child in children)
                if (child is not null && Find(child, id) is { } found) return found;
        return null;
    }

    // ── A headless app ─────────────────────────────────────────────────────

    /// <summary>
    /// A counter app with no window: a runtime, its port, and its frame loop on a
    /// thread of its own, as an app's loop runs a frame whenever one is owed.
    /// </summary>
    internal sealed class CounterApp : IDisposable
    {
        public readonly string Folder = Path.Combine(Path.GetTempPath(), "bibui-at-" + Guid.NewGuid().ToString("N")[..8]);
        public readonly TestPort Port;
        // The loop's: how long the last AnswerCmds that answered a request took, and
        // how many it has answered, written after the time.
        public TimeSpan LastAnswer;
        public int Answered;

        // How long the frame spent answering line, once the loop has noted it.
        public TimeSpan Time(Client client, string line, out JsonNode answer)
        {
            int before = Volatile.Read(ref Answered);
            answer = client.Ask(line);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Volatile.Read(ref Answered) == before && DateTime.UtcNow < deadline) Thread.Sleep(1);
            return LastAnswer;
        }

        private readonly BibuiRuntime _runtime = BibuiRuntime.Create();
        private readonly CounterState _state = new();
        private readonly List<CounterCmd> _pending = [];
        private readonly List<WindowEvent> _events = [];
        private readonly SkiaCpuRenderer? _cpu;    // what screenshots are drawn with; none, and they are refused
        private readonly int _rows;
        private readonly bool _list;
        private readonly bool _reportsState;
        private readonly Thread _loop;
        private volatile bool _stopping;

        // list: a scrolling list of buttons below the counter, 100 px tall and far
        // taller inside: row-i sets the count to 100 + i.
        public CounterApp(bool draw = true, bool state = true, int requestBytes = 1 << 16, int rows = 0, TimeSpan? sendTimeout = null, bool list = false)
        {
            (_rows, _list, _reportsState, _cpu) = (rows, list, state, draw ? new SkiaCpuRenderer() : null);
            _runtime.SetTheme(ThemeRegistry.Load("bibui-dark"));
            Port = TestPort.Open(_runtime, new TestPortOptions
            {
                Name = "counter",
                Directory = Folder,
                Commands = CounterJson.Default.CounterCmd,
                RequestBytes = requestBytes,
                SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(1),
            });
            _loop = new Thread(Loop) { IsBackground = true, Name = "counter app loop" };
            _loop.Start();
        }

        public Client Connect() => new(Port.SocketPath!);

        private void Loop()
        {
            var watch = new Stopwatch();
            while (!_stopping)
            {
                if (_runtime.FramesRequested == 0) { Thread.Sleep(1); continue; }
                _runtime.ConsumeFrame();

                //- top of frame: the request, if one is published
                _events.Clear();
                bool pulled = false;
                while (Port.TryPullCmd(out var cmd))
                {
                    pulled = true;
                    switch (cmd.Kind)
                    {
                        case TestCmdKind.Events: _events.AddRange(cmd.Events); break;
                        case TestCmdKind.Screenshot:
                            // The last frame's draw list is still the runtime's: the frame on screen.
                            if (_cpu is null) Port.RefuseCmd("This app takes no screenshots.");
                            else File.WriteAllBytes(cmd.Path!, FramePixels.Png(_runtime, _cpu.Draw, cmd.Region));
                            break;
                        case TestCmdKind.App:
                            try { _pending.Add(JsonSerializer.Deserialize(cmd.Json, CounterJson.Default.CounterCmd)!); }
                            catch (JsonException e) { Port.RefuseCmd($"Not a command of this app: {e.Message}"); }
                            break;
                    }
                }

                //- the app's commands, then the build
                foreach (var c in _pending)
                    _state.Count = c switch { CounterCmd.Increment => _state.Count + 1, CounterCmd.Set s => s.Value, _ => _state.Count };
                _pending.Clear();
                _runtime.BeginBuild(new WindowFrame(400, 300, 1f, 60f), _events);
                _runtime.Text($"Count {_state.Count}");
                if (_runtime.Button("add", "Add one").Clicked) _pending.Add(new CounterCmd.Increment());
                for (int i = 0; i < _rows; i++) _runtime.Text("A row of text to make the tree large");
                if (_list)
                {
                    var list = _runtime.BoxFromId("list", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip);
                    list.ChildLayoutAxis = LayoutAxis.Y;
                    _runtime.BeginParent(list);
                    for (int i = 0; i < 30; i++)
                        if (_runtime.Button($"row-{i}", $"Row {i}").Clicked) _pending.Add(new CounterCmd.Set(100 + i));
                    _runtime.EndParent(list);
                    _runtime.InteractionsFor(list);
                    (list.SizeX, list.SizeY) = (SemanticSize.Pixels(200), SemanticSize.Pixels(100));
                    list.AllowOverflowY = true;
                }
                _runtime.EndBuild();
                if (_pending.Count > 0) _runtime.RequestFrame();

                //- end of frame: answer, once settled
                watch.Restart();
                if (_reportsState) Port.AnswerCmds(_pending.Count == 0, _state, CounterJson.Default.CounterState);
                else Port.AnswerCmds(_pending.Count == 0);
                if (pulled && _pending.Count == 0)
                {
                    LastAnswer = watch.Elapsed;
                    Volatile.Write(ref Answered, Answered + 1);
                }
            }
        }

        public void Dispose()
        {
            Port.Dispose();
            _stopping = true;
            _loop.Join(TimeSpan.FromSeconds(2));
            try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A client of the port: lines out, lines in, over the Unix socket.</summary>
    internal sealed class Client : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly StreamReader _reader;

        public Client(string path)
        {
            _socket.Connect(new UnixDomainSocketEndPoint(path));
            _reader = new StreamReader(new NetworkStream(_socket), Encoding.UTF8);
        }

        public void Send(string text) => _socket.Send(Encoding.UTF8.GetBytes(text));
        public string ReadLine() => _reader.ReadLine() ?? throw new Exception("The port closed the connection.");
        public JsonNode Read() => JsonNode.Parse(ReadLine())!;
        public JsonNode Ask(string line) { Send(line + "\n"); return Read(); }
        public string ReadLineAfter(string line) { Send(line + "\n"); return ReadLine(); }

        // An answer within timeout, if one comes: nothing has been read ahead, so the
        // socket having bytes is the answer having come.
        public bool TryRead(TimeSpan timeout, out JsonNode? answer)
        {
            answer = _socket.Poll(timeout, SelectMode.SelectRead) ? Read() : null;
            return answer is not null;
        }

        public void Dispose() => _socket.Dispose();
    }
}

// The counter app's state and commands, as the app's own source-generated JSON.
sealed class CounterState
{
    public int Count;
    [JsonIgnore] public byte[] Blob = new byte[1024];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$cmd")]
[JsonDerivedType(typeof(Increment), "counter.increment")]
[JsonDerivedType(typeof(Set), "counter.set")]
abstract record CounterCmd
{
    public sealed record Increment : CounterCmd;
    public sealed record Set(int Value) : CounterCmd;
}

[JsonSourceGenerationOptions(IncludeFields = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(CounterState))]
[JsonSerializable(typeof(CounterCmd))]
partial class CounterJson : JsonSerializerContext;
