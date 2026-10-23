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

        usage: bibui-port [--app <name> | --socket <path>] <command> [args]

          list                          the running apps' sockets
          tree [--id <id>] [--depth N]  the UI tree as an outline; --json for the answer's JSON
          find <text>                   nodes whose id or text holds the text
          state                         the app's state (JSON)
          schema                        a JSON schema of every request the app takes
          click <id>                    scroll into view if hidden, then click at the centre
          cmd <name> [key=value...]     an app command: {"$cmd":name, key:value...}; numbers, true,
                                        false, null and JSON values ({..} [..] "..") as such
          screenshot <path>             the frame on screen, as a PNG the app writes

        With no --app or --socket, the only app running.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        //- options, anywhere; the rest is the command and its arguments
        string? app = null, id = null;
        int depth = int.MaxValue;
        bool json = false;
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
                case "-h" or "--help": rest.Insert(0, "help"); break;
                default: rest.Add(args[i]); break;
            }
        }
        if (depth < 0) return Fail(error, "--depth takes a number.");
        if (rest.Count == 0 || rest[0] == "help") { output.Write(Usage); return rest.Count == 0 ? 1 : 0; }
        var (command, argv) = (rest[0], rest.GetRange(1, rest.Count - 1));
        var needs = command switch { "find" => "a text", "click" => "an id", "cmd" => "a command's name", "screenshot" => "a path", _ => null };
        if (needs is not null && argv.Count == 0) return Fail(error, $"{command} takes {needs}.");

        try
        {
            if (command == "list")
            {
                foreach (var (name, pid, path) in Port.Running()) output.WriteLine($"{name}\t{pid}\t{path}");
                return 0;
            }

            using var port = new Port(Port.SocketFor(app));
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
                    var (x, y) = port.Click(argv[0]).Centre;
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"clicked #{argv[0]} at {x:0.#},{y:0.#}"));
                    return 0;

                case "cmd":
                    port.Ask(CmdLine(argv[0], argv.GetRange(1, argv.Count - 1)));
                    return 0;

                case "screenshot":
                    var file = Path.GetFullPath(argv[0]);     // the app writes it, from its own folder
                    port.Ask(new JsonObject { ["$cmd"] = "test.screenshot", ["path"] = file });
                    output.WriteLine(file);
                    return 0;

                default:
                    return Fail(error, $"No command '{command}': see bibui-port help.");
            }
        }
        catch (PortException e) { return Fail(error, e.Message); }
        catch (SocketException e) { return Fail(error, $"The app's socket: {e.Message}"); }
        catch (IOException e) { return Fail(error, $"The app's socket: {e.Message}"); }
        catch (JsonException e) { return Fail(error, $"Not JSON: {e.Message}"); }
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
