using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bibui.Port;

/// <summary>
/// bibui-port's commands: each call finds the app's socket, does one thing, prints
/// the answer and exits 0, or prints the reason on stderr and exits 1.
/// </summary>
public static class PortTool
{
    public const string Usage = """
        bibui-port: drive a running bibui app through its test port.

        usage: bibui-port [--app <name> | --socket <path>] [--idle] <command> [args]

          list                          the running apps' sockets
          tree [--id <id>] [--depth N]  the UI tree as an outline; --json for the answer's JSON
          find <text>                   nodes whose id or text holds the text
          state                         the app's state (JSON)
          schema                        a JSON schema of every request the app takes
          click <id> [--right|--middle] scroll into view if hidden, then click at the centre
          hover <id>                    the same, a move without a press
          drag <id> <x> <y>             press at the node, move to x y, release
          drag <x> <y> <x> <y>          the same from a point: to select text, from its first letter to its last
          scroll <id> <steps>           wheel steps over a node; a negative one shows what is below
          move <x> <y>                  move the pointer there, in logical pixels
          type <text>                   type into what has the keyboard
          key <key> [--ctrl --shift --alt --super] [--repeat N]
                                        press and release a key (Enter, Tab, Left, A, ...)
          cmd <name> [key=value...]     an app command: {"$cmd":name, key:value...}; numbers, true,
                                        false, null and JSON values ({..} [..] "..") as such
          screenshot <path> [--id <id>] the frame on screen as a PNG the app writes; one node's rect with --id
          idle [--timeout S]            wait until nothing in the app is moving (10 s by default)

        With no --app or --socket, the only app running. --idle waits as idle does after any command.
        Coordinates are logical pixels from the window's top-left.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        //- options, anywhere; the rest is the command and its arguments
        string? app = null, id = null;
        int depth = int.MaxValue, repeat = 1;
        bool json = false, idle = false;
        double timeout = 10;
        string button = "left";
        var modifiers = new List<string>();
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string? Value() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--app" or "--socket": app = Value(); break;
                case "--id": id = Value(); break;
                case "--depth": depth = int.TryParse(Value(), out var d) && d >= 0 ? d : -1; break;
                case "--json": json = true; break;
                case "--idle": idle = true; break;
                case "--timeout": timeout = double.TryParse(Value(), CultureInfo.InvariantCulture, out var t) && t > 0 ? t : -1; break;
                case "--repeat": repeat = int.TryParse(Value(), out var r) && r > 0 ? r : -1; break;
                case "--right" or "--middle": button = args[i][2..]; break;
                case "--ctrl" or "--shift" or "--alt" or "--super": modifiers.Add(args[i][2..]); break;
                case "-h" or "--help": rest.Insert(0, "help"); break;
                default: rest.Add(args[i]); break;
            }
        }
        if (depth < 0 || repeat < 0 || timeout < 0) return Fail(error, "--depth, --repeat and --timeout take a number above 0.");
        if (rest.Count == 0 || rest[0] == "help") { output.Write(Usage); return rest.Count == 0 ? 1 : 0; }
        var (command, argv) = (rest[0], rest.GetRange(1, rest.Count - 1));
        var needs = command switch
        {
            "find" or "type" => "a text", "click" or "hover" => "an id", "drag" => "an id, x and y", "scroll" => "an id and wheel steps",
            "move" => "x and y", "key" => "a key", "cmd" => "a command's name", "screenshot" => "a path", _ => null,
        };
        if (needs is not null && argv.Count == 0) return Fail(error, $"{command} takes {needs}.");

        try
        {
            if (command == "list")
            {
                foreach (var (name, pid, path) in Port.Running()) output.WriteLine($"{name}\t{pid}\t{path}");
                return 0;
            }

            using var port = new Port(Port.SocketFor(app));
            port.Timeout = TimeSpan.FromSeconds(timeout);
            int code = Command(port, command, argv, id, depth, json, button, modifiers, repeat, output, error);
            if (code == 0 && (idle || command == "idle")) port.Idle(timeout);
            return code;
        }
        catch (PortException e) { return Fail(error, e.Message); }
        catch (SocketException e) { return Fail(error, $"The app's socket: {e.Message}"); }
        catch (IOException e) { return Fail(error, $"The app's socket: {e.Message}"); }
        catch (JsonException e) { return Fail(error, $"Not JSON: {e.Message}"); }
    }

    // One command on a connected port: its answer printed, 0; or the reason, 1.
    private static int Command(Port port, string command, List<string> argv, string? id, int depth, bool json,
        string button, List<string> modifiers, int repeat, TextWriter output, TextWriter error)
    {
        bool Number(string text, out float value) => float.TryParse(text, CultureInfo.InvariantCulture, out value);
        switch (command)
        {
            case "tree" when json:
                var answer = port.Ask("""{"$cmd":"test.tree"}""")!;
                var subtree = id is null ? answer : FindJson(answer, id) ?? throw new PortException($"No node '{id}' in the tree.");
                output.WriteLine(subtree.ToJsonString(Port.Relaxed));
                return 0;

            case "tree":
                var tree = port.Tree();
                Outline(output, id is null ? tree : Port.Find(tree, id) ?? throw new PortException($"No node '{id}' in the tree."), 0, depth);
                return 0;

            case "find":
                var found = Port.FindAll(port.Tree(), n =>
                    (n.Id?.Contains(argv[0], StringComparison.OrdinalIgnoreCase) ?? false)
                    || (n.Text?.Contains(argv[0], StringComparison.OrdinalIgnoreCase) ?? false));
                foreach (var n in found)
                {
                    // A text's nearest ancestor with an id is what a click names.
                    var owner = n.Parent;
                    while (n.Id is null && owner is not null && owner.Id is null) owner = owner.Parent;
                    output.WriteLine(n.Id is null && owner is not null ? $"{n}  in #{owner.Id}" : n.ToString());
                }
                return found.Count > 0 ? 0 : Fail(error, $"Nothing holds '{argv[0]}'.");

            case "state": output.WriteLine(port.Ask("""{"$cmd":"test.state"}""")?.ToJsonString(Port.Relaxed)); return 0;
            case "schema": output.WriteLine(port.Ask("""{"$cmd":"test.schema"}""")?.ToJsonString(Port.Relaxed)); return 0;

            case "click":
                var (x, y) = port.Click(argv[0], button).Centre;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"clicked #{argv[0]} at {x:0.#},{y:0.#}"));
                return 0;

            case "hover":
                port.Hover(argv[0]);
                return 0;

            case "drag" when argv.Count == 4 && Number(argv[0], out var fx) && Number(argv[1], out var fy):
                if (!Number(argv[2], out var tx) || !Number(argv[3], out var ty)) return Fail(error, "drag takes x y to drag from and x y to drag to.");
                port.Drag(fx, fy, tx, ty);
                return 0;

            case "drag":
                if (argv.Count < 3 || !Number(argv[1], out var dx) || !Number(argv[2], out var dy))
                    return Fail(error, "drag takes an id, and x and y to drag to.");
                port.Drag(argv[0], dx, dy);
                return 0;

            case "scroll":
                if (argv.Count < 2 || !Number(argv[1], out var steps)) return Fail(error, "scroll takes an id and wheel steps.");
                port.Scroll(argv[0], steps);
                return 0;

            case "move":
                if (argv.Count < 2 || !Number(argv[0], out var mx) || !Number(argv[1], out var my))
                    return Fail(error, "move takes x and y, in logical pixels.");
                port.Ask(new JsonObject { ["$cmd"] = "test.events", ["events"] = new JsonArray(new JsonObject { ["kind"] = "mouse_move", ["x"] = mx, ["y"] = my }) });
                return 0;

            case "type":
                port.Type(string.Join(' ', argv));
                return 0;

            case "key":
                port.Key(argv[0], modifiers, repeat);
                return 0;

            case "cmd":
                port.Ask(CmdLine(argv[0], argv.GetRange(1, argv.Count - 1)));
                return 0;

            case "screenshot":
                var file = Path.GetFullPath(argv[0]);     // the app writes it, from its own folder
                var shot = new JsonObject { ["$cmd"] = "test.screenshot", ["path"] = file };
                if (id is not null) shot["id"] = id;
                port.Ask(shot);
                output.WriteLine(file);
                return 0;

            case "idle":
                return 0;                                 // waited for after the command

            default:
                return Fail(error, $"No command '{command}': see bibui-port help.");
        }
    }

    private static int Fail(TextWriter error, string reason)
    {
        error.WriteLine(reason);
        return 1;
    }

    private static void Outline(TextWriter output, PortNode node, int level, int depth)
    {
        output.Write(new string(' ', 2 * level));
        output.WriteLine(node);
        if (level < depth)
            foreach (var child in node.Children) Outline(output, child, level + 1, depth);
    }

    private static JsonNode? FindJson(JsonNode node, string id)
    {
        if (node["id"]?.GetValue<string>() == id) return node;
        if (node["children"] is JsonArray children)
            foreach (var child in children)
                if (FindJson(child!, id) is { } found) return found;
        return null;
    }

    /// <summary>
    /// {"$cmd":name, key:value...} from key=value arguments: a number, true, false,
    /// null, or a JSON object, array or string as such; anything else a string.
    /// </summary>
    public static JsonObject CmdLine(string name, IReadOnlyList<string> pairs)
    {
        var cmd = new JsonObject { ["$cmd"] = name };
        foreach (var pair in pairs)
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) throw new PortException($"'{pair}' is not key=value.");
            var (key, value) = (pair[..eq], pair[(eq + 1)..]);
            bool asJson = value is "true" or "false" or "null"
                || (value.Length > 0 && (char.IsAsciiDigit(value[0]) || value[0] == '-') && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                || value.StartsWith('{') || value.StartsWith('[') || value.StartsWith('"');
            cmd[key] = asJson ? JsonNode.Parse(value) : JsonValue.Create(value);
        }
        return cmd;
    }
}
