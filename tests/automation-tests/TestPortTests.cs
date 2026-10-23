using System.Text;
using Bibui.Automation;
using Bibui.Core;
using Bibui.Core.Runtime.Platform;

/// <summary>
/// TestPort's requests: from a line in the request buffer to the TestCmds
/// the frame loop pulls. No socket and no thread: each test publishes a line as the
/// port thread will, and pulls as the loop will.
/// </summary>
static class TestPortTests
{
    static void Eq<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
    }

    static void IsTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    // A port with line published in its request buffer.
    static TestPort Port(string line, int requestBytes = 1 << 16, int maxEvents = 256)
    {
        var port = new TestPort(requestBytes, maxEvents);
        port.Publish(Encoding.UTF8.GetBytes(line));
        return port;
    }

    static string Str(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);

    // The request line holds, which must be a refusal mentioning what.
    static void Refused(string line, string what, int maxEvents = 256)
    {
        var port = Port(line, maxEvents: maxEvents);
        IsTrue(port.TryPullCmd(out var cmd), $"{line}: something to pull");
        Eq(TestCmdKind.None, cmd.Kind, $"{line}: refused");
        IsTrue(cmd.Error!.Contains(what, StringComparison.OrdinalIgnoreCase), $"{line}: the error says '{what}', got '{cmd.Error}'");
        IsTrue(!port.TryPullCmd(out _), $"{line}: nothing after a refusal");
    }

    public static void Register(Action<string, Action> run)
    {
        run("TestPort: with nothing published there is nothing to pull", () =>
        {
            var port = new TestPort();
            IsTrue(!port.TryPullCmd(out _), "nothing");
        });

        run("TestPort: each test.* command is its kind", () =>
        {
            foreach (var kind in new[] { TestCmdKind.Tree, TestCmdKind.State, TestCmdKind.Schema })
            {
                var port = Port($"{{\"$cmd\":\"{Str(TestPort.NameOf(kind))}\"}}");
                IsTrue(port.TryPullCmd(out var cmd), $"{kind}: pulled");
                Eq(kind, cmd.Kind, $"{kind}: kind");
                IsTrue(cmd.Events.IsEmpty && cmd.Path.IsEmpty && cmd.Json.IsEmpty && cmd.Error is null, $"{kind}: no fields");
            }

            var shot = Port("{\"$cmd\":\"test.screenshot\",\"path\":\"shots/a.png\"}");
            IsTrue(shot.TryPullCmd(out var s), "screenshot pulled");
            Eq(TestCmdKind.Screenshot, s.Kind, "screenshot kind");
            Eq("shots/a.png", Str(s.Path), "its path");

            var events = Port("{\"$cmd\":\"test.events\",\"events\":[{\"kind\":\"mouse_move\",\"x\":1,\"y\":2}]}");
            IsTrue(events.TryPullCmd(out var e), "events pulled");
            Eq(TestCmdKind.Events, e.Kind, "events kind");
            Eq(1, e.Events.Length, "one event");
        });

        run("TestPort: an app command is handed over as sent", () =>
        {
            const string line = "{\"$cmd\":\"counter.set\",\"Value\":3}";
            var port = Port(line);
            IsTrue(port.TryPullCmd(out var cmd), "pulled");
            Eq(TestCmdKind.App, cmd.Kind, "an app command");
            Eq(line, Str(cmd.Json), "its bytes, unchanged");
        });

        run("TestPort: $cmd may come anywhere, and is compared unescaped", () =>
        {
            var last = Port("{\"x\":{\"$cmd\":\"nested, not this\"},\"$cmd\":\"test.tree\"}");
            IsTrue(last.TryPullCmd(out var a), "pulled");
            Eq(TestCmdKind.Tree, a.Kind, "$cmd last, and a nested $cmd ignored");

            var escaped = Port("{\"$cmd\":\"test.\\u0074ree\"}");
            IsTrue(escaped.TryPullCmd(out var b), "pulled");
            Eq(TestCmdKind.Tree, b.Kind, "an escaped test name");

            var escapedTest = Port("{\"$cmd\":\"\\u0074est.nope\"}");
            IsTrue(escapedTest.TryPullCmd(out var c), "pulled");
            Eq(TestCmdKind.None, c.Kind, "an escaped unknown test.* name is still a test name");

            var app = Port("{\"$cmd\":\"counter.\\u0073et\",\"Value\":1}");
            IsTrue(app.TryPullCmd(out var d), "pulled");
            Eq(TestCmdKind.App, d.Kind, "an escaped app name");
        });

        run("TestPort: a request that is not a command is refused, with the reason", () =>
        {
            Refused("{\"$cmd\":\"test.nope\"}", "unknown test command");
            Refused("{\"Value\":3}", "names its $cmd");
            Refused("{\"$cmd\":3}", "$cmd is a name");
            Refused("\"test.tree\"", "one JSON object");
            Refused("42", "one JSON object");
            Refused("{\"$cmd\":", "Not JSON");
            Refused("{\"$cmd\":\"test.tree\"} x", "Not JSON");
            Refused("{\"$cmd\":\"test.tree\"} {}", "invalid after a single JSON value");
        });

        run("TestPort: test.events and test.screenshot carry their arguments", () =>
        {
            Refused("{\"$cmd\":\"test.events\"}", "carries its events");
            Refused("{\"$cmd\":\"test.events\",\"events\":{}}", "carries its events");
            Refused("{\"$cmd\":\"test.screenshot\"}", "path");
            Refused("{\"$cmd\":\"test.screenshot\",\"path\":\"\"}", "path");
            Refused("{\"$cmd\":\"test.screenshot\",\"path\":7}", "path");

            var escaped = Port("{\"$cmd\":\"test.screenshot\",\"path\":\"a\\u0020b\\\\c.png\"}");
            IsTrue(escaped.TryPullCmd(out var cmd), "pulled");
            Eq("a b\\c.png", Str(cmd.Path), "an escaped path, unescaped");
        });

        run("TestPort: every event kind is the WindowEvent its factory makes", () =>
        {
            var port = Port("{\"$cmd\":\"test.events\",\"events\":["
                + "{\"kind\":\"mouse_move\",\"x\":1.5,\"y\":2},"
                + "{\"y\":4,\"button\":\"left\",\"x\":3,\"kind\":\"mouse_down\",\"modifiers\":[\"ctrl\",\"shift\"]},"
                + "{\"kind\":\"mouse_up\",\"x\":3,\"y\":4,\"button\":\"middle\"},"
                + "{\"kind\":\"scroll\",\"x\":5,\"y\":6,\"dy\":-1},"
                + "{\"kind\":\"key_down\",\"key\":\"enter\",\"modifiers\":[\"alt\"]},"
                + "{\"kind\":\"key_up\",\"key\":\"Left\"},"
                + "{\"kind\":\"key_char\",\"char\":\"😀\"},"
                + "{\"kind\":\"focus_lost\"}]}");
            IsTrue(port.TryPullCmd(out var cmd), "pulled");
            var ev = cmd.Events;
            Eq(8, ev.Length, "eight events");

            Eq(WindowEventKind.MouseMove, ev[0].Kind, "move");
            Eq((1.5f, 2f), (ev[0].X, ev[0].Y), "at x, y");
            Eq(MouseButton.Unknown, ev[0].Button, "a move presses nothing");

            Eq(WindowEventKind.MouseDown, ev[1].Kind, "down, its fields in any order");
            Eq((3f, 4f, MouseButton.Left), (ev[1].X, ev[1].Y, ev[1].Button), "at x, y, left");
            Eq(KeyModifiers.Ctrl | KeyModifiers.Shift, ev[1].Modifiers, "with ctrl and shift");

            Eq((WindowEventKind.MouseUp, MouseButton.Middle), (ev[2].Kind, ev[2].Button), "up, middle");

            Eq(WindowEventKind.Scroll, ev[3].Kind, "scroll");
            Eq((5f, 6f, 0f, -1f), (ev[3].X, ev[3].Y, ev[3].ScrollX, ev[3].ScrollY), "at x, y; dx defaults to 0");

            Eq((WindowEventKind.KeyDown, Key.Enter, KeyModifiers.Alt), (ev[4].Kind, ev[4].Key, ev[4].Modifiers), "key down, a name in any case");
            Eq((WindowEventKind.KeyUp, Key.Left), (ev[5].Kind, ev[5].Key), "key up");
            Eq((WindowEventKind.KeyChar, new Rune(0x1F600)), (ev[6].Kind, ev[6].CodePoint), "one character, two UTF-16 units");
            Eq(WindowEventKind.FocusLost, ev[7].Kind, "focus lost");
        });

        run("TestPort: an event it cannot read is refused, naming the event", () =>
        {
            static string Events(string events) => "{\"$cmd\":\"test.events\",\"events\":[" + events + "]}";
            Refused(Events("{\"kind\":\"mouse_move\",\"x\":1,\"y\":1},{\"kind\":\"mouse_dwn\"}"), "Event 1: unknown kind");
            Refused(Events("{\"x\":1,\"y\":1}"), "names its kind");
            Refused(Events("{\"kind\":\"mouse_down\",\"x\":1,\"y\":1,\"button\":\"fourth\"}"), "unknown button");
            Refused(Events("{\"kind\":\"mouse_down\",\"x\":1,\"y\":1}"), "carry x, y and button");
            Refused(Events("{\"kind\":\"mouse_move\",\"x\":1}"), "carries x and y");
            Refused(Events("{\"kind\":\"mouse_move\",\"x\":\"1\",\"y\":1}"), "x is a number");
            Refused(Events("{\"kind\":\"key_down\",\"key\":\"Hyper\"}"), "unknown key");
            Refused(Events("{\"kind\":\"key_down\",\"key\":\"A,B\"}"), "unknown key");
            Refused(Events("{\"kind\":\"key_down\",\"key\":\"65\"}"), "unknown key");
            Refused(Events("{\"kind\":\"key_down\",\"key\":\"Unknown\"}"), "unknown key");
            Refused(Events("{\"kind\":\"key_char\",\"char\":\"ab\"}"), "one character");
            Refused(Events("{\"kind\":\"key_down\",\"key\":\"A\",\"modifiers\":[\"hyper\"]}"), "unknown modifier");
            Refused(Events("1"), "an event is a JSON object");
            Refused(Events("{\"kind\":\"focus_lost\"},{\"kind\":\"focus_lost\"},{\"kind\":\"focus_lost\"}"), "at most 2 events", maxEvents: 2);
        });

        run("TestPort: a line longer than the request buffer is refused", () =>
        {
            var port = new TestPort(requestBytes: 16);
            port.Publish(Encoding.UTF8.GetBytes("{\"$cmd\":\"test.state\"}"));
            IsTrue(port.TryPullCmd(out var cmd), "pulled");
            Eq(TestCmdKind.None, cmd.Kind, "refused");
            IsTrue(cmd.Error!.Contains("longer than the request buffer"), $"why: {cmd.Error}");
        });

        run("TestPort: one command per line; a batch is refused", () =>
        {
            Refused("[{\"$cmd\":\"test.tree\"},{\"$cmd\":\"test.state\"}]", "one JSON object");
            Refused("[]", "one JSON object");
        });

        run("TestPort: the buffer holds one request until it is released", () =>
        {
            var port = Port("{\"$cmd\":\"test.state\"}");
            IsTrue(port.TryPullCmd(out _), "the request");
            IsTrue(!port.TryPullCmd(out _), "pulled once, until it is answered");

            var threw = false;
            try { port.Publish("{\"$cmd\":\"test.tree\"}"u8); } catch (InvalidOperationException) { threw = true; }
            IsTrue(threw, "the port thread cannot publish over a request the loop holds");

            port.Release();
            port.Publish("{\"$cmd\":\"test.tree\"}"u8);
            IsTrue(port.TryPullCmd(out var next), "after release, the next request");
            Eq(TestCmdKind.Tree, next.Kind, "the new one");
        });

        run("TestPort: a good request allocates nothing", () =>
        {
            var port = new TestPort();
            var lines = new[]
            {
                Encoding.UTF8.GetBytes("{\"$cmd\":\"test.events\",\"events\":[{\"kind\":\"mouse_down\",\"x\":1,\"y\":2,\"button\":\"left\",\"modifiers\":[\"ctrl\"]},{\"kind\":\"key_down\",\"key\":\"Enter\"},{\"kind\":\"key_char\",\"char\":\"x\"}]}"),
                Encoding.UTF8.GetBytes("{\"Value\":3,\"$cmd\":\"counter.set\"}"),
                Encoding.UTF8.GetBytes("{\"$cmd\":\"test.screenshot\",\"path\":\"a\\u0020b.png\"}"),
                Encoding.UTF8.GetBytes("{\"$cmd\":\"test.\\u0074ree\"}"),
            };
            void PullAll()
            {
                foreach (var line in lines)
                {
                    port.Publish(line);
                    while (port.TryPullCmd(out var cmd)) { if (cmd.Kind == TestCmdKind.None) throw new Exception(cmd.Error); }
                    port.Release();
                }
            }
            PullAll();   // warm: JIT, and anything static
            long before = GC.GetAllocatedBytesForCurrentThread();
            PullAll();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Eq(0L, allocated, "bytes allocated pulling four good requests");
        });
    }
}
