using System.Text;
using Bibui.Automation;
using Bibui.Core;
using Bibui.Core.Runtime.Platform;

/// <summary>
/// TestPort's requests: from a line in the request buffer to the TestCmd the frame
/// loop pulls. No socket and no thread: each test publishes a line as the port
/// thread does, and pulls as the loop does.
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
    static TestPort Port(string line, int requestBytes = 1 << 16)
    {
        var port = new TestPort(requestBytes);
        port.Publish(Encoding.UTF8.GetBytes(line));
        return port;
    }

    // The request line holds, which must be refused with a reason mentioning what.
    static void Refused(string line, string what)
    {
        var port = Port(line);
        IsTrue(port.TryPullCmd(out var cmd), $"{line}: something to pull");
        Eq(TestCmdKind.None, cmd.Kind, $"{line}: refused");
        IsTrue(cmd.Error!.Contains(what, StringComparison.OrdinalIgnoreCase), $"{line}: the reason says '{what}', got '{cmd.Error}'");
    }

    static string Events(string events) => "{\"$cmd\":\"test.events\",\"events\":[" + events + "]}";

    public static void Register(Action<string, Action> run)
    {
        run("TestPort: each test.* command is its kind, and an app command is handed over as sent", () =>
        {
            foreach (var (name, kind) in new[] { ("test.tree", TestCmdKind.Tree), ("test.state", TestCmdKind.State), ("test.schema", TestCmdKind.Schema) })
            {
                IsTrue(Port($"{{\"$cmd\":\"{name}\"}}").TryPullCmd(out var cmd), $"{name}: pulled");
                Eq(kind, cmd.Kind, name);
            }

            IsTrue(Port("{\"$cmd\":\"test.screenshot\",\"path\":\"shots/a b.png\"}").TryPullCmd(out var shot), "screenshot pulled");
            Eq((TestCmdKind.Screenshot, "shots/a b.png"), (shot.Kind, shot.Path), "a screenshot and its path");

            IsTrue(Port(Events("{\"kind\":\"focus_lost\"}")).TryPullCmd(out var events), "events pulled");
            Eq((TestCmdKind.Events, 1), (events.Kind, events.Events.Length), "events, and how many");

            const string line = "{\"$cmd\":\"counter.set\",\"Value\":3}";
            IsTrue(Port(line).TryPullCmd(out var app), "app command pulled");
            Eq((TestCmdKind.App, line), (app.Kind, Encoding.UTF8.GetString(app.Json)), "an app command, its bytes unchanged");
        });

        run("TestPort: $cmd may come anywhere, and is compared unescaped", () =>
        {
            IsTrue(Port("{\"x\":{\"$cmd\":\"nested, not this\"},\"$cmd\":\"test.tree\"}").TryPullCmd(out var last), "pulled");
            Eq(TestCmdKind.Tree, last.Kind, "$cmd last, a nested $cmd ignored");
            IsTrue(Port("{\"$cmd\":\"\\u0074est.tree\"}").TryPullCmd(out var escaped), "pulled");
            Eq(TestCmdKind.Tree, escaped.Kind, "an escaped test name");
            IsTrue(Port("{\"$cmd\":\"counter.\\u0073et\",\"Value\":1}").TryPullCmd(out var app), "pulled");
            Eq(TestCmdKind.App, app.Kind, "an escaped app name");
        });

        run("TestPort: a request it cannot read is refused, with the reason", () =>
        {
            Refused("{\"$cmd\":\"test.nope\"}", "test.nope");
            Refused("{\"Value\":3}", "names its $cmd");
            Refused("{\"$cmd\":3}", "$cmd is a name");
            Refused("42", "one JSON object");
            Refused("[{\"$cmd\":\"test.tree\"}]", "one JSON object");                 // a batch: one command per line
            Refused("{\"$cmd\":", "Not a request");
            Refused("{\"$cmd\":\"test.tree\"} x", "Not a request");
            Refused("{\"$cmd\":\"test.events\"}", "events");
            Refused("{\"$cmd\":\"test.screenshot\"}", "path");
            Refused("{\"$cmd\":\"test.screenshot\",\"path\":\"\"}", "path");
        });

        run("TestPort: every event kind is the WindowEvent its factory makes", () =>
        {
            IsTrue(Port(Events(
                "{\"kind\":\"mouse_move\",\"x\":1.5,\"y\":2},"
                + "{\"y\":4,\"button\":\"left\",\"x\":3,\"kind\":\"mouse_down\",\"modifiers\":[\"ctrl\",\"shift\"]},"
                + "{\"kind\":\"mouse_up\",\"x\":3,\"y\":4,\"button\":\"middle\"},"
                + "{\"kind\":\"scroll\",\"x\":5,\"y\":6,\"dy\":-1},"
                + "{\"kind\":\"key_down\",\"key\":\"enter\",\"modifiers\":[\"alt\"]},"
                + "{\"kind\":\"key_up\",\"key\":\"Left\"},"
                + "{\"kind\":\"key_char\",\"char\":\"😀\"},"
                + "{\"kind\":\"focus_lost\"}")).TryPullCmd(out var cmd), "pulled");
            var ev = cmd.Events;
            Eq(8, ev.Length, "eight events");
            Eq((WindowEventKind.MouseMove, 1.5f, 2f, MouseButton.Unknown), (ev[0].Kind, ev[0].X, ev[0].Y, ev[0].Button), "move");
            Eq((WindowEventKind.MouseDown, 3f, 4f, MouseButton.Left), (ev[1].Kind, ev[1].X, ev[1].Y, ev[1].Button), "down, its fields in any order");
            Eq(KeyModifiers.Ctrl | KeyModifiers.Shift, ev[1].Modifiers, "with ctrl and shift");
            Eq((WindowEventKind.MouseUp, MouseButton.Middle), (ev[2].Kind, ev[2].Button), "up, middle");
            Eq((WindowEventKind.Scroll, 0f, -1f), (ev[3].Kind, ev[3].ScrollX, ev[3].ScrollY), "scroll; dx defaults to 0");
            Eq((WindowEventKind.KeyDown, Key.Enter, KeyModifiers.Alt), (ev[4].Kind, ev[4].Key, ev[4].Modifiers), "key down, a name in any case");
            Eq((WindowEventKind.KeyUp, Key.Left), (ev[5].Kind, ev[5].Key), "key up");
            Eq((WindowEventKind.KeyChar, new Rune(0x1F600)), (ev[6].Kind, ev[6].CodePoint), "one character, two UTF-16 units");
            Eq(WindowEventKind.FocusLost, ev[7].Kind, "focus lost");
        });

        run("TestPort: an event it cannot read is refused, naming the event", () =>
        {
            Refused(Events("{\"kind\":\"focus_lost\"},{\"kind\":\"mouse_dwn\"}"), "Event 1: unknown kind");
            Refused(Events("{\"kind\":\"mouse_down\",\"x\":1,\"y\":1,\"button\":\"fourth\"}"), "button");
            Refused(Events("{\"kind\":\"mouse_move\",\"x\":1}"), "carries x and y");
            Refused(Events("{\"kind\":\"key_down\",\"key\":\"A,B\"}"), "unknown key");
            Refused(Events("{\"kind\":\"key_down\",\"key\":\"65\"}"), "unknown key");
            Refused(Events("{\"kind\":\"key_char\",\"char\":\"ab\"}"), "one character");
            Refused(Events("{\"kind\":\"key_down\",\"key\":\"A\",\"modifiers\":[\"hyper\"]}"), "unknown modifier");
        });

        run("TestPort: the buffer holds one request until it is released", () =>
        {
            var port = new TestPort();
            IsTrue(!port.TryPullCmd(out _), "nothing published, nothing to pull");
            port.Publish("{\"$cmd\":\"test.state\"}"u8);
            IsTrue(port.TryPullCmd(out _), "the request");
            IsTrue(!port.TryPullCmd(out _), "pulled once, until it is answered");

            var threw = false;
            try { port.Publish("{\"$cmd\":\"test.tree\"}"u8); } catch (InvalidOperationException) { threw = true; }
            IsTrue(threw, "the port thread cannot publish over a request the loop holds");

            port.Release();
            port.Publish("{\"$cmd\":\"test.tree\"}"u8);
            IsTrue(port.TryPullCmd(out var next) && next.Kind == TestCmdKind.Tree, "after release, the next request");
        });
    }
}
