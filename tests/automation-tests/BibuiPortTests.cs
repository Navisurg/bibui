using System.Text.Json.Nodes;
using Bibui.Port;
using CounterApp = TestPortSocketTests.CounterApp;

/// <summary>
/// bibui-port's commands, run in-process as the tool runs them, against a headless
/// app's port.
/// </summary>
static class BibuiPortTests
{
    static void Eq<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"{message}: expected {expected} but got {actual}");
    }

    static void IsTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    // bibui-port with these arguments: its exit code, what it printed and what it said was wrong.
    static (int Code, string Out, string Err) Tool(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        int code = PortTool.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    public static void Register(Action<string, Action> run)
    {
        run("bibui-port: a node is hidden by the innermost ancestor claiming Clip that has its centre outside it", () =>
        {
            var tree = Port.Parse(JsonNode.Parse("""
                {"kind":"box","x":0,"y":0,"w":400,"h":300,"children":[
                  {"kind":"box","id":"list","x":0,"y":0,"w":200,"h":100,"claims":["ViewScrollY","Clip"],"children":[
                    {"kind":"box","id":"top","x":0,"y":10,"w":200,"h":20,"claims":["LeftPress"]},
                    {"kind":"box","id":"deep","x":0,"y":250,"w":200,"h":20,"claims":["LeftPress"]}]}]}
                """)!);
            IsTrue(Port.Hiding(Port.Find(tree, "top")!) is null, "inside the list: in view");
            Eq("list", Port.Hiding(Port.Find(tree, "deep")!)?.Id, "below its fold: hidden by it");
        });

        run("bibui-port: list finds the app's socket, and skips one whose process is gone", () =>
        {
            using var app = new CounterApp();
            File.WriteAllText(Path.Combine(app.Folder, "gone-999999999.sock"), "");
            var running = Port.Running(app.Folder);
            Eq(1, running.Count, "one app");
            Eq(("counter", Environment.ProcessId), (running[0].Name, running[0].Pid), "named for the app and its process");
        });

        run("bibui-port: click, cmd, state, tree, find and screenshot", () =>
        {
            using var app = new CounterApp(list: true);
            var socket = app.Port.SocketPath!;

            var click = Tool("--socket", socket, "click", "add");
            Eq((0, ""), (click.Code, click.Err), "click");
            IsTrue(click.Out.StartsWith("clicked #add at "), $"says where: {click.Out}");
            Eq("""{"Count":1}""", Tool("--socket", socket, "state").Out.Trim(), "the click ran before the answer came");
            Eq(0, Tool("--socket", socket, "cmd", "counter.set", "Value=10").Code, "cmd");
            Eq("""{"Count":10}""", Tool("--socket", socket, "state").Out.Trim(), "and its effect");

            var tree = Tool("--socket", socket, "tree", "--id", "list", "--depth", "1").Out;
            IsTrue(tree.StartsWith("box #list [") && tree.Contains("ViewScrollY ClampY Clip") && tree.Contains("\n  box #row-0 "), $"one node's outline, with its claims: {tree[..Math.Min(300, tree.Length)]}");
            IsTrue(!tree.Contains("\"Row 0\""), "no deeper than asked");
            IsTrue(Tool("--socket", socket, "tree", "--json", "--id", "add").Out.StartsWith("""{"kind":"box","id":"add","""), "one node as JSON");
            IsTrue(Tool("--socket", socket, "find", "Add one").Out.Contains("in #add"), "find names what to click for a text");

            var png = Path.Combine(app.Folder, "tool.png");
            Eq(0, Tool("--socket", socket, "screenshot", png).Code, "screenshot");
            IsTrue(File.Exists(png), "written by the app");
        });

        run("bibui-port click: a button below a list's fold is scrolled into view, then clicked", () =>
        {
            using var app = new CounterApp(list: true);
            var socket = app.Port.SocketPath!;
            using (var port = new Port(socket))
                IsTrue(Port.Hiding(Port.Find(port.Tree(), "row-25")!) is not null, "out of view to begin with");
            Eq(0, Tool("--socket", socket, "click", "row-25").Code, "clicked");
            Eq("""{"Count":125}""", Tool("--socket", socket, "state").Out.Trim(), "the row's command ran");
            Eq(0, Tool("--socket", socket, "click", "row-0").Code, "and back up");
            Eq("""{"Count":100}""", Tool("--socket", socket, "state").Out.Trim(), "the first row's");
        });

        run("bibui-port: an unknown id and a refused command exit 1, with the reason", () =>
        {
            using var app = new CounterApp();
            var socket = app.Port.SocketPath!;
            var unknown = Tool("--socket", socket, "click", "nope");
            Eq((1, true), (unknown.Code, unknown.Err.Contains("No node 'nope'")), "an unknown id");
            var refused = Tool("--socket", socket, "cmd", "counter.nope");
            Eq((1, true), (refused.Code, refused.Err.Contains("counter.nope")), "a refused command");
            Eq((1, true), (Tool("click").Code, Tool("click").Err.Contains("takes an id")), "a command missing its argument");
        });

        run("bibui-port cmd: key=value arguments as JSON values", () =>
        {
            Eq("""{"$cmd":"a.b","n":-1.5,"t":true,"s":"x y","o":{"k":[1]},"q":"10"}""",
               PortTool.CmdLine("a.b", ["n=-1.5", "t=true", "s=x y", """o={"k":[1]}""", "q=\"10\""]).ToJsonString(Port.Relaxed),
               "numbers, booleans, strings and JSON");
        });
    }
}
